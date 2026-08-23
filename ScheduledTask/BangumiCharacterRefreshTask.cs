using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.ScheduledTask
{
    public class BangumiCharacterRefreshTask : IScheduledTask
    {
        private readonly ILogger logger;

        public BangumiCharacterRefreshTask(ILogManager logManager)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
        }

        public string Key => "MediaInfoKeeperBangumiCharacterRefreshTask";

        public string Name => "Bangumi 角色中文名增强";

        public string Description => "扫描指定媒体库中所有含 TMDB ID 的剧集和电影，从 Bangumi 获取角色中文名。支持国漫中文搜索、日漫日文搜索、美漫英文搜索。";

        public string Category => Plugin.TaskCategoryName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (Plugin.Instance?.Options?.MetaData?.EnableBangumiCharacters != true)
            {
                logger.Warn("Bangumi 角色增强计划任务跳过：功能未启用");
                progress.Report(100.0);
                return;
            }

            logger.Info("Bangumi 角色增强计划任务开始");

            var taskScope = Plugin.Instance?.Options?.MainPage?.ScheduledTasksEditor?.BangumiCharacter?.BangumiCharacterLibraries;

            var scopedItems = Plugin.LibraryService.FetchScheduledTaskLibraryItems(taskScope);

            var movies = scopedItems
                .OfType<Movie>()
                .Where(i => !string.IsNullOrWhiteSpace(i.GetProviderId(MetadataProviders.Tmdb)))
                .Cast<BaseItem>();

            var series = scopedItems
                .OfType<Episode>()
                .Select(e => e.Series)
                .Where(s => s != null)
                .Where(s => !string.IsNullOrWhiteSpace(s.GetProviderId(MetadataProviders.Tmdb)))
                .Cast<BaseItem>();

            var allItems = movies
                .Concat(series)
                .GroupBy(i => $"{i.GetType().Name}:{i.GetProviderId(MetadataProviders.Tmdb)}")
                .Select(g => g.First())
                .ToList();

            var total = allItems.Count;
            logger.Info(
                "Bangumi 角色增强计划任务：扫描媒体条目 {0} 个，待刷新电影/剧集 {1} 个",
                scopedItems.Count,
                total);

            if (total == 0)
            {
                progress.Report(100.0);
                logger.Info("Bangumi 角色增强计划任务完成，条目数 0");
                return;
            }

            var options = new MetadataRefreshOptions(new DirectoryService(logger, Plugin.FileSystem))
            {
                Recursive = true,
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.Default,
                ReplaceAllMetadata = true,
                ReplaceAllImages = false,
                ReplaceThumbnailImages = false,
                EnableThumbnailImageExtraction = false,
                EnableSubtitleDownloading = false,
                IsAutomated = true
            };

            var enhanced = 0;
            var skipped = 0;

            for (var i = 0; i < allItems.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    logger.Info("Bangumi 角色增强计划任务已取消 ({0}/{1})", i, total);
                    break;
                }

                var item = allItems[i];
                var tmdbId = item.GetProviderId(MetadataProviders.Tmdb);
                logger.Debug("Bangumi 角色增强计划任务：处理 {0}/{1} tmdbId={2} name={3}", i + 1, total, tmdbId, item.Name);

                try
                {
                    await MetaDataRunner.RefreshMetaDataAsync(item.InternalId, options, cancellationToken);
                    enhanced++;
                }
                catch (Exception ex)
                {
                    skipped++;
                    logger.Error("Bangumi 角色增强计划任务异常 [{0}]: {1}", item.Name, ex.Message);
                }

                progress.Report((double)(i + 1) / total * 100);
            }

            logger.Info("Bangumi 角色增强计划任务完成：处理 {0} 个，失败 {1} 个，总计 {2} 个", enhanced, skipped, total);
            progress.Report(100.0);
        }
    }
}
