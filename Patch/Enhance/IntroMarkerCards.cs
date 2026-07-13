using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using HarmonyLib;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;

namespace MediaInfoKeeper.Patch {
    /// <summary>
    ///     将已保存的片头片尾标记追加到 PlaybackInfo，供 Web 详情页沿用媒体流卡片渲染。
    /// </summary>
    public static class IntroMarkerCards {
        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo playbackInfoEntry;
        private static PropertyInfo isPlaybackProperty;
        private static bool isEnabled;
        private static bool isPatched;

        public static bool IsReady => harmony != null && (!isEnabled || isPatched);

        public static void Initialize(ILogger pluginLogger, bool enable) {
            if (harmony != null) {
                Configure(enable);
                return;
            }

            logger = pluginLogger;
            isEnabled = enable;

            try {
                var mediaEncoding = Assembly.Load("Emby.Server.MediaEncoding");
                var mediaInfoServiceType = mediaEncoding?.GetType("Emby.Server.MediaEncoding.Api.MediaInfoService");
                var getPostedPlaybackInfoType =
                    mediaEncoding?.GetType("Emby.Server.MediaEncoding.Api.GetPostedPlaybackInfo");

                if (mediaInfoServiceType == null || getPostedPlaybackInfoType == null) {
                    PatchLog.InitFailed(logger, nameof(IntroMarkerCards),
                        "未找到 MediaInfoService/GetPostedPlaybackInfo 类型");
                    return;
                }

                isPlaybackProperty = getPostedPlaybackInfoType.GetProperty("IsPlayback",
                    BindingFlags.Instance | BindingFlags.Public);
                if (isPlaybackProperty?.PropertyType != typeof(bool)) {
                    PatchLog.InitFailed(logger, nameof(IntroMarkerCards),
                        "未找到 GetPostedPlaybackInfo.IsPlayback");
                    return;
                }

                playbackInfoEntry = PatchMethodResolver.Resolve(
                    mediaInfoServiceType,
                    mediaEncoding.GetName().Version,
                    new MethodSignatureProfile {
                        Name = "playbackinfo-intro-marker-cards-exact",
                        MethodName = "GetPlaybackInfo",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        ParameterTypes = new[] {
                            getPostedPlaybackInfoType,
                            typeof(bool),
                            typeof(string),
                            typeof(CancellationToken)
                        },
                        ReturnType = typeof(Task<>).MakeGenericType(typeof(PlaybackInfoResponse))
                    },
                    logger,
                    "IntroMarkerCards.MediaInfoService.GetPlaybackInfo");

                if (playbackInfoEntry == null) {
                    PatchLog.InitFailed(logger, nameof(IntroMarkerCards), "未找到 GetPlaybackInfo");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.intromarkercards");
                if (isEnabled) Patch();
            }
            catch (Exception ex) {
                PatchLog.InitFailed(logger, nameof(IntroMarkerCards), ex.Message);
                logger?.Error("IntroMarkerCards 初始化异常：{0}", ex);
                harmony = null;
                playbackInfoEntry = null;
                isPlaybackProperty = null;
                isPatched = false;
            }
        }

        public static void Configure(bool enable) {
            isEnabled = enable;
            if (harmony == null) return;

            if (isEnabled)
                Patch();
            else
                Unpatch();
        }

        private static void Patch() {
            if (isPatched || harmony == null || playbackInfoEntry == null) return;

            harmony.Patch(
                playbackInfoEntry,
                postfix: new HarmonyMethod(typeof(IntroMarkerCards), nameof(GetPlaybackInfoPostfix)));
            PatchLog.Patched(logger, nameof(IntroMarkerCards), playbackInfoEntry);
            isPatched = true;
        }

        private static void Unpatch() {
            if (!isPatched || harmony == null || playbackInfoEntry == null) return;

            harmony.Unpatch(playbackInfoEntry, HarmonyPatchType.Postfix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPostfix]
        private static void GetPlaybackInfoPostfix(object __0, ref Task<PlaybackInfoResponse> __result) {
            if (!isEnabled || __result == null || IsPlaybackRequest(__0)) return;

            __result = AppendMarkerCardsAsync(__result);
        }

        private static async Task<PlaybackInfoResponse> AppendMarkerCardsAsync(Task<PlaybackInfoResponse> task) {
            var response = await task.ConfigureAwait(false);
            if (!isEnabled || response?.MediaSources == null || response.MediaSources.Length == 0) return response;

            try {
                var itemId = response.MediaSources.FirstOrDefault()?.ItemId;
                var item = long.TryParse(itemId, out var internalId)
                    ? Plugin.LibraryManager?.GetItemById(internalId)
                    : Guid.TryParse(itemId, out var itemGuid)
                        ? Plugin.LibraryManager?.GetItemById(itemGuid)
                        : null;
                var chapters = item == null ? null : Plugin.IntroSkipChapterApi?.GetChapters(item);
                var introStartTicks = chapters == null ? null : GetMarkerTicks(chapters, MarkerType.IntroStart);
                var introEndTicks = chapters == null ? null : GetMarkerTicks(chapters, MarkerType.IntroEnd);
                var creditsStartTicks = chapters == null ? null : GetMarkerTicks(chapters, MarkerType.CreditsStart);
                var danmuPath = Plugin.DanmuService?.GetDanmuXmlPath(item);
                if (!File.Exists(danmuPath)) danmuPath = null;
                var danmuCount = danmuPath == null ? 0 : GetDanmuCount(danmuPath);

                var hasMarker = introStartTicks.HasValue || introEndTicks.HasValue || creditsStartTicks.HasValue;
                if (!hasMarker && string.IsNullOrWhiteSpace(danmuPath)) return response;

                foreach (var source in response.MediaSources) {
                    if (source?.MediaStreams == null) continue;

                    if (hasMarker && !HasCard(source, "MediaInfoKeeper.IntroMarkerCard")) {
                        if (introStartTicks.HasValue || introEndTicks.HasValue)
                            source.MediaStreams.Add(CreateCard(
                                "片头",
                                FormatRange(introStartTicks, introEndTicks),
                                "MediaInfoKeeper.IntroMarkerCard"));

                        if (creditsStartTicks.HasValue)
                            source.MediaStreams.Add(CreateCard(
                                "片尾",
                                FormatRange(creditsStartTicks, source.RunTimeTicks ?? item.RunTimeTicks),
                                "MediaInfoKeeper.IntroMarkerCard"));
                    }

                    if (!string.IsNullOrWhiteSpace(danmuPath) &&
                        !HasCard(source, "MediaInfoKeeper.DanmuCard"))
                        source.MediaStreams.Add(CreateDanmuCard(danmuCount, File.GetLastWriteTime(danmuPath)));

                    source.MediaStreams = source.MediaStreams
                        .OrderBy(GetDisplayOrder)
                        .ToList();
                }
            }
            catch (Exception ex) {
                logger?.Debug("IntroMarkerCards 追加失败：{0}", ex.Message);
            }

            return response;
        }

        private static long? GetMarkerTicks(System.Collections.Generic.IEnumerable<ChapterInfo> chapters,
            MarkerType markerType) {
            return chapters.FirstOrDefault(chapter => chapter?.MarkerType == markerType)?.StartPositionTicks;
        }

        private static bool IsPlaybackRequest(object request) {
            return isPlaybackProperty?.GetValue(request) is true;
        }

        private static bool HasCard(MediaSourceInfo source, string cardType) {
            return source.MediaStreams.Any(stream =>
                stream.Type == MediaStreamType.Attachment &&
                string.Equals(stream.Comment, cardType, StringComparison.Ordinal));
        }

        private static MediaStream CreateCard(string title, string range, string cardType) {
            return new MediaStream {
                Type = MediaStreamType.Attachment,
                Index = -1,
                DisplayTitle = title,
                Title = range,
                Comment = cardType
            };
        }

        private static MediaStream CreateDanmuCard(int count, DateTime lastWriteTime) {
            return new MediaStream {
                Type = MediaStreamType.Attachment,
                Index = -1,
                DisplayTitle = "弹幕",
                Title = $"{lastWriteTime:yyyy/M/d HH:mm} {count} 条",
                Comment = "MediaInfoKeeper.DanmuCard"
            };
        }

        private static int GetDanmuCount(string path) {
            try {
                return XDocument.Load(path).Descendants("d").Count();
            }
            catch (Exception ex) {
                logger?.Debug("IntroMarkerCards 读取弹幕失败：{0}", ex.Message);
                return 0;
            }
        }

        private static int GetDisplayOrder(MediaStream stream) {
            if (stream?.Type == MediaStreamType.Video) return 0;

            if (stream?.Type == MediaStreamType.Audio) return 1;

            if (stream?.Type == MediaStreamType.Attachment &&
                string.Equals(stream.Comment, "MediaInfoKeeper.IntroMarkerCard", StringComparison.Ordinal))
                return 2;

            if (stream?.Type == MediaStreamType.Attachment &&
                string.Equals(stream.Comment, "MediaInfoKeeper.DanmuCard", StringComparison.Ordinal))
                return 3;

            if (stream?.Type == MediaStreamType.Subtitle) return 4;

            return 5;
        }

        private static string FormatRange(long? startTicks, long? endTicks) {
            var start = FormatTicks(startTicks);
            var end = FormatTicks(endTicks);

            return start != null && end != null
                ? $"{start} - {end}"
                : start ?? end ?? string.Empty;
        }

        private static string FormatTicks(long? ticks) {
            return ticks.HasValue && ticks.Value >= 0
                ? new TimeSpan(ticks.Value).ToString(@"hh\:mm\:ss")
                : null;
        }
    }
}
