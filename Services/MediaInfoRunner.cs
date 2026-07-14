using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace MediaInfoKeeper.Services
{
    /// <summary>
    /// 媒体信息提取 runner，统一去重并限制 ffprobe/ffmpeg 提取并发。
    /// </summary>
    public static class MediaInfoRunner
    {
        /// <summary>
        /// runner 当前队列状态，用于插件设置页展示。
        /// </summary>
        public sealed class QueueStats
        {
            /// <summary>已进入 runner 但尚未获取并发槽的任务数。</summary>
            public int Waiting { get; set; }

            /// <summary>已获取并发槽并正在执行的任务数。</summary>
            public int Running { get; set; }

            /// <summary>当前配置允许的最大并发数。</summary>
            public int MaxConcurrent { get; set; }

            /// <summary>尚未完成的任务总数。</summary>
            public int Total => Waiting + Running;
        }

        private sealed class ExtractionRequest
        {
            public long InternalId { get; set; }

            public string Source { get; set; }

            public RefreshPriority Priority { get; set; }

            public CancellationToken CancellationToken { get; set; }

            public MediaStreamType[] RequiredStreamTypes { get; set; }

            public TaskCompletionSource<bool> Completion { get; set; }

            public bool Started { get; set; }

            public bool Disabled { get; set; }
        }

        private static readonly object QueueSync = new object();
        private static readonly Queue<ExtractionRequest> HighestExtractionQueue =
            new Queue<ExtractionRequest>();
        private static readonly Queue<ExtractionRequest> HighExtractionQueue =
            new Queue<ExtractionRequest>();
        private static readonly Queue<ExtractionRequest> NormalExtractionQueue =
            new Queue<ExtractionRequest>();
        private static readonly Queue<ExtractionRequest> LowExtractionQueue =
            new Queue<ExtractionRequest>();
        private static readonly Dictionary<long, ExtractionRequest> InFlightExtractions =
            new Dictionary<long, ExtractionRequest>();

        private static int maxConcurrentCount = 1;
        private static int activeCount;
        private static int waitingCount;

        /// <summary>
        /// 更新媒体信息 runner 的运行配置，避免执行热路径反复读取插件配置。
        /// </summary>
        /// <param name="maxConcurrent">最大并发数。</param>
        public static void Configure(int maxConcurrent)
        {
            Volatile.Write(ref maxConcurrentCount, Math.Max(1, maxConcurrent));

            lock (QueueSync)
            {
                StartWorkersInsideLock();
            }
        }

        /// <summary>
        /// 获取媒体信息提取 runner 的实时队列状态。
        /// </summary>
        public static QueueStats GetQueueStats()
        {
            return new QueueStats
            {
                Waiting = Math.Max(0, Volatile.Read(ref waitingCount)),
                Running = Math.Max(0, Volatile.Read(ref activeCount)),
                MaxConcurrent = GetMaxConcurrent()
            };
        }

        /// <summary>
        /// 提取单个音视频条目的媒体信息，同一条目重复请求会复用正在运行的任务。
        /// </summary>
        /// <param name="internalId">Emby 条目内部 ID。</param>
        /// <param name="source">日志来源。</param>
        /// <param name="cancellationToken">取消标记。</param>
        /// <param name="requiredStreamTypes">完成后必须存在的媒体流类型。</param>
        /// <returns>提取成功返回 true，否则返回 false。</returns>
        public static async Task<bool> ExtractMediaInfoAsync(
            long internalId,
            string source = "媒体信息提取",
            CancellationToken cancellationToken = default,
            MediaStreamType[] requiredStreamTypes = null,
            RefreshPriority priority = RefreshPriority.Normal,
            bool replaceQueued = false)
        {
            if (internalId <= 0)
            {
                return false;
            }

            var extractionTask = QueueExtraction(
                internalId,
                source,
                cancellationToken,
                requiredStreamTypes,
                priority,
                replaceQueued);
            return await extractionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 如果指定条目正在提取媒体信息，则等待该提取任务完成；没有正在运行的任务时直接返回。
        /// </summary>
        public static async Task<bool> WaitForItemFinishAsync(
            long internalId,
            CancellationToken cancellationToken = default)
        {
            Task<bool> extractionTask = null;
            lock (QueueSync)
            {
                if (InFlightExtractions.TryGetValue(internalId, out var existing) &&
                    existing.Disabled != true)
                {
                    extractionTask = existing.Completion.Task;
                }
            }

            return extractionTask == null ||
                   await extractionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private static Task<bool> QueueExtraction(
            long internalId,
            string source,
            CancellationToken cancellationToken,
            MediaStreamType[] requiredStreamTypes,
            RefreshPriority priority = RefreshPriority.Normal,
            bool replaceQueued = false)
        {
            lock (QueueSync)
            {
                if (InFlightExtractions.TryGetValue(internalId, out var existing))
                {
                    if (!existing.Started &&
                        !existing.Disabled &&
                        (replaceQueued || priority < existing.Priority))
                    {
                        existing.Disabled = true;
                        waitingCount = Math.Max(0, waitingCount - 1);
                        Volatile.Write(ref waitingCount, waitingCount);

                        var replacement = new ExtractionRequest
                        {
                            InternalId = internalId,
                            Source = source,
                            Priority = priority,
                            CancellationToken = cancellationToken,
                            RequiredStreamTypes = requiredStreamTypes,
                            Completion = existing.Completion
                        };

                        InFlightExtractions[internalId] = replacement;
                        GetQueue(priority).Enqueue(replacement);
                        waitingCount++;
                        Volatile.Write(ref waitingCount, waitingCount);
                        StartWorkersInsideLock();
                    }

                    return existing.Completion.Task;
                }

                var request = new ExtractionRequest
                {
                    InternalId = internalId,
                    Source = source,
                    Priority = priority,
                    CancellationToken = cancellationToken,
                    RequiredStreamTypes = requiredStreamTypes,
                    Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                };

                InFlightExtractions[internalId] = request;
                GetQueue(priority).Enqueue(request);
                waitingCount++;
                Volatile.Write(ref waitingCount, waitingCount);
                StartWorkersInsideLock();
                return request.Completion.Task;
            }
        }

        private static Queue<ExtractionRequest> GetQueue(RefreshPriority priority)
        {
            switch (priority)
            {
                case RefreshPriority.Highest:
                    return HighestExtractionQueue;
                case RefreshPriority.High:
                    return HighExtractionQueue;
                case RefreshPriority.Low:
                    return LowExtractionQueue;
                case RefreshPriority.Normal:
                default:
                    return NormalExtractionQueue;
            }
        }

        private static void StartWorkersInsideLock()
        {
            var maxConcurrent = GetMaxConcurrent();
            while (activeCount < maxConcurrent && waitingCount > 0)
            {
                activeCount++;
                Volatile.Write(ref activeCount, activeCount);
                _ = Task.Run(ProcessQueueAsync);
            }
        }

        private static async Task ProcessQueueAsync()
        {
            while (true)
            {
                ExtractionRequest request;
                lock (QueueSync)
                {
                    if (waitingCount == 0 || activeCount > GetMaxConcurrent())
                    {
                        activeCount = Math.Max(0, activeCount - 1);
                        Volatile.Write(ref activeCount, activeCount);
                        return;
                    }

                    request = TakeNextRequestInsideLock();
                }

                await ProcessExtractionAsync(request).ConfigureAwait(false);
            }
        }

        private static ExtractionRequest TakeNextRequestInsideLock()
        {
            while (true)
            {
                var request = GetNextQueueInsideLock().Dequeue();
                if (request.Disabled)
                {
                    continue;
                }

                request.Started = true;
                waitingCount--;
                Volatile.Write(ref waitingCount, waitingCount);
                return request;
            }
        }

        private static Queue<ExtractionRequest> GetNextQueueInsideLock()
        {
            if (HighestExtractionQueue.Count > 0)
            {
                return HighestExtractionQueue;
            }

            if (HighExtractionQueue.Count > 0)
            {
                return HighExtractionQueue;
            }

            return NormalExtractionQueue.Count > 0
                ? NormalExtractionQueue
                : LowExtractionQueue;
        }

        private static async Task ProcessExtractionAsync(ExtractionRequest request)
        {
            try
            {
                request.CancellationToken.ThrowIfCancellationRequested();

                var item = Plugin.LibraryManager?.GetItemById(request.InternalId) as BaseItem;
                var result = item != null &&
                             await Plugin.MediaInfoService
                                 .ExtractMediaInfoAsync(
                                     item,
                                     request.Source,
                                     request.CancellationToken,
                                     request.RequiredStreamTypes)
                                 .ConfigureAwait(false);

                request.Completion.TrySetResult(result);
            }
            catch (OperationCanceledException ex)
            {
                request.Completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                var displayName = Plugin.LibraryManager?.GetItemById(request.InternalId) is BaseItem item
                    ? item.FileName ?? item.Path ?? item.Name
                    : request.InternalId.ToString();
                Plugin.SharedLogger?.Error($"{request.Source} 媒体信息提取失败 item={displayName}");
                Plugin.SharedLogger?.Error(ex.Message);
                Plugin.SharedLogger?.Debug(ex.StackTrace);
                request.Completion.TrySetResult(false);
            }
            finally
            {
                lock (QueueSync)
                {
                    if (InFlightExtractions.TryGetValue(request.InternalId, out var current) &&
                        ReferenceEquals(current, request))
                    {
                        InFlightExtractions.Remove(request.InternalId);
                    }

                    StartWorkersInsideLock();
                }
            }
        }

        private static int GetMaxConcurrent()
        {
            return Math.Max(1, Volatile.Read(ref maxConcurrentCount));
        }
    }
}
