using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DiscUtils.Udf;
using HarmonyLib;
using MediaInfoKeeper.Patch.MediaInfo.Bluray;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using ModelMediaInfo = MediaBrowser.Model.MediaInfo.MediaInfo;

namespace MediaInfoKeeper.Patch
{
    /*
     * ISO 探测流程：
     * 1. IsoProbeSupport 放开 Emby 对 .iso 的 IsSupported 拦截，让 MediaProbeManager 继续走 ffprobe 探测。
     *    .strm 在 Emby MediaSourceManager 中已经被解析成文件内容里的目标路径；这里接管的是解析后的 .iso。
     * 2. IsoProbeInput 在 GetMediaInfo 前改写 MediaSource.ProbePath，不直接把 ISO 交给 ffprobe，也不使用 bluray: 或旧头部缓存。
     * 3. IsoDiscProbeInput 用可 seek 的源流打开 ISO：本地 ISO 直接 FileStream，HTTP ISO 使用 Range 流按需读取。
     * 4. DiscUtils.Udf 读取 BDMV/PLAYLIST/*.mpls，选主 playlist，按 MPLS 里的 clip 顺序定位 BDMV/STREAM/*.m2ts。
     * 5. 依次从这些 clip 抽取前缀字节，拼成一个临时 .m2ts，交给 ffprobe 识别真实音视频流。
     * 6. ffprobe 返回后，用原始 ISO 覆盖总大小、用主 playlist 覆盖时长，并用 MPLS 补齐音轨/字幕语言和章节。
     *    音频 codec、声道、采样率、位深等技术参数保留 ffprobe 结果；TrueHD 内嵌 AC3 会从展示流列表中过滤。
     * 7. 对 ISO 双层 HEVC 结果做杜比视界兜底判断，用于补足 ffprobe 前缀探测可能缺失的 Profile 7 标记。
     */
    internal static class IsoFfprobeRouting
    {
        public static bool LooksLikeIsoPath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);
        }

    }

    /// <summary>
    /// 为 ISO 探测放开 Emby 原版的 IsSupported 硬拦截，让 ProbePath 改写逻辑接管真实探测流程。
    /// </summary>
    public static class IsoProbeSupport
    {
        private static Harmony harmony;
        private static MethodInfo isSupportedMethod;
        private static ILogger logger;

        public static bool IsReady => harmony != null && isSupportedMethod != null;

        public static void Initialize(ILogger pluginLogger, bool enabled)
        {
            if (harmony != null)
            {
                return;
            }

            logger = pluginLogger;
            if (!enabled)
            {
                return;
            }

            try
            {
                var embyProvidersAssembly = Assembly.Load("Emby.Providers");
                var mediaBrowserModelAssembly = Assembly.Load("MediaBrowser.Model");
                var ffProbeVideoInfoType = embyProvidersAssembly?.GetType("Emby.Providers.MediaInfo.FFProbeVideoInfo");
                var mediaProtocolType = mediaBrowserModelAssembly?.GetType("MediaBrowser.Model.MediaInfo.MediaProtocol");

                if (ffProbeVideoInfoType == null || mediaProtocolType == null)
                {
                    PatchLog.InitFailed(logger, nameof(IsoProbeSupport), "关键运行时类型缺失");
                    return;
                }

                isSupportedMethod = PatchMethodResolver.Resolve(
                    ffProbeVideoInfoType,
                    embyProvidersAssembly.GetName().Version,
                    new MethodSignatureProfile
                    {
                        Name = "ffprobevideoinfo-issupported-exact",
                        MethodName = "IsSupported",
                        BindingFlags = BindingFlags.Static | BindingFlags.Public,
                        ParameterTypes = new[] { typeof(string), mediaProtocolType },
                        ReturnType = typeof(bool)
                    },
                    logger,
                    "IsoProbeSupport.FFProbeVideoInfo.IsSupported");

                if (isSupportedMethod == null)
                {
                    PatchLog.InitFailed(logger, nameof(IsoProbeSupport), "未命中 IsSupported");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.iso-probe-support");
                PatchLog.Patched(logger, nameof(IsoProbeSupport), isSupportedMethod);
                harmony.Patch(
                    isSupportedMethod,
                    postfix: new HarmonyMethod(typeof(IsoProbeSupport), nameof(IsSupportedPostfix)));
            }
            catch (Exception ex)
            {
                PatchLog.InitFailed(logger, nameof(IsoProbeSupport), ex.Message);
                logger?.Error("IsoProbeSupport 初始化异常：{0}", ex);
                harmony = null;
            }
        }

        public static void Configure(bool enabled)
        {
            // 一次性安装，运行期直接读取配置。
        }

        private static void IsSupportedPostfix(string __0, ref bool __result)
        {
            if (__result)
            {
                return;
            }

            if (!IsoFfprobeRouting.LooksLikeIsoPath(__0))
            {
                return;
            }

            __result = true;
        }
    }

    /// <summary>
    /// 当媒体源实际指向 ISO 时，把 probe 输入改写为按 MPLS clip 顺序抽取出的临时 m2ts。
    /// </summary>
    public static class IsoProbeInput
    {
        private static Harmony harmony;
        private static MethodInfo getMediaInfoMethod;
        private static ILogger logger;
        private static Type mediaInfoType;
        private static readonly ConditionalWeakTable<object, StrongBox<string>> tempProbeInputs = new ConditionalWeakTable<object, StrongBox<string>>();

        public static bool IsReady => harmony != null && getMediaInfoMethod != null;

        public static void Initialize(ILogger pluginLogger, bool enabled)
        {
            if (harmony != null)
            {
                return;
            }

            logger = pluginLogger;
            if (!enabled)
            {
                return;
            }

            try
            {
                var mediaEncodingAssembly = Assembly.Load("Emby.Server.MediaEncoding");
                var controllerAssembly = Assembly.Load("MediaBrowser.Controller");
                var mediaProbeManagerType = mediaEncodingAssembly?.GetType("Emby.Server.MediaEncoding.Probing.MediaProbeManager");
                var mediaInfoRequestType = controllerAssembly?.GetType("MediaBrowser.Controller.MediaEncoding.MediaInfoRequest");
                mediaInfoType = Assembly.Load("MediaBrowser.Model")?.GetType("MediaBrowser.Model.MediaInfo.MediaInfo");

                if (mediaProbeManagerType == null || mediaInfoRequestType == null || mediaInfoType == null)
                {
                    PatchLog.InitFailed(logger, nameof(IsoProbeInput), "关键运行时类型缺失");
                    return;
                }

                getMediaInfoMethod = PatchMethodResolver.Resolve(
                    mediaProbeManagerType,
                    mediaEncodingAssembly.GetName().Version,
                    new MethodSignatureProfile
                    {
                        Name = "mediaprobemanager-getmediainfo-probe-input-exact",
                        MethodName = "GetMediaInfo",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        ParameterTypes = new[] { mediaInfoRequestType, typeof(CancellationToken) }
                    },
                    logger,
                    "IsoProbeInput.MediaProbeManager.GetMediaInfo");

                if (getMediaInfoMethod == null)
                {
                    PatchLog.InitFailed(logger, nameof(IsoProbeInput), "未命中 GetMediaInfo");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.iso-probe-input");
                PatchLog.Patched(logger, nameof(IsoProbeInput), getMediaInfoMethod);
                harmony.Patch(
                    getMediaInfoMethod,
                    prefix: new HarmonyMethod(typeof(IsoProbeInput), nameof(GetMediaInfoPrefix)),
                    postfix: new HarmonyMethod(typeof(IsoProbeInput), nameof(GetMediaInfoPostfix)));
            }
            catch (Exception ex)
            {
                PatchLog.InitFailed(logger, nameof(IsoProbeInput), ex.Message);
                logger?.Error("IsoProbeInput 初始化异常：{0}", ex);
                harmony = null;
            }
        }

        public static void Configure(bool enabled)
        {
            // 一次性安装，运行期直接读取配置。
        }

        private static void GetMediaInfoPrefix(object __instance, object __0, CancellationToken __1)
        {
            if (__instance == null || __0 == null)
            {
                return;
            }

            try
            {
                var mediaSource = GetRequestMediaSource(__0);
                if (mediaSource == null)
                {
                    return;
                }

                var path = ResolveMediaSourceIsoPath(mediaSource);
                if (!IsoFfprobeRouting.LooksLikeIsoPath(path))
                {
                    return;
                }

                // 这里不使用 bluray: 挂载式输入，也不缓存 ISO 头部；本地 ISO 和 strm/http ISO 都转换成临时 m2ts。
                var remappedPath = IsoDiscProbeInput.PrepareProbeInput(
                    path,
                    log: message => logger?.Warn(message));
                if (string.IsNullOrWhiteSpace(remappedPath))
                {
                    logger?.Warn("ISO probe 输入改写失败：未生成临时 m2ts path={0}", path);
                    return;
                }

                if (string.Equals(remappedPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                SetProperty(mediaSource, "ProbePath", remappedPath);
                SetProperty(mediaSource, "ProbeProtocol", MediaProtocol.File);
                tempProbeInputs.Remove(__0);
                tempProbeInputs.Add(__0, new StrongBox<string>(remappedPath));

                logger?.Debug("ISO probe 输入改写：source={0} probePath={1}", path, remappedPath);
            }
            catch (Exception ex)
            {
                logger?.Error("ISO probe 输入改写异常：{0}", ex);
            }
        }

        private static void GetMediaInfoPostfix(object __0, ref object __result)
        {
            if (__0 == null || __result == null || mediaInfoType == null)
            {
                return;
            }

            var expectedTaskType = typeof(Task<>).MakeGenericType(mediaInfoType);
            if (!expectedTaskType.IsInstanceOfType(__result))
            {
                return;
            }

            var wrapMethod = typeof(IsoProbeInput).GetMethod(nameof(WrapTask), BindingFlags.Static | BindingFlags.NonPublic);
            var genericWrapMethod = wrapMethod?.MakeGenericMethod(mediaInfoType);
            if (genericWrapMethod == null)
            {
                return;
            }

            __result = genericWrapMethod.Invoke(null, new[] { __result, __0 });
        }

        private static async Task<T> WrapTask<T>(Task<T> task, object request)
        {
            try
            {
                var result = await task.ConfigureAwait(false);

                try
                {
                    if (result is ModelMediaInfo mediaInfo)
                    {
                        ApplyBlurayPlaylistMetadata(mediaInfo, request);
                        ApplyDolbyVisionFallback(mediaInfo, request);
                    }
                }
                catch (Exception ex)
                {
                    logger?.Error("ISO probe 结果补全异常：{0}", ex);
                }

                return result;
            }
            finally
            {
                CleanupTempProbeInput(request);
            }
        }

        private static void ApplyBlurayPlaylistMetadata(ModelMediaInfo mediaInfo, object request)
        {
            if (mediaInfo == null)
            {
                return;
            }

            var sourcePath = GetPreferredDiscSourcePath(mediaInfo, request);
            if (!IsoFfprobeRouting.LooksLikeIsoPath(sourcePath))
            {
                return;
            }

            try
            {
                using var discInfo = IsoDiscProbeInput.ReadDiscInfo(
                    sourcePath,
                    log: message => logger?.Warn(message));
                if (discInfo?.Playlist == null)
                {
                    return;
                }

                ApplyDiscSourceMetadata(mediaInfo, discInfo);
                ApplyDiscStreamMetadata(mediaInfo, discInfo.Playlist);
                ApplyDiscChapters(mediaInfo, discInfo.Playlist);
            }
            catch (Exception ex)
            {
                logger?.Warn("ISO 播放列表元数据补全失败：path={0}, error={1}", sourcePath ?? "<null>", ex.Message);
            }
        }

        private static void ApplyDiscSourceMetadata(ModelMediaInfo mediaInfo, IsoDiscProbeInput.IsoDiscInfo discInfo)
        {
            if (mediaInfo == null || discInfo?.Playlist == null)
            {
                return;
            }

            if (discInfo.Playlist.TotalLengthSeconds > 0)
            {
                mediaInfo.RunTimeTicks = TimeSpan.FromSeconds(discInfo.Playlist.TotalLengthSeconds).Ticks;
            }

            if (discInfo.SourceLength.HasValue && discInfo.SourceLength.Value > 0)
            {
                mediaInfo.Size = discInfo.SourceLength.Value;
            }

        }

        private static void ApplyDiscStreamMetadata(ModelMediaInfo mediaInfo, BlurayPlaylistInfo playlistInfo)
        {
            if (mediaInfo == null || playlistInfo?.Streams == null || playlistInfo.Streams.Count == 0)
            {
                return;
            }

            if (mediaInfo.MediaStreams == null)
            {
                mediaInfo.MediaStreams = new List<MediaStream>();
            }

            var currentAudios = mediaInfo.MediaStreams.Where(stream => stream?.Type == MediaStreamType.Audio).ToList();
            var currentSubtitles = mediaInfo.MediaStreams.Where(stream => stream?.Type == MediaStreamType.Subtitle).ToList();
            var discAudios = playlistInfo.Streams.Where(stream => stream.IsAudio).ToList();
            var discSubtitles = playlistInfo.Streams.Where(stream => stream.IsSubtitle).ToList();

            var removed = RemoveExpandedCompatibilityAudio(mediaInfo.MediaStreams, currentAudios, discAudios);
            if (removed > 0)
            {
                currentAudios = mediaInfo.MediaStreams.Where(stream => stream?.Type == MediaStreamType.Audio).ToList();
                ReindexStreams(mediaInfo.MediaStreams);
            }

            var changed = ApplyDiscStreamsByOrder(currentAudios, discAudios, MediaStreamType.Audio);
            changed += ApplyDiscStreamsByOrder(currentSubtitles, discSubtitles, MediaStreamType.Subtitle);

            var added = 0;
            added += AppendMissingStreams(mediaInfo.MediaStreams, currentAudios.Count, discAudios, MediaStreamType.Audio);
            added += AppendMissingStreams(mediaInfo.MediaStreams, currentSubtitles.Count, discSubtitles, MediaStreamType.Subtitle);

            if (added > 0)
            {
                ReindexStreams(mediaInfo.MediaStreams);
            }

            if (changed > 0 || added > 0 || removed > 0)
            {
                logger?.Debug(
                    "ISO playlist 流元数据补全：changed={0} added={1} removed={2} audio={3}/{4} subtitle={5}/{6}",
                    changed,
                    added,
                    removed,
                    Math.Max(currentAudios.Count, discAudios.Count),
                    discAudios.Count,
                    Math.Max(currentSubtitles.Count, discSubtitles.Count),
                    discSubtitles.Count);
            }
        }

        private static int ApplyDiscStreamsByOrder(
            List<MediaStream> currentStreams,
            List<BlurayPlaylistStream> discStreams,
            MediaStreamType streamType)
        {
            if (currentStreams == null || discStreams == null)
            {
                return 0;
            }

            var changed = 0;
            var limit = Math.Min(currentStreams.Count, discStreams.Count);
            for (var i = 0; i < limit; i++)
            {
                changed += ApplyDiscStreamMetadata(currentStreams[i], discStreams[i], streamType);
            }

            return changed;
        }

        private static int RemoveExpandedCompatibilityAudio(
            List<MediaStream> mediaStreams,
            List<MediaStream> currentAudios,
            List<BlurayPlaylistStream> discAudios)
        {
            if (mediaStreams == null || currentAudios == null || discAudios == null)
            {
                return 0;
            }

            var removed = 0;
            var currentIndex = 0;
            for (var discIndex = 0; discIndex < discAudios.Count && currentIndex < currentAudios.Count; discIndex++)
            {
                var discStream = discAudios[discIndex];
                if (HasExpandedCompatibilityAudio(currentAudios, currentIndex, discStream))
                {
                    var compatibilityStream = currentAudios[currentIndex + 1];
                    mediaStreams.Remove(compatibilityStream);
                    currentAudios.RemoveAt(currentIndex + 1);
                    removed++;

                }

                currentIndex++;
            }

            return removed;
        }

        private static bool HasExpandedCompatibilityAudio(
            List<MediaStream> currentAudios,
            int currentIndex,
            BlurayPlaylistStream discStream)
        {
            if (currentAudios == null ||
                discStream == null ||
                currentIndex + 1 >= currentAudios.Count)
            {
                return false;
            }

            var currentCodec = NormalizeCodecName(currentAudios[currentIndex]?.Codec);
            var nextCodec = NormalizeCodecName(currentAudios[currentIndex + 1]?.Codec);
            var discCodec = NormalizeCodecName(discStream.Codec);

            return string.Equals(discCodec, "truehd", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(currentCodec, "truehd", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(nextCodec, "ac3", StringComparison.OrdinalIgnoreCase);
        }

        private static int AppendMissingStreams(
            List<MediaStream> mediaStreams,
            int existingCount,
            List<BlurayPlaylistStream> discStreams,
            MediaStreamType streamType)
        {
            if (mediaStreams == null || discStreams == null || existingCount >= discStreams.Count)
            {
                return 0;
            }

            var added = 0;
            for (var i = existingCount; i < discStreams.Count; i++)
            {
                var discStream = discStreams[i];
                if (discStream == null)
                {
                    continue;
                }

                mediaStreams.Add(CreateDiscMediaStream(discStream, streamType));
                added++;
            }

            return added;
        }

        private static MediaStream CreateDiscMediaStream(BlurayPlaylistStream discStream, MediaStreamType streamType)
        {
            var codec = NormalizeDiscCodec(discStream?.Codec, streamType);
            var language = string.IsNullOrWhiteSpace(discStream?.LanguageCode) ? null : discStream.LanguageCode;

            return new MediaStream
            {
                Type = streamType,
                Codec = codec,
                Language = language,
                IsDefault = false,
                IsForced = false,
                IsExternal = false,
                Protocol = MediaProtocol.File,
                SupportsExternalStream = false
            };
        }

        private static int ApplyDiscStreamMetadata(
            MediaStream mediaStream,
            BlurayPlaylistStream discStream,
            MediaStreamType streamType)
        {
            if (mediaStream == null || discStream == null)
            {
                return 0;
            }

            var changed = 0;
            var language = string.IsNullOrWhiteSpace(discStream.LanguageCode) ? null : discStream.LanguageCode;

            if (!string.Equals(mediaStream.Language, language, StringComparison.OrdinalIgnoreCase))
            {
                mediaStream.Language = language;
                changed++;
            }

            mediaStream.Type = streamType;
            mediaStream.IsExternal = false;
            mediaStream.Protocol = MediaProtocol.File;
            mediaStream.SupportsExternalStream = false;
            return changed;
        }

        private static string NormalizeCodecName(string codec)
        {
            if (string.IsNullOrWhiteSpace(codec))
            {
                return null;
            }

            return codec.Trim().Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant() switch
            {
                "mlptruehd" => "truehd",
                "dolbytruehd" => "truehd",
                "truehd" => "truehd",
                "ac3" => "ac3",
                "dolbydigital" => "ac3",
                "eac3" => "eac3",
                "dts" => "dts",
                "dtshd" => "dtshd",
                "dtshdma" => "dtshd_ma",
                _ => codec
            };
        }

        private static string NormalizeDiscCodec(string codec, MediaStreamType streamType)
        {
            if (streamType == MediaStreamType.Subtitle)
            {
                if (string.Equals(codec, "pgs", StringComparison.OrdinalIgnoreCase))
                {
                    return "PGSSUB";
                }

                return string.IsNullOrWhiteSpace(codec) ? "PGSSUB" : codec;
            }

            return string.IsNullOrWhiteSpace(codec) ? "unknown" : codec;
        }

        private static void ReindexStreams(List<MediaStream> mediaStreams)
        {
            if (mediaStreams == null)
            {
                return;
            }

            for (var i = 0; i < mediaStreams.Count; i++)
            {
                if (mediaStreams[i] != null)
                {
                    mediaStreams[i].Index = i;
                }
            }
        }

        private static void ApplyDiscChapters(ModelMediaInfo mediaInfo, BlurayPlaylistInfo playlistInfo)
        {
            if (mediaInfo.Chapters != null && mediaInfo.Chapters.Length > 0)
            {
                return;
            }

            var chapters = playlistInfo?.Chapters;
            if (chapters == null || chapters.Count == 0)
            {
                return;
            }

            mediaInfo.Chapters = chapters
                .Select((seconds, index) => new ChapterInfo
                {
                    StartPositionTicks = TimeSpan.FromSeconds(seconds).Ticks,
                    Name = "Chapter " + (index + 1),
                    ChapterIndex = index
                })
                .ToArray();

        }

        private static void ApplyDolbyVisionFallback(ModelMediaInfo mediaInfo, object request)
        {
            if (mediaInfo == null)
            {
                return;
            }

            var sourcePath = GetPreferredDiscSourcePath(mediaInfo, request);
            if (!IsoFfprobeRouting.LooksLikeIsoPath(sourcePath))
            {
                return;
            }

            var videoStreams = (mediaInfo.MediaStreams ?? new List<MediaStream>())
                .Where(stream => stream != null && stream.Type == MediaStreamType.Video)
                .OrderByDescending(stream => stream.Width ?? 0)
                .ThenByDescending(stream => stream.Height ?? 0)
                .ThenBy(stream => stream.Index)
                .ToList();

            if (videoStreams.Count == 0)
            {
                return;
            }

            var mainVideo = videoStreams[0];
            if (mainVideo.ExtendedVideoType == ExtendedVideoTypes.DolbyVision)
            {
                return;
            }

            if (!LooksLikeHdrBase(mainVideo))
            {
                return;
            }

            var inferredSubtype = GetDolbyVisionSubtype(videoStreams, out var reason);
            if (!inferredSubtype.HasValue)
            {
                return;
            }

            mainVideo.ExtendedVideoType = ExtendedVideoTypes.DolbyVision;
            mainVideo.ExtendedVideoSubType = inferredSubtype.Value;

            logger?.Debug("ISO probe 杜比配置补判：subtype={0} reason={1}", inferredSubtype.Value, reason ?? "<null>");
        }

        private static ExtendedVideoSubTypes? GetDolbyVisionSubtype(List<MediaStream> videoStreams, out string reason)
        {
            reason = null;

            var hasEnhancementLayer = videoStreams.Count > 1 &&
                                      videoStreams.Skip(1).Any(IsEnhancementLayerCandidate);

            if (hasEnhancementLayer)
            {
                reason = "iso dual-layer enhancement layer";
                return ExtendedVideoSubTypes.DoviProfile76;
            }

            return null;
        }

        private static bool IsEnhancementLayerCandidate(MediaStream stream)
        {
            if (stream == null || stream.Type != MediaStreamType.Video)
            {
                return false;
            }

            var codec = stream.Codec ?? string.Empty;
            if (!codec.Equals("hevc", StringComparison.OrdinalIgnoreCase) &&
                !codec.Equals("h265", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var width = stream.Width ?? 0;
            var height = stream.Height ?? 0;
            return width >= 1280 && width <= 2048 && height >= 720 && height <= 1200;
        }

        private static bool LooksLikeHdrBase(MediaStream stream)
        {
            if (stream == null)
            {
                return false;
            }

            if (stream.ExtendedVideoType == ExtendedVideoTypes.Hdr10 ||
                stream.ExtendedVideoType == ExtendedVideoTypes.Hdr10Plus ||
                stream.ExtendedVideoType == ExtendedVideoTypes.HyperLogGamma)
            {
                return true;
            }

            if (string.Equals(stream.ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(stream.ColorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static string GetPreferredDiscSourcePath(ModelMediaInfo mediaInfo, object request)
        {
            var mediaSource = GetRequestMediaSource(request);
            if (mediaSource != null)
            {
                var requestIsoPath = ResolveMediaSourceIsoPath(mediaSource);
                if (IsoFfprobeRouting.LooksLikeIsoPath(requestIsoPath))
                {
                    return requestIsoPath;
                }
            }

            return mediaInfo?.Path;
        }

        private static void CleanupTempProbeInput(object request)
        {
            if (request == null || !tempProbeInputs.TryGetValue(request, out var probeInput))
            {
                return;
            }

            tempProbeInputs.Remove(request);
            try
            {
                if (File.Exists(probeInput.Value))
                {
                    File.Delete(probeInput.Value);
                }
            }
            catch (Exception ex)
            {
                logger?.Warn("ISO probe 临时输入清理失败：input={0} error={1}", probeInput.Value, ex.Message);
            }
        }

        private static string ResolveMediaSourceIsoPath(object mediaSource)
        {
            if (mediaSource == null)
            {
                return null;
            }

            var path = GetStringProperty(mediaSource, "Path");
            if (IsoFfprobeRouting.LooksLikeIsoPath(path))
            {
                return path;
            }

            var probePath = GetStringProperty(mediaSource, "ProbePath");
            if (IsoFfprobeRouting.LooksLikeIsoPath(probePath))
            {
                return probePath;
            }

            return null;
        }

        private static object GetRequestMediaSource(object request)
        {
            return request?.GetType()
                .GetProperty("MediaSource", BindingFlags.Instance | BindingFlags.Public)?
                .GetValue(request);
        }

        private static string GetStringProperty(object instance, string propertyName)
        {
            return instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(instance) as string;
        }

        private static void SetProperty(object instance, string propertyName, object value)
        {
            instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.SetValue(instance, value);
        }

    }

    internal static class IsoDiscProbeInput
    {
        internal const int DefaultProbeBytes = 5_000_000;
        private const string BrowserUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36";
        private const int RangeCacheBlockSize = 256 * 1024;
        public static string PrepareProbeInput(string path, Action<string> log = null)
        {
            if (!IsoFfprobeRouting.LooksLikeIsoPath(path))
            {
                return path;
            }

            try
            {
                return PrepareProbeInputAsync(path, DefaultProbeBytes, CancellationToken.None, log)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                log?.Invoke($"ISO playlist 探测输入生成失败 path={path} error={ex.Message}");
                return path;
            }
        }

        public static async Task<string> PrepareProbeInputAsync(
            string path,
            int probeBytes,
            CancellationToken cancellationToken,
            Action<string> log = null)
        {
            if (!IsoFfprobeRouting.LooksLikeIsoPath(path))
            {
                return path;
            }

            if (probeBytes <= 0)
            {
                probeBytes = DefaultProbeBytes;
            }

            using var discInfo = await ReadDiscInfoAsync(path, cancellationToken, log).ConfigureAwait(false);
            if (discInfo?.Playlist == null)
            {
                return path;
            }

            var targetClips = discInfo.ClipPaths;
            if (targetClips.Count == 0)
            {
                log?.Invoke($"ISO playlist 探测输入跳过：未找到主 clip path={path} playlist={discInfo.Playlist?.PlaylistName ?? "<null>"}");
                return path;
            }

            var probeTempDirectory = GetProbeTempDirectory();
            Directory.CreateDirectory(probeTempDirectory);
            var probePath = Path.Combine(probeTempDirectory, Guid.NewGuid().ToString("N") + ".m2ts");
            await using var output = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var copied = 0L;
            foreach (var targetClip in targetClips)
            {
                // 按 playlist 顺序拼接 clip 前缀，避免只读 ISO 文件头导致 ffprobe 看不到真实 m2ts 流。
                var remaining = probeBytes - (int)copied;
                if (remaining <= 0)
                {
                    break;
                }

                await using var input = discInfo.FileSystem.OpenFile(targetClip, FileMode.Open, FileAccess.Read);
                copied += await CopyUpToAsync(input, output, remaining, cancellationToken).ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);

            return probePath;
        }

        private static string GetProbeTempDirectory()
        {
            return Path.Combine(GetEmbyCachePath(), Plugin.PluginName, "iso-playlist-probe");
        }

        private static string GetEmbyCachePath()
        {
            var cachePath = Plugin.Instance?.AppHost?.Resolve<IApplicationPaths>()?.CachePath;
            if (string.IsNullOrWhiteSpace(cachePath))
            {
                throw new InvalidOperationException("Emby CachePath is not available.");
            }

            return cachePath;
        }

        public static IsoDiscInfo ReadDiscInfo(string path, Action<string> log = null)
        {
            if (!IsoFfprobeRouting.LooksLikeIsoPath(path))
            {
                return null;
            }

            try
            {
                return ReadDiscInfoAsync(path, CancellationToken.None, log)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                log?.Invoke($"ISO playlist 元数据读取失败 path={path} error={ex.Message}");
                return null;
            }
        }

        private static async Task<IsoDiscInfo> ReadDiscInfoAsync(
            string path,
            CancellationToken cancellationToken,
            Action<string> log = null)
        {
            var sourceStream = await OpenSourceStreamAsync(path, cancellationToken).ConfigureAwait(false);
            if (!OpenUdf(sourceStream, out var udf))
            {
                log?.Invoke($"ISO playlist 元数据读取跳过：UDF 打开失败 path={path}");
                sourceStream.Dispose();
                return null;
            }

            var sourceLength = TryGetStreamLength(sourceStream);
            var playlist = BlurayStructureReader.ReadMainPlaylistFromFileSystem(udf);
            var clipPaths = BlurayStructureReader.ResolvePlaylistClipPaths(udf, playlist);

            return new IsoDiscInfo(path, sourceLength, sourceStream, udf, playlist, clipPaths);
        }

        private static long? TryGetStreamLength(Stream stream)
        {
            try
            {
                return stream?.CanSeek == true ? stream.Length : null;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<Stream> OpenSourceStreamAsync(
            string path,
            CancellationToken cancellationToken)
        {
            if (!IsHttpIsoPath(path))
            {
                return File.OpenRead(path);
            }

            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            };
            var client = new HttpClient(handler, disposeHandler: true);
            ApplyDefaultHeaders(client);
            await Task.CompletedTask.ConfigureAwait(false);
            return new HttpRangeStream(client, handler, new Uri(path));
        }

        internal sealed class IsoDiscInfo : IDisposable
        {
            private readonly Stream sourceStream;
            private bool disposed;

            public IsoDiscInfo(
                string path,
                long? sourceLength,
                Stream sourceStream,
                dynamic fileSystem,
                BlurayPlaylistInfo playlist,
                List<string> clipPaths)
            {
                Path = path;
                SourceLength = sourceLength;
                this.sourceStream = sourceStream;
                FileSystem = fileSystem;
                Playlist = playlist;
                ClipPaths = clipPaths ?? new List<string>();
            }

            public string Path { get; }

            public long? SourceLength { get; }

            public dynamic FileSystem { get; }

            public BlurayPlaylistInfo Playlist { get; }

            public List<string> ClipPaths { get; }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                try
                {
                    FileSystem?.Dispose();
                }
                finally
                {
                    sourceStream?.Dispose();
                    disposed = true;
                }
            }
        }

        private static bool IsHttpIsoPath(string path)
        {
            if (!IsoFfprobeRouting.LooksLikeIsoPath(path))
            {
                return false;
            }

            return path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        private static void ApplyDefaultHeaders(HttpClient client)
        {
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        }

        private static bool OpenUdf(Stream stream, out UdfReader udf)
        {
            udf = null;
            try
            {
                if (stream == null || !stream.CanSeek)
                {
                    return false;
                }

                stream.Position = 0;
                if (!UdfReader.Detect(stream))
                {
                    stream.Position = 0;
                    return false;
                }

                stream.Position = 0;
                udf = new UdfReader(stream, 2048);
                return true;
            }
            catch
            {
                if (stream?.CanSeek == true)
                {
                    stream.Position = 0;
                }

                udf = null;
                return false;
            }
        }

        private static async Task<long> CopyUpToAsync(Stream input, Stream output, int byteLimit, CancellationToken cancellationToken)
        {
            var buffer = new byte[64 * 1024];
            var remaining = byteLimit;
            long total = 0;

            while (remaining > 0)
            {
                var toRead = Math.Min(buffer.Length, remaining);
                var read = await input.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
                total += read;
            }

            return total;
        }

        private sealed class HttpRangeStream : Stream
        {
            private readonly HttpClient httpClient;
            private readonly HttpClientHandler handler;
            private readonly Uri uri;
            private readonly Dictionary<long, byte[]> blockCache = new Dictionary<long, byte[]>();
            private long position;
            private long? length;
            private bool disposed;

            public HttpRangeStream(HttpClient httpClient, HttpClientHandler handler, Uri uri)
            {
                this.httpClient = httpClient;
                this.handler = handler;
                this.uri = uri;
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => length ??= FetchLength();

            public override long Position
            {
                get => position;
                set => position = value;
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (count <= 0)
                {
                    return 0;
                }

                var total = 0;
                while (total < count && position < Length)
                {
                    var blockStart = (position / RangeCacheBlockSize) * RangeCacheBlockSize;
                    var block = GetOrFetchBlock(blockStart);
                    if (block == null || block.Length == 0)
                    {
                        break;
                    }

                    var blockOffset = (int)(position - blockStart);
                    if (blockOffset >= block.Length)
                    {
                        break;
                    }

                    var available = Math.Min(block.Length - blockOffset, count - total);
                    Buffer.BlockCopy(block, blockOffset, buffer, offset + total, available);
                    var read = available;
                    total += read;
                    position += read;
                }

                return total;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                position = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => position + offset,
                    SeekOrigin.End => Length + offset,
                    _ => throw new ArgumentOutOfRangeException(nameof(origin))
                };
                return position;
            }

            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (!disposed)
                {
                    if (disposing)
                    {
                        httpClient.Dispose();
                        handler.Dispose();
                    }

                    disposed = true;
                }

                base.Dispose(disposing);
            }

            private long FetchLength()
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Range = new RangeHeaderValue(0, 0);

                using var response = httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                if (response.Content.Headers.ContentRange?.Length is long contentLength)
                {
                    return contentLength;
                }

                return response.Content.Headers.ContentLength
                    ?? throw new InvalidOperationException("HTTP ISO 响应缺少 Content-Length");
            }

            private byte[] GetOrFetchBlock(long blockStart)
            {
                if (blockCache.TryGetValue(blockStart, out var cached))
                {
                    return cached;
                }

                var end = Math.Min(blockStart + RangeCacheBlockSize - 1L, Length - 1L);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Range = new RangeHeaderValue(blockStart, end);

                using var response = httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                using var stream = response.Content.ReadAsStream();
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var bytes = ms.ToArray();
                blockCache[blockStart] = bytes;
                return bytes;
            }
        }
    }
}
