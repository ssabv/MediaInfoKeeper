using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 将 ffprobe 读到的内嵌章节名映射为 Emby 片头片尾标记。
    /// 规则：
    /// Intro -> IntroStart，优先用 Intro 章结束点补一个 IntroEnd；无结束点时退回到下一章起点。
    /// Credits -> CreditsStart。
    /// </summary>
    public static class EmbeddedChapterMarkerMap
    {
        private const string ProbeChaptersMemberName = "chapters";
        private const string ProbeChapterEndTimeMemberName = "end_time";

        private static readonly HashSet<string> IntroNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "intro",
            "opening",
            "op",
            "片头",
            "片头曲",
            "オープニング",
            "vorspann",
            "opening credits",
            "오프닝"
        };

        private static readonly HashSet<string> CreditsNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "credits",
            "end credits",
            "outro",
            "ending",
            "ed",
            "片尾",
            "片尾曲",
            "演职员表",
            "エンディング",
            "スタッフロール",
            "abspann",
            "엔딩",
            "크레딧"
        };

        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo getMediaInfo;
        private static bool isEnabled;
        private static bool isPatched;

        public static bool IsReady => harmony != null && (!isEnabled || isPatched);

        public static void Initialize(ILogger pluginLogger, bool enable)
        {
            if (harmony != null)
            {
                Configure(enable);
                return;
            }

            logger = pluginLogger;
            isEnabled = enable;

            try
            {
                var mediaEncodingAssembly = Assembly.Load("Emby.Server.MediaEncoding");
                var probeResultNormalizerType = mediaEncodingAssembly?.GetType("Emby.Server.MediaEncoding.Probing.ProbeResultNormalizer");
                var version = mediaEncodingAssembly?.GetName().Version;
                var probeResultType = Assembly.Load("Emby.Media.Model")?.GetType("Emby.Media.Model.ProbeModel.ProbeResult");
                if (probeResultNormalizerType == null || probeResultType == null)
                {
                    PatchLog.InitFailed(logger, nameof(EmbeddedChapterMarkerMap), "ProbeResultNormalizer 或 ProbeResult 类型缺失");
                    return;
                }

                getMediaInfo = PatchMethodResolver.Resolve(
                    probeResultNormalizerType,
                    version,
                    new MethodSignatureProfile
                    {
                        Name = "proberesultnormalizer-getmediainfo-exact",
                        MethodName = "GetMediaInfo",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        ParameterTypes = new[] { probeResultType, typeof(bool), typeof(string), typeof(MediaBrowser.Model.MediaInfo.MediaProtocol) },
                        ReturnType = typeof(MediaBrowser.Model.MediaInfo.MediaInfo)
                    },
                    logger,
                    "EmbeddedChapterMarkerMap.GetMediaInfo");

                if (getMediaInfo == null)
                {
                    PatchLog.InitFailed(logger, nameof(EmbeddedChapterMarkerMap), "目标方法缺失");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.embeddedchaptermarkermap");

                if (isEnabled)
                {
                    Patch();
                }
            }
            catch (Exception ex)
            {
                logger?.Error("EmbeddedChapterMarkerMap 初始化失败。");
                logger?.Error(ex.Message);
                logger?.Error(ex.ToString());
                harmony = null;
                isEnabled = false;
            }
        }

        public static void Configure(bool enable)
        {
            isEnabled = enable;

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

        private static void Patch()
        {
            if (isPatched || harmony == null)
            {
                return;
            }

            harmony.Patch(
                getMediaInfo,
                postfix: new HarmonyMethod(typeof(EmbeddedChapterMarkerMap), nameof(GetMediaInfoPostfix)));

            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null)
            {
                return;
            }

            harmony.Unpatch(getMediaInfo, HarmonyPatchType.Postfix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPostfix]
        private static void GetMediaInfoPostfix(object __0, MediaBrowser.Model.MediaInfo.MediaInfo __result)
        {
            if (!isEnabled || __0 == null || __result == null)
            {
                return;
            }

            try
            {
                var chapters = __result.Chapters?.ToList();
                var probeChapters = GetProbeChapters(__0);
                if (chapters == null || probeChapters == null || chapters.Count == 0 || probeChapters.Count == 0)
                {
                    return;
                }

                ApplyMarkerMapping(chapters, probeChapters);
                __result.Chapters = chapters.ToArray();
            }
            catch (Exception ex)
            {
                logger?.Error("EmbeddedChapterMarkerMap 处理失败。");
                logger?.Error(ex.Message);
                logger?.Debug(ex.StackTrace);
            }
        }

        private static List<object> GetProbeChapters(object probeResult)
        {
            return GetPropertyValue(probeResult, ProbeChaptersMemberName) is Array chapters
                ? chapters.Cast<object>().Where(i => i != null).ToList()
                : null;
        }

        private static void ApplyMarkerMapping(List<ChapterInfo> chapters, List<object> probeChapters)
        {
            var count = Math.Min(chapters.Count, probeChapters.Count);
            if (count == 0)
            {
                return;
            }

            var addedMarkers = 0;
            var mappedMarkers = new List<string>();
            var orderedPairs = Enumerable.Range(0, count)
                .Select(i => new ChapterPair(chapters[i], probeChapters[i]))
                .Where(i => i.Chapter != null)
                .OrderBy(i => i.Chapter.StartPositionTicks)
                .ToList();

            for (var i = 0; i < orderedPairs.Count; i++)
            {
                var pair = orderedPairs[i];
                var normalizedName = NormalizeName(pair.Chapter.Name);
                if (string.IsNullOrEmpty(normalizedName))
                {
                    continue;
                }

                if (IntroNames.Contains(normalizedName))
                {
                    addedMarkers += ApplyIntroMarker(chapters, orderedPairs, i, pair, mappedMarkers);

                    continue;
                }

                addedMarkers += ApplyCreditsMarker(pair, normalizedName, mappedMarkers);
            }

            if (addedMarkers > 0)
            {
                chapters.Sort((left, right) => left.StartPositionTicks.CompareTo(right.StartPositionTicks));
                logger?.Info("内嵌章节已识别为片头片尾标记: {0}", mappedMarkers.Count == 0 ? "<none>" : string.Join(", ", mappedMarkers));
            }
        }

        private static int ApplyIntroMarker(
            List<ChapterInfo> chapters,
            IReadOnlyList<ChapterPair> orderedPairs,
            int index,
            ChapterPair pair,
            List<string> mappedMarkers)
        {
            var addedMarkers = 0;
            var hasIntroStart = pair.Chapter.MarkerType == MarkerType.IntroStart;

            if (pair.Chapter.MarkerType == MarkerType.Chapter)
            {
                pair.Chapter.MarkerType = MarkerType.IntroStart;
                addedMarkers++;
                hasIntroStart = true;
                mappedMarkers.Add($"IntroStart={FormatTicks(pair.Chapter.StartPositionTicks)}");
            }

            if (!hasIntroStart)
            {
                return addedMarkers;
            }

            var introEndTicks = ResolveIntroEndTicks(orderedPairs, index, pair);
            if (introEndTicks.HasValue &&
                introEndTicks.Value > pair.Chapter.StartPositionTicks &&
                !HasMarkerAt(chapters, MarkerType.IntroEnd, introEndTicks.Value))
            {
                chapters.Add(new ChapterInfo
                {
                    Name = "IntroEnd",
                    StartPositionTicks = introEndTicks.Value,
                    MarkerType = MarkerType.IntroEnd
                });
                addedMarkers++;
                mappedMarkers.Add($"IntroEnd={FormatTicks(introEndTicks.Value)}");
            }

            return addedMarkers;
        }

        private static int ApplyCreditsMarker(ChapterPair pair, string normalizedName, List<string> mappedMarkers)
        {
            if (!CreditsNames.Contains(normalizedName) ||
                pair.Chapter.MarkerType != MarkerType.Chapter)
            {
                return 0;
            }

            pair.Chapter.MarkerType = MarkerType.CreditsStart;
            mappedMarkers.Add($"CreditsStart={FormatTicks(pair.Chapter.StartPositionTicks)}");
            return 1;
        }

        private static bool HasMarkerAt(IEnumerable<ChapterInfo> chapters, MarkerType markerType, long ticks)
        {
            return chapters.Any(c => c != null &&
                                     c.MarkerType == markerType &&
                                     c.StartPositionTicks == ticks);
        }

        private static long? ResolveIntroEndTicks(IReadOnlyList<ChapterPair> orderedPairs, int index, ChapterPair pair)
        {
            var endTicks = GetProbeChapterTicks(pair.ProbeChapter, ProbeChapterEndTimeMemberName);
            if (endTicks.HasValue && endTicks.Value > pair.Chapter.StartPositionTicks)
            {
                return endTicks.Value;
            }

            var nextPair = index + 1 < orderedPairs.Count ? orderedPairs[index + 1] : null;
            if (nextPair?.Chapter != null && nextPair.Chapter.StartPositionTicks > pair.Chapter.StartPositionTicks)
            {
                return nextPair.Chapter.StartPositionTicks;
            }

            return null;
        }

        private static long? GetProbeChapterTicks(object probeChapter, string propertyName)
        {
            if (probeChapter == null)
            {
                return null;
            }

            var value = GetPropertyValue(probeChapter, propertyName);
            if (value == null)
            {
                return null;
            }

            if (value is decimal decimalValue)
            {
                return TimeSpan.FromSeconds((double)decimalValue).Ticks;
            }

            if (decimal.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return TimeSpan.FromSeconds((double)parsed).Ticks;
            }

            return null;
        }

        private static object GetPropertyValue(object instance, string propertyName)
        {
            if (instance == null || string.IsNullOrWhiteSpace(propertyName))
            {
                return null;
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            return instance.GetType().GetProperty(propertyName, flags)?.GetValue(instance);
        }

        private static string NormalizeName(string name)
        {
            return string.IsNullOrWhiteSpace(name)
                ? null
                : name.Trim();
        }

        private static string FormatTicks(long ticks)
        {
            return TimeSpan.FromTicks(ticks).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
        }

        private sealed class ChapterPair
        {
            public ChapterPair(ChapterInfo chapter, object probeChapter)
            {
                Chapter = chapter;
                ProbeChapter = probeChapter;
            }

            public ChapterInfo Chapter { get; }

            public object ProbeChapter { get; }
        }
    }
}
