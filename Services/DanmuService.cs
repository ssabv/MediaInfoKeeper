using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Logging;
using MediaInfoKeeper.Common;

namespace MediaInfoKeeper.Services
{
    public class DanmuService
    {
        public sealed class DanmuDownloadResult
        {
            public bool Succeeded { get; set; }

            public string Reason { get; set; }
        }

        private sealed class QueuedDanmuItem
        {
            public long InternalId { get; set; }

            public bool OverwriteExisting { get; set; }

            public TaskCompletionSource<bool> CompletionSource { get; set; }

            public TaskCompletionSource<DanmuDownloadResult> DetailCompletionSource { get; set; }
        }

        private static readonly TimeSpan QueueIntervalDelay = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan FetchCacheDuration = TimeSpan.FromHours(6);
        private const string FetchCacheScope = "danmu-fetch";

        private readonly ILogger logger;
        private readonly IHttpClient httpClient;
        private readonly ConcurrentQueue<QueuedDanmuItem> itemAddedQueue = new ConcurrentQueue<QueuedDanmuItem>();
        private readonly SemaphoreSlim queueSignal = new SemaphoreSlim(0);
        private int queueWorkerStarted;

        public DanmuService(ILogManager logManager, IHttpClient httpClient)
        {
            this.logger = Plugin.SharedLogger ?? logManager.GetLogger(Plugin.PluginName);
            this.httpClient = httpClient;
        }

        public bool IsEnabled =>
            Plugin.Instance?.Options?.MetaData?.EnableDanmuApi == true &&
            !string.IsNullOrWhiteSpace(Plugin.Instance?.Options?.MetaData?.DanmuApiBaseUrl);

        public bool IsSupportedItem(BaseItem item)
        {
            return item is Episode || item is Movie;
        }

        public Task<bool> QueueDownloadAsync(long internalId, bool overwriteExisting, CancellationToken cancellationToken)
        {
            var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() => completionSource.TrySetCanceled(cancellationToken));
            }

            var item = Plugin.LibraryManager?.GetItemById(internalId);
            if (!overwriteExisting && ShouldSkipAutoDownload(item))
            {
                this.logger.Debug($"弹幕下载: 跳过 {item?.FileName} 文件已存在");
                completionSource.TrySetResult(false);
                return completionSource.Task;
            }

