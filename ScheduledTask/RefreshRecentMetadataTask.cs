using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaInfoKeeper.Common;
using MediaInfoKeeper.Services;
using static MediaInfoKeeper.Options.MainPageOptions;

namespace MediaInfoKeeper.ScheduledTask
{
    public class RefreshRecentMetadataTask : IScheduledTask
    {
        private sealed class RoleRefreshTarget
        {
            public long ItemId { get; set; }

            public string Name { get; set; }

            public int? ProductionYear { get; set; }
        }

        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;

        public RefreshRecentMetadataTask(ILogManager logManager, ILibraryManager libraryManager)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
            this.libraryManager = libraryManager;
        }
        public string Key => "MediaInfoKeeperRefreshRecentMetadataTask";

        public string Name => "02.刷新媒体元数据";

        public string Description => "按本任务配置的媒体库范围与最近入库时间窗口筛选条目，刷新元数据（可选覆盖或补全），之后会从 JSON 恢复媒体信息。";

        public string Category => Plugin.TaskCategoryName;

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            this.logger.Info("最近条目刷新元数据计划任务开始");

            var items = FetchRecentItems();
            var total = items.Count;
            if (total == 0)
            {
                progress.Report(100.0);
                this.logger.Info("计划任务完成，条目数 0");
                return Task.CompletedTask;
            }

            var replaceMetadata = ShouldReplaceMetadata();
            var replaceImages = ShouldReplaceImages();
            var replaceThumbnails = ShouldReplaceThumbnails();
            var metadataRefreshTargets = CollectMetadataRefreshItemIds(items);
            var totalWork = total + metadataRefreshTargets.Count;
            this.logger.Info($"计划任务条目数{total}，元数据覆盖{replaceMetadata}，图片覆盖{replaceImages}，视频缩略图覆盖{replaceThumbnails}");

            var submitted = 0;
            foreach (var item in items)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    this.logger.Info($"计划任务已取消 {item.Path}");
                    break;
                }

                var options = BuildRefreshOptions(replaceMetadata, replaceImages, replaceThumbnails);
                _ = MetaDataRunner.RefreshMetaDataAsync(item.InternalId, options, CancellationToken.None, priority:RefreshPriority.High);
                ReportProgress(totalWork, progress, ++submitted);
            }

            if (metadataRefreshTargets.Count > 0)
            {
                this.logger.Info($"计划任务刷新豆瓣角色中文化 {metadataRefreshTargets.Count} 个 Series");
            }
            foreach (var target in metadataRefreshTargets)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    this.logger.Info($"计划任务已取消 itemid={target.ItemId}");
                    break;
                }
                
                var roleOptions = BuildRefreshOptions(replaceMetadata: true, replaceImages: false, replaceThumbnails: false);
                roleOptions.Recursive = false;
                _ = MetaDataRunner.RefreshMetaDataAsync(target.ItemId, roleOptions, CancellationToken.None, priority:RefreshPriority.High);
                ReportProgress(totalWork, progress, ++submitted);
            }

            progress.Report(100.0);
            this.logger.Info("最近条目刷新元数据计划任务已提交后台执行");
            return Task.CompletedTask;
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            };

            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(16).Ticks
            };
        }

        private List<BaseItem> FetchRecentItems()
        {
            var taskOptions = Plugin.Instance.Options.MainPage.ScheduledTasksEditor.RefreshRecentMetadata;
            var taskScope = taskOptions.RefreshRecentMetadataLibraries;
            var days = taskOptions.RefreshRecentMetadataDays;
            var cutoff = days > 0
                ? ConfiguredDateTime.Now.AddDays(-days)
                : (DateTime?)null;

            return Plugin.LibraryService.FetchRecentScheduledTaskLibraryItems(
                cutoff,
                taskScope,
                true,
                includeAudio: true);
        }

        private MetadataRefreshOptions BuildRefreshOptions(bool replaceMetadata, bool replaceImages, bool replaceThumbnails)
        {
            return new MetadataRefreshOptions(new DirectoryService(this.logger, Plugin.FileSystem))
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = replaceMetadata,
                ReplaceAllImages = replaceImages,
                ReplaceThumbnailImages = replaceThumbnails,
                EnableThumbnailImageExtraction = Plugin.Instance.Options.MetaData.EnableImageCapture ||
                                                 Plugin.Instance.Options.MetaData.EnableEmbeddedImages,
                IsAutomated = true
            };
        }

        private List<RoleRefreshTarget> CollectMetadataRefreshItemIds(IEnumerable<BaseItem> items)
        {
            var result = new List<RoleRefreshTarget>();
            var seen = new HashSet<long>();

            foreach (var item in items)
            {
                BaseItem refreshItem = item switch
                {
                    Series series when series.InternalId > 0 => series,
                    Season season when season.Series?.InternalId > 0 => season.Series,
                    Episode episode when episode.Series?.InternalId > 0 => episode.Series,
                    _ => null
                };

                if (!IsDoubanRoleEnabled(refreshItem))
                {
                    continue;
                }

                if (seen.Add(refreshItem.InternalId))
                {
                    result.Add(new RoleRefreshTarget
                    {
                        ItemId = refreshItem.InternalId,
                        Name = refreshItem.Name,
                        ProductionYear = refreshItem.ProductionYear
                    });
                }
            }

            return result;
        }

        private bool IsDoubanRoleEnabled(BaseItem item)
        {
            if (item == null)
            {
                return false;
            }

            var libraryOptions = this.libraryManager.GetLibraryOptions(item);
            return libraryOptions != null &&
                   item.IsMetadataFetcherEnabled(libraryOptions, Provider.DoubanRoleProvider.ProviderName);
        }

        private static string FormatItemLabel(string name, int? productionYear)
        {
            return productionYear.HasValue
                ? $"{name} ({productionYear.Value})"
                : name ?? string.Empty;
        }

        private static void ReportProgress(int totalWork, IProgress<double> progress, int completed)
        {
            progress?.Report(completed / (double)totalWork * 100);
        }

        private bool ShouldReplaceMetadata()
        {
            return Plugin.Instance.Options.MainPage.ScheduledTasksEditor.RefreshRecentMetadata.RefreshMetadataMode == RefreshModeOption.Replace;
        }

        private bool ShouldReplaceImages()
        {
            return Plugin.Instance.Options.MainPage.ScheduledTasksEditor.RefreshRecentMetadata.ReplaceExistingImages;
        }

        private bool ShouldReplaceThumbnails()
        {
            return Plugin.Instance.Options.MainPage.ScheduledTasksEditor.RefreshRecentMetadata.ReplaceExistingVideoPreviewThumbnails;
        }
    }
}
