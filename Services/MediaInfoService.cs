using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaInfoKeeper.Patch;
using MediaInfoKeeper.Store;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Services
{
    public class MediaInfoService
    {
        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;
        private readonly IMediaSourceManager mediaSourceManager;
        private readonly IFileSystem fileSystem;

        /// <summary>创建 MediaInfo 处理辅助类并注入所需服务。</summary>
        public MediaInfoService(
            ILibraryManager libraryManager,
            IMediaSourceManager mediaSourceManager,
            IFileSystem fileSystem)
        {
            this.logger = Plugin.SharedLogger;
            this.libraryManager = libraryManager;
            this.mediaSourceManager = mediaSourceManager;
            this.fileSystem = fileSystem;
        }

        /// <summary>判断条目是否已存在可用的 MediaInfo。</summary>
        public bool HasMediaInfo(BaseItem item)
        {
            if (item is not IHasMediaSources || !item.RunTimeTicks.HasValue)
            {
                return false;
            }

            return this.mediaSourceManager.GetMediaStreams(item.InternalId)
                .Any(stream => stream != null &&
                               (stream.Type == MediaStreamType.Audio || stream.Type == MediaStreamType.Video));
        }

        /// <summary>判断条目当前 MediaInfo 中是否存在音频流。</summary>
        public bool HasAudioStream(BaseItem item)
        {
            return HasStreamType(item, MediaStreamType.Audio);
        }

        /// <summary>获取指定条目的静态媒体源。</summary>
        public List<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enableAlternateMediaSources)
        {
            if (item is not IHasMediaSources)
            {
                return new List<MediaSourceInfo>();
            }

            var collectionFolders = this.libraryManager.GetCollectionFolders(item).Cast<BaseItem>().ToArray();
            var libraryOptions = this.libraryManager.GetLibraryOptions(item);

            return this.mediaSourceManager.GetStaticMediaSources(
                item,
                enableAlternateMediaSources,
                enablePathSubstitution: false,
                fillChapters: false,
                collectionFolders: collectionFolders,
                libraryOptions: libraryOptions,
                deviceProfile: null,
                user: null);
        }

        /// <summary>构建 MediaInfo 提取所需的刷新选项。</summary>
        private MetadataRefreshOptions GetMediaInfoRefreshOptions()
        {
            var options = new MetadataRefreshOptions(new DirectoryService(this.logger, this.fileSystem));
            ApplyMediaInfoOnlyRefreshOptions(options);
            return options;
        }

        private static void ApplyMediaInfoOnlyRefreshOptions(MetadataRefreshOptions options)
        {
            options.EnableRemoteContentProbe = true;
            options.MetadataRefreshMode = MetadataRefreshMode.ValidationOnly;
            options.ReplaceAllMetadata = false;
            options.ImageRefreshMode = MetadataRefreshMode.ValidationOnly;
            options.ReplaceAllImages = false;
            options.EnableThumbnailImageExtraction = false;
            options.EnableSubtitleDownloading = false;
        }

        public MediaInfoDocument.MediaInfoRestoreResult RestorePersistedMediaInfo(BaseItem item)
        {
            if (item is not Video && item is not Audio)
            {
                return MediaInfoDocument.MediaInfoRestoreResult.Failed;
            }

            var result = Plugin.MediaSourceInfoStore.ApplyToItem(item);
            if (item is Video)
            {
                Plugin.ChaptersStore.ApplyToItem(item);
            }
            else if (item is Audio)
            {
                Plugin.EmbeddedInfoStore.ApplyToItem(item);
            }

            return result;
        }

        public MediaInfoDocument.MediaInfoRestoreResult RestorePersistedMediaInfoForExistingSource(BaseItem item, string source)
        {
            var displayName = item?.FileName ?? item?.Path ?? item?.Name;
            if (item is not Video && item is not Audio)
            {
                this.logger.Info($"{source} 跳过 条目非音视频: {displayName}");
                return MediaInfoDocument.MediaInfoRestoreResult.Failed;
            }

            using (FfProcessGuard.Allow())
            {
                var filePath = item.Path;
                if (string.IsNullOrEmpty(filePath))
                {
                    this.logger.Info($"{source} 跳过 无路径: {displayName}");
                    return MediaInfoDocument.MediaInfoRestoreResult.Failed;
                }

                if (IsMissingLocalFile(filePath, GetMediaInfoRefreshOptions().DirectoryService))
                {
                    this.logger.Info($"{source} 跳过 文件不存在: {displayName}");
                    return MediaInfoDocument.MediaInfoRestoreResult.Failed;
                }

                return RestorePersistedMediaInfo(item);
            }
        }

        internal async Task<bool> ExtractMediaInfoAsync(
            BaseItem item,
            string source,
            CancellationToken cancellationToken,
            MediaStreamType[] requiredStreamTypes = null)
        {
            if (!(item is Video) && !(item is Audio))
            {
                return false;
            }

            var displayName = item.FileName ?? item.Path ?? item.Name;
            var maxAttempts = Math.Max(1, Plugin.Instance.Options.MediaInfo.ExtractMediaInfoAttemptCount);

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HasRequiredMediaInfo(item, requiredStreamTypes))
                {
                    this.logger.Info($"{source} 提取媒体信息队列出队 已存在媒体流: {displayName}");
                    return true;
                }

                var result = await ExtractMediaInfoOnceAsync(
                        item,
                        source,
                        displayName,
                        attempt,
                        maxAttempts,
                        requiredStreamTypes,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (result == MediaInfoExtractionAttemptResult.Succeeded)
                {
                    return true;
                }

                if (result == MediaInfoExtractionAttemptResult.NotRetryable)
                {
                    return false;
                }

            }

            return false;
        }

        private async Task<MediaInfoExtractionAttemptResult> ExtractMediaInfoOnceAsync(
            BaseItem item,
            string source,
            string displayName,
            int attempt,
            int maxAttempts,
            MediaStreamType[] requiredStreamTypes,
            CancellationToken cancellationToken)
        {
            if (!(item is Video) && !(item is Audio))
            {
                return MediaInfoExtractionAttemptResult.NotRetryable;
            }

            using (FfProcessGuard.Allow())
            {
                var filePath = item.Path;
                if (string.IsNullOrEmpty(filePath))
                {
                    this.logger.Info($"{source} 提取媒体信息跳过 无路径: {displayName}");
                    return MediaInfoExtractionAttemptResult.NotRetryable;
                }

                var refreshOptions = GetMediaInfoRefreshOptions();
                var directoryService = refreshOptions.DirectoryService;

                if (IsMissingLocalFile(filePath, directoryService))
                {
                    this.logger.Info($"{source} 提取媒体信息跳过 文件不存在: {displayName}");
                    return MediaInfoExtractionAttemptResult.NotRetryable;
                }

                var deserializeResult = RestorePersistedMediaInfo(item);

                if (deserializeResult == MediaInfoDocument.MediaInfoRestoreResult.Restored ||
                    deserializeResult == MediaInfoDocument.MediaInfoRestoreResult.AlreadyExists)
                {
                    if (HasRequiredMediaInfo(item, requiredStreamTypes))
                    {
                        this.logger.Info($"{source} 提取媒体信息队列出队 已存在媒体流: {displayName}");
                        return MediaInfoExtractionAttemptResult.Succeeded;
                    }

                    this.logger.Info($"{source} 提取媒体信息恢复后仍无媒体流，继续刷新: {displayName}");
                }

                var startAttemptSuffix = maxAttempts > 1 && attempt > 1 ? $" 第 {attempt}/{maxAttempts} 次" : string.Empty;
                this.logger.Info($"{source} 提取开始{startAttemptSuffix}: {displayName}");

                var collectionFolders = Plugin.LibraryManager.GetCollectionFolders(item).Cast<BaseItem>().ToArray();
                var libraryOptions = Plugin.LibraryManager.GetLibraryOptions(item);
                await Plugin.ProviderManager
                    .RefreshSingleItem(item, refreshOptions, collectionFolders, libraryOptions, cancellationToken)
                    .ConfigureAwait(false);

                if (!HasRequiredMediaInfo(item, requiredStreamTypes))
                {
                    return MediaInfoExtractionAttemptResult.RetryableFailure;
                }

                this.logger.Info($"{source} 提取成功: {displayName}");
                return MediaInfoExtractionAttemptResult.Succeeded;
            }
        }

        private bool HasRequiredMediaInfo(BaseItem item, MediaStreamType[] requiredStreamTypes)
        {
            if (requiredStreamTypes == null || requiredStreamTypes.Length == 0)
            {
                return HasMediaInfo(item);
            }

            foreach (var streamType in requiredStreamTypes.Distinct())
            {
                if (!HasStreamType(item, streamType))
                {
                    return false;
                }
            }

            return true;
        }

        private bool HasStreamType(BaseItem item, MediaStreamType streamType)
        {
            if (item is not IHasMediaSources || !item.RunTimeTicks.HasValue)
            {
                return false;
            }

            return this.mediaSourceManager.GetMediaStreams(item.InternalId)
                .Any(stream => stream?.Type == streamType);
        }

        private static bool IsMissingLocalFile(string filePath, IDirectoryService directoryService)
        {
            if (Uri.TryCreate(filePath, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri &&
                uri.Scheme == Uri.UriSchemeFile)
            {
                var file = directoryService.GetFile(filePath);
                return file?.Exists != true;
            }

            return false;
        }

        private enum MediaInfoExtractionAttemptResult
        {
            Succeeded,
            RetryableFailure,
            NotRetryable
        }
    }
}
