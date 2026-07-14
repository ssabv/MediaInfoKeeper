using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaInfoKeeper.Store;

namespace MediaInfoKeeper.ScheduledTask
{
    public class RestoreMediaInfoTask : IScheduledTask
    {
        private readonly ILogger logger;

        public RestoreMediaInfoTask(ILogManager logManager)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
        }

        public string Key => "MediaInfoKeeperExtractMediaInfoTask";

        public string Name => "06.恢复媒体信息";

        public string Description => "按本任务配置的媒体库范围，存在 JSON 则恢复媒体信息，不存在则跳过。";

        public string Category => Plugin.TaskCategoryName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            this.logger.Info("计划任务执行");

            var items = FetchScopedItems();
            var total = items.Count;
            if (total == 0)
            {
                progress.Report(100.0);
                this.logger.Info("计划任务完成(0 个条目)");
                return;
            }

            var completed = 0;
            var tasks = items
                .Select(async item =>
                {
                    try
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }

                        await ProcessItemAsync(item, "Scheduled Task", cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // ignore
                    }
                    catch (Exception ex)
                    {
                        this.logger.Error($"任务执行失败: {item.Path ?? item.Name}");
                        this.logger.Error(ex.Message);
                        this.logger.Debug(ex.StackTrace);
                    }
                    finally
                    {
                        var done = Interlocked.Increment(ref completed);
                        progress?.Report(done / (double)total * 100);
                    }
                })
                .ToList();
            await Task.WhenAll(tasks).ConfigureAwait(false);

            this.logger.Info("计划任务完成");
        }

        private List<BaseItem> FetchScopedItems()
        {
            var items = Plugin.LibraryService.FetchScheduledTaskLibraryItems(
                Plugin.Instance.Options.MainPage.ScheduledTasksEditor.RestoreMediaInfo.RestoreMediaInfoLibraries,
                includeAudio: true);
            this.logger.Info($"计划任务条目数 {items.Count}");
            return items;
        }

        private Task ProcessItemAsync(BaseItem item, string source, CancellationToken cancellationToken)
        {
            var displayName = item.FileName ?? item.Path;

            if (!Plugin.Instance.Options.MainPage.PlugginEnabled)
            {
                this.logger.Info($"跳过 未开启持久化: {displayName}");
                return Task.CompletedTask;
            }

            var restoreResult = Plugin.MediaInfoService
                .RestorePersistedMediaInfoForExistingSource(item, source);
            if (restoreResult == MediaInfoDocument.MediaInfoRestoreResult.Restored)
            {
                this.logger.Info($"从JSON 恢复成功: {displayName}");
                return Task.CompletedTask;
            }

            if (restoreResult == MediaInfoDocument.MediaInfoRestoreResult.AlreadyExists)
            {
                this.logger.Info($"跳过 已存在MediaInfo: {displayName}");
                return Task.CompletedTask;
            }

            this.logger.Info($"无Json媒体信息存在，跳过: {displayName}");
            return Task.CompletedTask;
        }

    }
}
