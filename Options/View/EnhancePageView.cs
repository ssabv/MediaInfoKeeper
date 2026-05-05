namespace MediaInfoKeeper.Options.View
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Persistence;
    using Emby.Web.GenericEdit.Elements;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI.Views;
    using MediaInfoKeeper.Options;
    using MediaInfoKeeper.Options.Store;
    using MediaInfoKeeper.Options.UIBaseClasses.Views;
    using MediaInfoKeeper.Patch;

    internal class EnhancePageView : PluginPageView
    {
        private const string OptimizeDatabaseCommandId = "enhance.optimizeDatabase";
        private readonly EnhanceOptionsStore store;
        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;
        private readonly IItemRepository itemRepository;

        public EnhancePageView(PluginInfo pluginInfo, EnhanceOptionsStore store)
            : base(pluginInfo.Id)
        {
            this.store = store;
            this.logger = Plugin.Instance.AppHost.Resolve<ILogManager>().GetLogger(Plugin.PluginName);
            this.libraryManager = Plugin.LibraryManager;
            this.itemRepository = Plugin.Instance.ItemRepository;
            this.ContentData = store.GetOptions();
        }

        public EnhanceOptions Options => this.ContentData as EnhanceOptions;

        public override async Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            if (string.Equals(commandId, OptimizeDatabaseCommandId, StringComparison.Ordinal))
            {
                await Task.Run(() => ExecuteOptimizeDatabase()).ConfigureAwait(false);
                this.ContentData = this.store.GetOptions();
                return this;
            }

            return await base.RunCommand(itemId, commandId, data).ConfigureAwait(false);
        }

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            this.store.SetOptions(this.Options);
            this.ContentData = this.store.GetOptions();
            return base.OnSaveCommand(itemId, commandId, data);
        }

        private void ExecuteOptimizeDatabase(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.logger.Info("开始执行: 优化数据库（重建搜索索引 + 清理裂图记录）");

            var rebuilt = ChineseSearch.RebuildSearchIndex();
            if (!rebuilt)
            {
                throw new InvalidOperationException("重建搜索索引失败，请查看 Emby 系统日志中 EnhanceChineseSearch 相关记录。");
            }

            var updatedItems = 0;
            var removedItemImages = 0;
            var removedChapterImages = 0;
            var items = this.libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true
            }, cancellationToken);

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var currentRemovedItemImages = RemoveBrokenItemImages(item);
                var currentRemovedChapterImages = RemoveBrokenChapterImages(item);
                if (currentRemovedItemImages > 0 || currentRemovedChapterImages > 0)
                {
                    updatedItems++;
                    removedItemImages += currentRemovedItemImages;
                    removedChapterImages += currentRemovedChapterImages;
                }
            }

            this.logger.Info(
                $"数据库优化完成: 重建搜索索引 True，删除条目图片 {removedItemImages} 条，删除章节图片 {removedChapterImages} 条，影响条目 {updatedItems} 个");
        }

        private int RemoveBrokenItemImages(BaseItem item)
        {
            var existingImages = item.ImageInfos ?? Array.Empty<ItemImageInfo>();
            var brokenImages = existingImages
                .Where(i => i != null && IsBrokenPath(i.Path))
                .ToList();

            if (brokenImages.Count == 0)
            {
                return 0;
            }

            item.RemoveImages(brokenImages);
            this.itemRepository.SaveImages(item.InternalId, item.ImageInfos ?? Array.Empty<ItemImageInfo>());
            return brokenImages.Count;
        }

        private int RemoveBrokenChapterImages(BaseItem item)
        {
            var chapters = this.itemRepository.GetChapters(item) ?? new List<ChapterInfo>();
            var removed = 0;

            foreach (var chapter in chapters)
            {
                if (!IsBrokenPath(chapter?.ImagePath))
                {
                    continue;
                }

                chapter.ImagePath = null;
                chapter.ImageTag = null;
                chapter.ImageDateModified = default;
                removed++;
            }

            if (removed == 0)
            {
                return 0;
            }

            this.itemRepository.SaveChapters(item.InternalId, chapters);
            return removed;
        }

        private static bool IsBrokenPath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && !File.Exists(path);
        }
    }
}