            Enqueue(internalId, overwriteExisting, completionSource);
            return completionSource.Task;
        }

        public Task<DanmuDownloadResult> QueueDownloadWithReasonAsync(long internalId, bool overwriteExisting, CancellationToken cancellationToken)
        {
            var completionSource = new TaskCompletionSource<DanmuDownloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() => completionSource.TrySetCanceled(cancellationToken));
            }

            var item = Plugin.LibraryManager?.GetItemById(internalId);
            if (!overwriteExisting && ShouldSkipAutoDownload(item))
            {
                completionSource.TrySetResult(new DanmuDownloadResult
                {
                    Succeeded = false,
                    Reason = "已存在弹幕文件"
                });
                return completionSource.Task;
            }

            Enqueue(internalId, overwriteExisting, detailCompletionSource: completionSource);
            return completionSource.Task;
        }

        private void Enqueue(long internalId, bool overwriteExisting, TaskCompletionSource<bool> completionSource)
        {
            itemAddedQueue.Enqueue(new QueuedDanmuItem
            {
                InternalId = internalId,
                OverwriteExisting = overwriteExisting,
                CompletionSource = completionSource
            });
            queueSignal.Release();

            if (Interlocked.CompareExchange(ref queueWorkerStarted, 1, 0) == 0)
            {
                _ = Task.Run(ProcessItemAddedQueueAsync);
            }
        }

        private void Enqueue(long internalId, bool overwriteExisting, TaskCompletionSource<DanmuDownloadResult> detailCompletionSource)
        {
            itemAddedQueue.Enqueue(new QueuedDanmuItem
            {
                InternalId = internalId,
                OverwriteExisting = overwriteExisting,
                DetailCompletionSource = detailCompletionSource
            });
            queueSignal.Release();

            if (Interlocked.CompareExchange(ref queueWorkerStarted, 1, 0) == 0)
            {
                _ = Task.Run(ProcessItemAddedQueueAsync);
            }
        }

        private async Task ProcessItemAddedQueueAsync()
        {
            while (true)
            {
                try
                {
                    await queueSignal.WaitAsync().ConfigureAwait(false);
                    if (!itemAddedQueue.TryDequeue(out var queuedItem) || queuedItem == null)
                    {
                        continue;
                    }

                    try
                    {
                        var result = await ProcessQueuedItemAsync(queuedItem).ConfigureAwait(false);
                        queuedItem.CompletionSource?.TrySetResult(result.Succeeded);
                        queuedItem.DetailCompletionSource?.TrySetResult(result);
                    }
                    catch (Exception ex)
                    {
                        queuedItem.CompletionSource?.TrySetResult(false);
                        queuedItem.DetailCompletionSource?.TrySetResult(new DanmuDownloadResult
                        {
                            Succeeded = false,
                            Reason = ex.Message
                        });
                        this.logger.Debug($"弹幕下载: 失败 {queuedItem.InternalId} {ex.Message}");
                        this.logger.Debug(ex.StackTrace);
                    }
                    await Task.Delay(QueueIntervalDelay).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.logger.Debug($"弹幕下载: 失败 queue {ex.Message}");
                    this.logger.Debug(ex.StackTrace);
                }
            }
        }

        private async Task<DanmuDownloadResult> ProcessQueuedItemAsync(QueuedDanmuItem queuedItem)
        {
            if (queuedItem == null)
            {
                return Failed("队列项为空");
            }

            var internalId = queuedItem.InternalId;
            var currentItem = Plugin.LibraryManager?.GetItemById(internalId);
            if (currentItem == null || Plugin.Instance == null || Plugin.Instance.Options.MainPage?.PlugginEnabled != true || !IsSupportedItem(currentItem))
            {
                return Failed("条目无效或插件未启用");
            }

            if (!IsEnabled)
            {
                return Failed("弹幕 API 未启用");
            }

            return await TryDownloadDanmuXmlDetailedAsync(currentItem, CancellationToken.None, queuedItem.OverwriteExisting).ConfigureAwait(false);
        }

        public bool ShouldSkipAutoDownload(BaseItem item)
        {
            if (item == null)
            {
                return true;
            }

            if (item is not Episode && item is not Movie)
            {
                return true;
            }

            if (ShouldAlwaysFetchLatest())
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(item.ContainingFolderPath) || string.IsNullOrWhiteSpace(item.FileNameWithoutExtension))
            {
                return false;
            }

            var targetPath = Path.Combine(item.ContainingFolderPath, item.FileNameWithoutExtension + ".xml");
            return File.Exists(targetPath);
        }

        public string GetDanmuXmlPath(BaseItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.ContainingFolderPath) || string.IsNullOrWhiteSpace(item.FileNameWithoutExtension))
            {
                return null;
            }

            return Path.Combine(item.ContainingFolderPath, item.FileNameWithoutExtension + ".xml");
        }

        public async Task<bool> TryDownloadDanmuXmlAsync(BaseItem item, CancellationToken cancellationToken, bool overwriteExisting = false)
        {
            var result = await TryDownloadDanmuXmlDetailedAsync(item, cancellationToken, overwriteExisting).ConfigureAwait(false);
            return result.Succeeded;
        }

        public async Task<DanmuDownloadResult> TryDownloadDanmuXmlDetailedAsync(BaseItem item, CancellationToken cancellationToken, bool overwriteExisting = false)
        {
            if (!IsEnabled || item == null)
            {
                return Failed("弹幕 API 未启用或条目为空");
            }

            if (item is not Episode && item is not Movie)
            {
                return Failed("条目类型不支持");
            }

            if (string.IsNullOrWhiteSpace(item.ContainingFolderPath) || string.IsNullOrWhiteSpace(item.FileNameWithoutExtension))
            {
                return Failed("路径信息不完整");
            }

            var overwrite = overwriteExisting || ShouldAlwaysFetchLatest();
            var targetPath = GetDanmuXmlPath(item);
            if (!overwrite && File.Exists(targetPath))
            {
                return Failed("已存在弹幕文件");
            }

            var fetchResult = await FetchDanmuXmlDetailedAsync(item, cancellationToken).ConfigureAwait(false);
            if (fetchResult.XmlBytes == null || fetchResult.XmlBytes.Length == 0)
            {
                if (!string.IsNullOrWhiteSpace(fetchResult.Reason))
                {
                    return Failed(fetchResult.Reason);
                }

                return Failed("未获取到内容");
            }

            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(targetPath, fetchResult.XmlBytes, cancellationToken).ConfigureAwait(false);

            this.logger.Info($"Danmu 下载成功: {FormatItemForLog(item)}");
            return Succeeded();
        }

        public async Task<byte[]> FetchDanmuXmlBytesAsync(BaseItem item, CancellationToken cancellationToken)
        {
            var result = await FetchDanmuXmlDetailedAsync(item, cancellationToken).ConfigureAwait(false);
            return result.XmlBytes;
        }

        public async Task<DanmuFetchResult> FetchDanmuXmlDetailedForApiAsync(BaseItem item, CancellationToken cancellationToken)
        {
            return await FetchDanmuXmlDetailedAsync(item, cancellationToken).ConfigureAwait(false);
        }

        public sealed class DanmuFetchResult
        {
            public byte[] XmlBytes { get; set; }

            public string Reason { get; set; }
        }

        public bool TryGetCachedDanmuXmlBytes(BaseItem item, out byte[] xmlBytes)
        {
            xmlBytes = null;
            if (item == null || !TryBuildSearchRequest(item, out var animeTitle, out var episodeNumber))
            {
                return false;
            }

            var cacheKey = BuildSearchCacheKey(animeTitle, episodeNumber);
            var cachedXmlBytes = PluginDiskCache.GetBytes(FetchCacheScope, cacheKey, FetchCacheDuration, ".xml");
            if (cachedXmlBytes == null || cachedXmlBytes.Length == 0)
            {
                return false;
            }

            xmlBytes = cachedXmlBytes;
            return true;
        }

        private async Task<DanmuFetchResult> FetchDanmuXmlDetailedAsync(BaseItem item, CancellationToken cancellationToken)
        {
            if (!IsEnabled || item == null)
            {
                return new DanmuFetchResult { Reason = "弹幕 API 未启用或条目为空" };
            }

            if (item is not Episode && item is not Movie)
            {
                return new DanmuFetchResult { Reason = "条目类型不支持" };
            }

            if (!TryBuildSearchRequest(item, out var animeTitle, out var episodeNumber))
            {
                return new DanmuFetchResult { Reason = "无法解析标题或集数" };
            }

            var baseUrl = Plugin.Instance?.Options?.MetaData?.DanmuApiBaseUrl?.Trim();
            if (TryGetCachedDanmuXmlBytes(item, out var cachedXmlBytes))
            {
                return new DanmuFetchResult { XmlBytes = cachedXmlBytes };
            }

            var cacheKey = BuildSearchCacheKey(animeTitle, episodeNumber);
            var episodeId = await SearchEpisodeIdAsync(baseUrl, animeTitle, episodeNumber, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(episodeId))
            {
                return new DanmuFetchResult { Reason = "未匹配到条目" };
            }

            var xmlBytes = await DownloadXmlAsync(baseUrl, episodeId, cancellationToken).ConfigureAwait(false);
            if (xmlBytes == null || xmlBytes.Length == 0)
            {
                return new DanmuFetchResult { Reason = $"未获取到内容 episodeId={episodeId}" };
            }

            var success = new DanmuFetchResult { XmlBytes = xmlBytes };
            SetFetchCache(cacheKey, success);
            return success;
        }

        private static DanmuDownloadResult Succeeded()
        {
            return new DanmuDownloadResult
            {
                Succeeded = true,
                Reason = "成功"
            };
        }

        private static DanmuDownloadResult Failed(string reason)
        {
            return new DanmuDownloadResult
            {
                Succeeded = false,
                Reason = reason
            };
        }

        private static bool ShouldAlwaysFetchLatest()
        {
            return Plugin.Instance?.Options?.MetaData?.AlwaysFetchLatestDanmu == true;
        }

        private static string FormatItemForLog(BaseItem item)
        {
            if (item == null)
            {
                return "<unknown>";
            }

            return item.FileName ?? item.Path ?? item.Name ?? item.InternalId.ToString();
        }

        private static string BuildSearchCacheKey(string animeTitle, int episodeNumber)
        {
            return $"{NormalizeCacheKeyPart(animeTitle)}|{episodeNumber}";
        }

        private static string NormalizeCacheKeyPart(string value)
        {
            return (value ?? string.Empty).Trim().ToUpperInvariant();
        }

        private static void SetFetchCache(string cacheKey, DanmuFetchResult result)
        {
            PluginDiskCache.SetBytes(FetchCacheScope, cacheKey, result?.XmlBytes, ".xml");
        }

        private async Task<string> SearchEpisodeIdAsync(string baseUrl, string animeTitle, int episodeNumber, CancellationToken cancellationToken)
        {
            var requestUrl = BuildApiUrl(
                baseUrl,
                $"search/episodes?anime={Uri.EscapeDataString(animeTitle)}&episode={episodeNumber}");

            var requestOptions = new HttpRequestOptions
            {
                Url = requestUrl,
                CancellationToken = cancellationToken,
                AcceptHeader = "application/json",
                UserAgent = "MediaInfoKeeper",
                EnableDefaultUserAgent = false
            };

            using var response = await httpClient.SendAsync(requestOptions, "GET").ConfigureAwait(false);
            var body = await ReadResponseBodyAsync(response).ConfigureAwait(false);

            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
            {
                this.logger.Debug($"弹幕下载: 失败 {animeTitle} status={(int)response.StatusCode} url={requestUrl} body={body}");
                return null;
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                if (!document.RootElement.TryGetProperty("animes", out var animesElement) ||
                    animesElement.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (var animeElement in animesElement.EnumerateArray())
                {
                    if (!animeElement.TryGetProperty("episodes", out var episodesElement) ||
                        episodesElement.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var episodeElement in episodesElement.EnumerateArray())
                    {
                        if (!episodeElement.TryGetProperty("episodeId", out var episodeIdElement))
                        {
                            continue;
                        }

                        var episodeId = episodeIdElement.ValueKind == JsonValueKind.Number
                            ? episodeIdElement.GetInt64().ToString()
                            : episodeIdElement.GetString();
                        if (!string.IsNullOrWhiteSpace(episodeId))
                        {
                            return episodeId;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                this.logger.Debug($"弹幕下载: 失败 {animeTitle} 结果解析异常 url={requestUrl}");
                this.logger.Debug(ex.Message);
            }

            return null;
        }

        private async Task<byte[]> DownloadXmlAsync(string baseUrl, string episodeId, CancellationToken cancellationToken)
        {
            var requestUrl = BuildApiUrl(baseUrl, $"comment/{Uri.EscapeDataString(episodeId)}?format=xml");
            var requestOptions = new HttpRequestOptions
            {
                Url = requestUrl,
                CancellationToken = cancellationToken,
                AcceptHeader = "application/xml,text/xml;q=0.9,*/*;q=0.8",
                UserAgent = "MediaInfoKeeper",
                EnableDefaultUserAgent = false
            };

            using var response = await httpClient.GetResponse(requestOptions).ConfigureAwait(false);
            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
            {
                var body = await ReadStreamToStringAsync(response.Content).ConfigureAwait(false);
                this.logger.Debug($"弹幕下载: 失败 {episodeId} status={(int)response.StatusCode} url={requestUrl} body={body}");
                return null;
            }

            using var memoryStream = new MemoryStream();
            await response.Content.CopyToAsync(memoryStream, 81920, cancellationToken).ConfigureAwait(false);
            return memoryStream.ToArray();
        }

        private static bool TryBuildSearchRequest(BaseItem item, out string animeTitle, out int episodeNumber)
        {
            animeTitle = null;
            episodeNumber = 1;

            if (item is Episode episode)
            {
                animeTitle = ResolveEpisodeTitle(episode);
                if (!episode.IndexNumber.HasValue || episode.IndexNumber.Value <= 0)
                {
                    return false;
                }

                episodeNumber = episode.IndexNumber.Value;
                return !string.IsNullOrWhiteSpace(animeTitle);
            }

            if (item is Movie movie)
            {
                animeTitle = movie.Name?.Trim();
                return !string.IsNullOrWhiteSpace(animeTitle);
            }

            return false;
        }

        private static string ResolveEpisodeTitle(Episode item)
        {
            if (!string.IsNullOrWhiteSpace(item.SeriesName))
            {
                return item.SeriesName.Trim();
            }

            var series = item.Series;
            if (!string.IsNullOrWhiteSpace(series?.Name))
            {
                return series.Name.Trim();
            }

            return item.Name?.Trim();
        }

        private static string BuildApiUrl(string baseUrl, string relativePath)
        {
            var normalizedBaseUrl = (baseUrl ?? string.Empty).Trim();
            if (!normalizedBaseUrl.EndsWith("/", StringComparison.Ordinal))
            {
                normalizedBaseUrl += "/";
            }

            var combined = normalizedBaseUrl + "api/v2/" + relativePath.TrimStart('/');
            return Regex.Replace(combined, "(?<!:)/{2,}", "/");
        }

        private static async Task<string> ReadResponseBodyAsync(HttpResponseInfo response)
        {
            return await ReadStreamToStringAsync(response?.Content).ConfigureAwait(false);
        }

        private static async Task<string> ReadStreamToStringAsync(Stream stream)
        {
            if (stream == null)
            {
                return string.Empty;
            }

            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }
    }
}
