using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaInfoKeeper.Common;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.ScheduledTask
{
    public class SubmitTheIntroDbMarkersTask : IScheduledTask
    {
        private readonly ILogger logger;
        private const string TheIntroDbMarkerSuffix = "#MIKTIDB";

        public SubmitTheIntroDbMarkersTask(ILogManager logManager)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
        }

        public string Key => "MediaInfoKeeperSubmitTheIntroDbMarkersTask";

        public string Name => "10.共享片头片尾";

        public string Description => "按本任务配置的媒体库范围，将已有片头/片尾章节标记共享到 TheIntroDB。建议指行周期和筛选天数一样，这样不会重复上报。";

        public string Category => Plugin.TaskCategoryName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            this.logger.Info("TheIntroDB 上报计划任务开始");

            var apiKey = Plugin.Instance?.Options?.IntroSkip?.TheIntroDbApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                progress?.Report(100.0);
                this.logger.Warn("TheIntroDB 上报计划任务跳过: 未配置 API Key");
                return;
            }

            var items = FetchScopedItems();
            var total = items.Count;
            if (total == 0)
            {
                progress?.Report(100.0);
                this.logger.Info("TheIntroDB 上报计划任务完成: 条目数 0");
                return;
            }

            var processed = 0;
            var succeeded = 0;
            var skipped = 0;
            var failed = 0;
            var submittedSegments = 0;

            foreach (var item in items)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    this.logger.Info("TheIntroDB 上报计划任务已取消");
                    return;
                }

                processed++;
                try
                {
                    var result = await TheIntroDbService.SubmitMarkersAsync(item, cancellationToken).ConfigureAwait(false);
                    if (result.Succeeded)
                    {
                        succeeded++;
                        submittedSegments += result.SubmittedSegments;
                    }
                    else if (result.Skipped)
                    {
                        skipped++;
                    }
                    else
                    {
                        failed++;
                        if (result.RateLimited)
                        {
                            this.logger.Warn("TheIntroDB 上报计划任务遇到限流，停止本轮任务");
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    this.logger.Info("TheIntroDB 上报计划任务已取消");
                    return;
                }
                catch (Exception ex)
                {
                    failed++;
                    this.logger.Error("TheIntroDB 上报计划任务异常: " + TheIntroDbService.FormatItemForLog(item));
                    this.logger.Error(ex.Message);
                    this.logger.Debug(ex.StackTrace);
                }

                progress?.Report(processed / (double)total * 100);
            }

            progress?.Report(100.0);
            this.logger.Info(
                "TheIntroDB 上报计划任务完成: total={0}, processed={1}, succeeded={2}, skipped={3}, failed={4}, submittedSegments={5}",
                total,
                processed,
                succeeded,
                skipped,
                failed,
                submittedSegments);
        }

        private List<BaseItem> FetchScopedItems()
        {
            var taskOptions = Plugin.Instance.Options.MainPage.ScheduledTasksEditor.SubmitTheIntroDbMarkers;
            var days = taskOptions.SubmitTheIntroDbMarkersDays;
            var cutoff = days > 0
                ? ConfiguredDateTime.Now.AddDays(-days)
                : (DateTime?)null;
            var items = Plugin.LibraryService.FetchRecentScheduledTaskLibraryItems(
                    cutoff: cutoff,
                    taskScopedLibraries: taskOptions.SubmitTheIntroDbMarkersLibraries,
                    orderByDateCreatedDesc: true)
                .Where(IsSupportedItem)
                .Where(HasAnyMarkers)
                .ToList();

            this.logger.Info("TheIntroDB 上报候选条目数 {0}, 天数窗口: {1}", items.Count, cutoff == null ? "不限制" : days.ToString());
            return items;
        }

        private static bool IsSupportedItem(BaseItem item)
        {
            return item is Movie || item is Episode;
        }

        private static bool HasAnyMarkers(BaseItem item)
        {
            var chapters = Plugin.IntroSkipChapterApi.GetChapters(item);
            return chapters != null && chapters.Any(chapter =>
                chapter != null &&
                (chapter.MarkerType == MarkerType.IntroStart ||
                 chapter.MarkerType == MarkerType.IntroEnd ||
                 chapter.MarkerType == MarkerType.CreditsStart) &&
                !IsTheIntroDbMarker(chapter));
        }

        private static bool IsTheIntroDbMarker(ChapterInfo chapter)
        {
            return chapter?.Name?.IndexOf(TheIntroDbMarkerSuffix, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
