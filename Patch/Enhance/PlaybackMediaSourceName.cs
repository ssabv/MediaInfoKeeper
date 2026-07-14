using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 美化 PlaybackInfo 返回给客户端的多版本媒体源名称。
    /// </summary>
    public static class PlaybackMediaSourceName
    {
        private static readonly object InitLock = new object();

        private static Harmony harmony;
        private static ILogger logger;
        private static bool isEnabled;
        private static bool isPatched;
        private static MethodInfo playbackInfoEntry;

        public static bool IsReady => harmony != null && (!isEnabled || isPatched);

        public static void Initialize(ILogger pluginLogger, bool enablePlaybackMediaSourceName)
        {
            lock (InitLock)
            {
                logger = pluginLogger;
                isEnabled = enablePlaybackMediaSourceName;
                if (harmony != null)
                {
                    Configure(enablePlaybackMediaSourceName);
                    return;
                }

                try
                {
                    var mediaEncoding = Assembly.Load("Emby.Server.MediaEncoding");
                    var mediaInfoServiceType = mediaEncoding?.GetType("Emby.Server.MediaEncoding.Api.MediaInfoService");
                    var getPostedPlaybackInfoType = mediaEncoding?.GetType("Emby.Server.MediaEncoding.Api.GetPostedPlaybackInfo");

                    if (mediaInfoServiceType == null || getPostedPlaybackInfoType == null)
                    {
                        PatchLog.InitFailed(logger, nameof(PlaybackMediaSourceName), "未找到 MediaInfoService/GetPostedPlaybackInfo 类型");
                        return;
                    }

                    playbackInfoEntry = PatchMethodResolver.Resolve(
                        mediaInfoServiceType,
                        mediaEncoding.GetName().Version,
                        new MethodSignatureProfile
                        {
                            Name = "playbackinfo-mediasource-name-entry-exact",
                            MethodName = "GetPlaybackInfo",
                            BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                            ParameterTypes = new[]
                            {
                                getPostedPlaybackInfoType,
                                typeof(bool),
                                typeof(string),
                                typeof(CancellationToken)
                            },
                            ReturnType = typeof(Task<>).MakeGenericType(typeof(PlaybackInfoResponse))
                        },
                        logger,
                        "PlaybackMediaSourceName.MediaInfoService.GetPlaybackInfo");

                    if (playbackInfoEntry == null)
                    {
                        PatchLog.InitFailed(logger, nameof(PlaybackMediaSourceName), "未找到 GetPlaybackInfo");
                        return;
                    }

                    harmony = new Harmony("mediainfokeeper.playbackmediasourcename");
                    PatchLog.Patched(logger, nameof(PlaybackMediaSourceName), playbackInfoEntry);

                    if (isEnabled)
                    {
                        Patch();
                    }
                }
                catch (Exception ex)
                {
                    PatchLog.InitFailed(logger, nameof(PlaybackMediaSourceName), ex.Message);
                    logger?.Error("PlaybackMediaSourceName 初始化异常：{0}", ex);
                    harmony = null;
                    isPatched = false;
                }
            }
        }

        public static void Configure(bool enablePlaybackMediaSourceName)
        {
            lock (InitLock)
            {
                isEnabled = enablePlaybackMediaSourceName;
                if (harmony == null)
                {
                    return;
                }

                if (isEnabled)
                {
                    Patch();
                }
                else
                {
                    Unpatch();
                }
            }
        }

        private static void Patch()
        {
            if (isPatched || harmony == null || playbackInfoEntry == null)
            {
                return;
            }

            harmony.Patch(
                playbackInfoEntry,
                postfix: new HarmonyMethod(typeof(PlaybackMediaSourceName), nameof(GetPlaybackInfoPostfix)));
            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null || playbackInfoEntry == null)
            {
                return;
            }

            harmony.Unpatch(playbackInfoEntry, HarmonyPatchType.Postfix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPostfix]
        private static void GetPlaybackInfoPostfix(ref Task<PlaybackInfoResponse> __result)
        {
            if (!isEnabled || __result == null)
            {
                return;
            }

            __result = BeautifyAsync(__result);
        }

        private static async Task<PlaybackInfoResponse> BeautifyAsync(Task<PlaybackInfoResponse> task)
        {
            var response = await task.ConfigureAwait(false);
            if (!isEnabled || response?.MediaSources == null)
            {
                return response;
            }

            foreach (var source in response.MediaSources)
            {
                TryApplyDisplayName(source);
            }

            return response;
        }

        private static void TryApplyDisplayName(MediaSourceInfo source)
        {
            try
            {
                var displayName = BuildDisplayName(source);
                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    source.Name = displayName;
                }
            }
            catch (Exception ex)
            {
                logger?.Debug("PlaybackMediaSourceName failed: {0}", ex.Message);
            }
        }

        private static string BuildDisplayName(MediaSourceInfo source)
        {
            var videoStream = source?.VideoStream ??
                source?.MediaStreams?.FirstOrDefault(i => i.Type == MediaStreamType.Video);
            if (videoStream == null)
            {
                return null;
            }

            var bitrate = GetBitrate(source);
            if (!bitrate.HasValue || bitrate.Value <= 0)
            {
                return null;
            }

            var resolution = GetResolutionLabel(videoStream);
            if (string.IsNullOrWhiteSpace(resolution))
            {
                return null;
            }

            var range = GetVideoRangeLabel(videoStream);
            var bitrateLabel = FormatBitrate(bitrate.Value);

            return string.IsNullOrWhiteSpace(range)
                ? $"{resolution} - {bitrateLabel}"
                : $"{resolution} {range} - {bitrateLabel}";
        }

        private static long? GetBitrate(MediaSourceInfo source)
        {
            if (source?.Bitrate > 0)
            {
                return source.Bitrate.Value;
            }

            var streamBitrate = source?.MediaStreams?
                .Where(i => !i.IsExternal)
                .Select(i => (long)i.BitRate.GetValueOrDefault())
                .Sum();

            return streamBitrate > 0 ? streamBitrate : null;
        }

        private static string GetResolutionLabel(MediaStream videoStream)
        {
            var width = videoStream.Width.GetValueOrDefault();
            var height = videoStream.Height.GetValueOrDefault();

            if (width >= 3800 || height >= 2000)
            {
                return "4K";
            }

            if (height >= 1440)
            {
                return "1440p";
            }

            if (height >= 1080)
            {
                return "1080p";
            }

            if (height >= 720)
            {
                return "720p";
            }

            if (height > 0)
            {
                return $"{height}p";
            }

            return null;
        }

        private static string GetVideoRangeLabel(MediaStream videoStream)
        {
            if (videoStream.ExtendedVideoType == ExtendedVideoTypes.DolbyVision)
            {
                var profile = GetDolbyVisionProfileLabel(videoStream.ExtendedVideoSubType);
                return string.IsNullOrWhiteSpace(profile) ? "DV" : $"DV {profile}";
            }

            return null;
        }

        private static string GetDolbyVisionProfileLabel(ExtendedVideoSubTypes subType)
        {
            switch (subType)
            {
                case ExtendedVideoSubTypes.DoviProfile02:
                    return "P2";
                case ExtendedVideoSubTypes.DoviProfile10:
                    return "P1";
                case ExtendedVideoSubTypes.DoviProfile22:
                    return "P2";
                case ExtendedVideoSubTypes.DoviProfile30:
                    return "P3";
                case ExtendedVideoSubTypes.DoviProfile42:
                    return "P4";
                case ExtendedVideoSubTypes.DoviProfile50:
                    return "P5";
                case ExtendedVideoSubTypes.DoviProfile61:
                    return "P6";
                case ExtendedVideoSubTypes.DoviProfile76:
                    return "P7";
                case ExtendedVideoSubTypes.DoviProfile81:
                case ExtendedVideoSubTypes.DoviProfile82:
                case ExtendedVideoSubTypes.DoviProfile83:
                case ExtendedVideoSubTypes.DoviProfile84:
                case ExtendedVideoSubTypes.DoviProfile85:
                    return "P8";
                case ExtendedVideoSubTypes.DoviProfile92:
                    return "P9";
                default:
                    return null;
            }
        }

        private static string FormatBitrate(long bitrate)
        {
            var mbps = Math.Max(1, (int)Math.Round(bitrate / 1000000d, MidpointRounding.AwayFromZero));
            return $"{mbps} Mbps";
        }
    }
}
