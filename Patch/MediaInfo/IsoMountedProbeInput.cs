using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.IO;
using HarmonyLib;
using MediaInfoKeeper.Patch.MediaInfo.Bluray;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using ModelMediaInfo = MediaBrowser.Model.MediaInfo.MediaInfo;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 当媒体源实际指向 ISO 时，优先把 probe 输入改写为 ffprobe 可直接识别的协议路径，
    /// 例如 bluray:/path/to/file.iso；若未来需要，也可继续扩展为真实挂载入口。
    /// </summary>
    public static class IsoMountedProbeInput
    {
        private static Harmony harmony;
        private static MethodInfo getMediaInfoMethod;
        private static ILogger logger;
        private static Type mediaInfoType;

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
                    PatchLog.InitFailed(logger, nameof(IsoMountedProbeInput), "关键运行时类型缺失");
                    return;
                }

                getMediaInfoMethod = PatchMethodResolver.Resolve(
                    mediaProbeManagerType,
                    mediaEncodingAssembly.GetName().Version,
                    new MethodSignatureProfile
                    {
                        Name = "mediaprobemanager-getmediainfo-mounted-input-exact",
                        MethodName = "GetMediaInfo",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        ParameterTypes = new[] { mediaInfoRequestType, typeof(CancellationToken) }
                    },
                    logger,
                    "IsoMountedProbeInput.MediaProbeManager.GetMediaInfo");

                if (getMediaInfoMethod == null)
                {
                    PatchLog.InitFailed(logger, nameof(IsoMountedProbeInput), "未命中 GetMediaInfo");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.iso-mounted-probe-input");
                PatchLog.Patched(logger, nameof(IsoMountedProbeInput), getMediaInfoMethod);
                harmony.Patch(
                    getMediaInfoMethod,
                    prefix: new HarmonyMethod(typeof(IsoMountedProbeInput), nameof(GetMediaInfoPrefix)),
                    postfix: new HarmonyMethod(typeof(IsoMountedProbeInput), nameof(GetMediaInfoPostfix)));
            }
            catch (Exception ex)
            {
                PatchLog.InitFailed(logger, nameof(IsoMountedProbeInput), ex.Message);
                logger?.Error("IsoMountedProbeInput 初始化异常：{0}", ex);
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
                var mediaSource = __0.GetType().GetProperty("MediaSource", BindingFlags.Instance | BindingFlags.Public)?.GetValue(__0);
                if (mediaSource == null)
                {
                    logger?.Debug("ISO mount probe 跳过：MediaSource=<null>");
                    return;
                }

                var path = GetStringProperty(mediaSource, "Path");
                if (!LooksLikeIsoPath(path))
                {
                    logger?.Debug("ISO mount probe 跳过：path={0}", path ?? "<null>");
                    return;
                }

                var container = InferIsoContainer(path);
                var remappedPath = BuildProbePath(path, container);
                if (string.IsNullOrWhiteSpace(remappedPath))
                {
                    logger?.Warn("ISO probe 输入改写失败：未生成协议路径 path={0} container={1}", path, container);
                    return;
                }

                var probeProtocolValue = GetMediaProtocolValue("File");
                SetProperty(mediaSource, "ProbePath", remappedPath);
                SetProperty(mediaSource, "ProbeProtocol", probeProtocolValue);

                logger?.Debug(
                    "ISO probe 输入改写：source={0} container={1} probePath={2} probeProtocol={3}",
                    path,
                    container,
                    remappedPath,
                    probeProtocolValue ?? "<null>");

                logger?.Debug(
                    "ISO probe 改写后 MediaSource：path={0} protocol={1} container={2} probePath={3} probeProtocol={4}",
                    GetStringProperty(mediaSource, "Path") ?? "<null>",
                    GetProperty(mediaSource, "Protocol") ?? "<null>",
                    GetStringProperty(mediaSource, "Container") ?? "<null>",
                    GetStringProperty(mediaSource, "ProbePath") ?? "<null>",
                    GetProperty(mediaSource, "ProbeProtocol") ?? "<null>");
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

            var wrapMethod = typeof(IsoMountedProbeInput).GetMethod(nameof(WrapTask), BindingFlags.Static | BindingFlags.NonPublic);
            var genericWrapMethod = wrapMethod?.MakeGenericMethod(mediaInfoType);
            if (genericWrapMethod == null)
            {
                return;
            }

            __result = genericWrapMethod.Invoke(null, new[] { __result, __0 });
        }

        private static async Task<T> WrapTask<T>(Task<T> task, object request)
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
                logger?.Error("ISO probe 杜比配置补判异常：{0}", ex);
            }

            return result;
        }

        private static void ApplyBlurayPlaylistMetadata(ModelMediaInfo mediaInfo, object request)
        {
            if (mediaInfo == null)
            {
                return;
            }

            var sourcePath = GetPreferredDiscSourcePath(mediaInfo, request);
            if (!LooksLikeIsoOrBdmvContent(sourcePath, mediaInfo.Container))
            {
                return;
            }

            var discRootPath = GetBlurayDiscRootPath(sourcePath);
            if (string.IsNullOrWhiteSpace(discRootPath))
            {
                return;
            }

            try
            {
                var preferredDurationSeconds = GetPreferredDurationSeconds(mediaInfo);
                var playlistInfo = BlurayStructureReader.ReadMainPlaylist(discRootPath, preferredDurationSeconds);
                if (playlistInfo == null)
                {
                    return;
                }

                ApplyDiscStreamLanguages(mediaInfo, playlistInfo);
                ApplyDiscChapters(mediaInfo, playlistInfo);
            }
            catch (Exception ex)
            {
                logger?.Debug("ISO/BDMV 播放列表元数据补全失败：path={0}, error={1}", sourcePath ?? "<null>", ex.Message);
            }
        }

        private static double? GetPreferredDurationSeconds(ModelMediaInfo mediaInfo)
        {
            var ticks = mediaInfo?.RunTimeTicks;
            if (!ticks.HasValue || ticks.Value <= 0)
            {
                return null;
            }

            return TimeSpan.FromTicks(ticks.Value).TotalSeconds;
        }

        private static void ApplyDiscStreamLanguages(ModelMediaInfo mediaInfo, BlurayPlaylistInfo playlistInfo)
        {
            if (playlistInfo?.Streams == null || playlistInfo.Streams.Count == 0 || mediaInfo.MediaStreams == null || mediaInfo.MediaStreams.Count == 0)
            {
                return;
            }

            var currentAudios = mediaInfo.MediaStreams.Where(s => s?.Type == MediaStreamType.Audio).ToList();
            var currentSubtitles = mediaInfo.MediaStreams.Where(s => s?.Type == MediaStreamType.Subtitle).ToList();

            var discAudios = playlistInfo.Streams.Where(stream => stream.IsAudio).ToList();
            var discSubtitles = playlistInfo.Streams.Where(stream => stream.IsSubtitle).ToList();

            logger?.Debug(
                "ISO/BDMV playlist streams: audio={0} subtitle={1} detail={2}",
                discAudios.Count,
                discSubtitles.Count,
                string.Join(" | ", playlistInfo.Streams.Select(stream =>
                    $"order={stream.Order},pid=0x{stream.Pid:X},audio={stream.IsAudio},subtitle={stream.IsSubtitle},lang={stream.LanguageCode ?? "<null>"}")));

            logger?.Debug(
                "ISO/BDMV current subtitle streams: {0}",
                string.Join(" | ", currentSubtitles.Select(stream =>
                    $"index={stream.Index},codec={stream.Codec ?? "<null>"},lang={stream.Language ?? "<null>"},title={stream.Title ?? "<null>"}")));

            CopyLanguagesByOrder(currentAudios, discAudios, "audio");
            CopyLanguagesByOrder(currentSubtitles, discSubtitles, "subtitle");
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

            logger?.Debug("ISO/BDMV 章节补全：count={0}", mediaInfo.Chapters.Length);
        }

        private static void CopyLanguagesByOrder(List<MediaStream> currentStreams, List<BlurayPlaylistStream> discStreams, string streamTypeName)
        {
            if (currentStreams.Count == 0 || discStreams.Count == 0)
            {
                return;
            }

            var limit = Math.Min(currentStreams.Count, discStreams.Count);
            var changed = 0;

            for (var i = 0; i < limit; i++)
            {
                var current = currentStreams[i];
                var source = discStreams[i];
                if (current == null || source == null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(current.Language))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(source.LanguageCode))
                {
                    continue;
                }

                current.Language = source.LanguageCode;
                changed++;
            }

            if (changed > 0)
            {
                logger?.Debug("ISO/BDMV {0} 语言补全：count={1}", streamTypeName, changed);
            }
        }

        private static void ApplyDolbyVisionFallback(ModelMediaInfo mediaInfo, object request)
        {
            if (mediaInfo == null)
            {
                return;
            }

            var sourcePath = GetPreferredDiscSourcePath(mediaInfo, request);
            if (!LooksLikeIsoOrBdmvContent(sourcePath, mediaInfo.Container))
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
                logger?.Debug(
                    "ISO probe 杜比配置保留原生结果：path={0} subtype={1}",
                    sourcePath ?? "<null>",
                    mainVideo.ExtendedVideoSubType);
                return;
            }

            if (!LooksLikeHdrBase(mainVideo))
            {
                logger?.Debug(
                    "ISO probe 杜比配置跳过：主视频非 HDR 基底 path={0} codec={1} colorTransfer={2} subtype={3}",
                    sourcePath ?? "<null>",
                    mainVideo.Codec ?? "<null>",
                    mainVideo.ColorTransfer ?? "<null>",
                    mainVideo.ExtendedVideoSubType);
                return;
            }

            var inferredSubtype = GetDolbyVisionSubtype(sourcePath, mediaInfo.Container, videoStreams, out var reason);
            if (!inferredSubtype.HasValue)
            {
                logger?.Debug(
                    "ISO probe 杜比配置未命中：path={0} container={1} reason={2}",
                    sourcePath ?? "<null>",
                    mediaInfo.Container ?? "<null>",
                    reason ?? "<null>");
                return;
            }

            mainVideo.ExtendedVideoType = ExtendedVideoTypes.DolbyVision;
            mainVideo.ExtendedVideoSubType = inferredSubtype.Value;

            logger?.Debug(
                "ISO probe 杜比配置补判：path={0} container={1} subtype={2} reason={3}",
                sourcePath ?? "<null>",
                mediaInfo.Container ?? "<null>",
                inferredSubtype.Value,
                reason ?? "<null>");
        }

        private static ExtendedVideoSubTypes? GetDolbyVisionSubtype(
            string sourcePath,
            string container,
            List<MediaStream> videoStreams,
            out string reason)
        {
            reason = null;

            var path = sourcePath ?? string.Empty;
            var lowerPath = path.ToLowerInvariant();
            var hasEnhancementLayer = videoStreams.Count > 1 &&
                                      videoStreams.Skip(1).Any(IsEnhancementLayerCandidate);
            var isBlurayLike = IsBlurayLike(lowerPath, container);

            if (hasEnhancementLayer && isBlurayLike)
            {
                reason = "bluray dual-layer enhancement layer";
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

        private static bool LooksLikeIsoOrBdmvContent(string path, string container)
        {
            path = NormalizeDiscPath(path);
            if (LooksLikeIsoPath(path))
            {
                return true;
            }

            var lowerPath = (path ?? string.Empty).ToLowerInvariant();
            var lowerContainer = (container ?? string.Empty).ToLowerInvariant();
            return lowerPath.Contains("/bdmv/") ||
                   lowerPath.EndsWith("/bdmv", StringComparison.OrdinalIgnoreCase) ||
                   lowerContainer.Contains("bluray") ||
                   lowerContainer.Contains("bdmv");
        }

        private static string GetBlurayDiscRootPath(string path)
        {
            path = NormalizeDiscPath(path);
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            if (path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            var normalized = path.Replace('\\', '/');
            var marker = "/BDMV/";
            var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return path.Substring(0, index);
            }

            if (normalized.EndsWith("/BDMV", StringComparison.OrdinalIgnoreCase))
            {
                return path.Substring(0, path.Length - 5);
            }

            return null;
        }

        private static bool IsBlurayLike(string lowerPath, string container)
        {
            var lowerContainer = (container ?? string.Empty).ToLowerInvariant();
            return lowerPath.Contains("bluray") ||
                   lowerPath.Contains("blu-ray") ||
                   lowerPath.Contains("bdmv") ||
                   lowerContainer.Contains("bluray") ||
                   lowerContainer.Contains("bdmv");
        }

        private static string GetRequestMediaSourcePath(object request)
        {
            var mediaSource = request?.GetType()
                .GetProperty("MediaSource", BindingFlags.Instance | BindingFlags.Public)?
                .GetValue(request) as MediaSourceInfo;

            return mediaSource?.Path;
        }

        private static string GetPreferredDiscSourcePath(ModelMediaInfo mediaInfo, object request)
        {
            var requestPath = NormalizeDiscPath(GetRequestMediaSourcePath(request));
            if (LooksLikeIsoOrBdmvContent(requestPath, mediaInfo?.Container))
            {
                return requestPath;
            }

            return NormalizeDiscPath(mediaInfo?.Path);
        }

        private static string NormalizeDiscPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            if (path.StartsWith("bluray:", StringComparison.OrdinalIgnoreCase))
            {
                return path.Substring("bluray:".Length);
            }

            return path;
        }

        private static bool LooksLikeIsoPath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);
        }

        private static string InferIsoContainer(string path)
        {
            var lower = (path ?? string.Empty).ToLowerInvariant();
            if (lower.Contains("bluray") || lower.Contains("blu-ray") || lower.Contains("bdmv"))
            {
                return "blurayiso";
            }

            if (lower.Contains("dvd") || lower.Contains("video_ts"))
            {
                return "dvdiso";
            }

            return "iso";
        }

        private static string BuildProbePath(string path, string container)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            if (string.Equals(container, "blurayiso", StringComparison.OrdinalIgnoreCase))
            {
                return "bluray:" + path;
            }

            return path;
        }

        private static object GetMediaProtocolValue(string name)
        {
            var mediaProtocolType = Assembly.Load("MediaBrowser.Model")?.GetType("MediaBrowser.Model.MediaInfo.MediaProtocol");
            if (mediaProtocolType == null)
            {
                return null;
            }

            try
            {
                return Enum.Parse(mediaProtocolType, name);
            }
            catch
            {
                return null;
            }
        }

        private static string GetStringProperty(object instance, string propertyName)
        {
            return instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(instance) as string;
        }

        private static void SetProperty(object instance, string propertyName, object value)
        {
            instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.SetValue(instance, value);
        }

        private static object GetProperty(object instance, string propertyName)
        {
            return instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(instance);
        }

    }
}
