using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.ScheduledTask
{
    public class ExtractRecentMediaInfoTask : IScheduledTask
    {
        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;

        public ExtractRecentMediaInfoTask(ILogManager logManager, ILibraryManager libraryManager)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
            this.libraryManager = libraryManager;
        }

        public string Key => "MediaInfoKeeperExtractRecentMediaInfoTask";

        public string Name => "04.提取媒体信息";

        public string Description => "按本任务配置的媒体库范围，取最近条目提取媒体信息并写入 JSON。（已存在则恢复）";

        public string Category => Plugin.TaskCategoryName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            this.logger.Info("计划任务执行");

            var items = FetchRecentScopedItems();
            var total = items.Count;
            if (total == 0)
            {
                progress.Report(100.0);
                this.logger.Info("计划任务完成，条目数 0");
                return Task.CompletedTask;
            }

            var submitted = 0;
            foreach (var item in items)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    this.logger.Info($"计划任务已取消 {item.Path ?? item.Name}");
                    break;
                }

                FireAndForgetItemExtraction(item);
                progress?.Report(++submitted / (double)total * 100);
            }

            progress.Report(100.0);
            this.logger.Info("计划任务已提交后台执行");
            return Task.CompletedTask;
        }

        private void FireAndForgetItemExtraction(BaseItem item)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessItemAsync(item, "Recent Scheduled Task", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.logger.Error($"任务执行失败: {item.Path ?? item.Name}");
                    this.logger.Error(ex.Message);
                    this.logger.Debug(ex.StackTrace);
                }
            });
        }

        private List<BaseItem> FetchRecentScopedItems()
        {
            var taskOptions = Plugin.Instance.Options.MainPage.ScheduledTasksEditor.ExtractRecentMediaInfo;
            var limit = Math.Max(1, taskOptions.ExtractRecentMediaInfoLimit);
            var items = Plugin.LibraryService.FetchScheduledTaskLibraryItems(
                taskOptions.ExtractRecentMediaInfoLibraries,
                true,
                limit,
                includeAudio: true);
            this.logger.Info($"计划任务条目数 {items.Count}");
            return items;
        }

        private async Task ProcessItemAsync(BaseItem item, string source, CancellationToken cancellationToken)
        {
            var displayName = item.FileName ?? item.Path;

            var persistMediaInfo = (item is Video || item is Audio) && Plugin.Instance.Options.MainPage.PlugginEnabled;
            if (!persistMediaInfo)
            {
                this.logger.Info($"跳过 未开启持久化或非音视频: {displayName}");
                return;
            }

            var result = await MediaInfoRunner
                .ExtractMediaInfoAsync(item.InternalId, source, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            this.logger.Info(result
                ? $"完成: {displayName}"
                : $"失败或跳过: {displayName}");
        }

    }
}
