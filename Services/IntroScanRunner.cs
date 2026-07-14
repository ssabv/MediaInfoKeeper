using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaInfoKeeper.Patch;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace MediaInfoKeeper.Services
{
    public static class IntroScanRunner
    {
        private static readonly object runtimeLock = new object();
        private static readonly object QueueSync = new object();
        private static readonly Queue<IntroScanRequest> HighestScanQueue =
            new Queue<IntroScanRequest>();
        private static readonly Queue<IntroScanRequest> HighScanQueue =
            new Queue<IntroScanRequest>();
        private static readonly Queue<IntroScanRequest> NormalScanQueue =
            new Queue<IntroScanRequest>();
        private static readonly Queue<IntroScanRequest> LowScanQueue =
            new Queue<IntroScanRequest>();
        private static readonly Dictionary<long, IntroScanRequest> InFlightScans =
            new Dictionary<long, IntroScanRequest>();

        private static ILogger logger;
        private static ILibraryManager libraryManager;
        private static IFileSystem fileSystem;
        private static volatile AudioFingerprintRuntime audioFingerprintRuntime;
        private static int maxConcurrentCount = 1;
        private static int activeCount;
        private static int waitingCount;

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

        private sealed class IntroScanRequest
        {
            public long InternalId { get; set; }

            public string Source { get; set; }

            public RefreshPriority Priority { get; set; }

            public CancellationToken CancellationToken { get; set; }

            public TaskCompletionSource<bool> Completion { get; set; }

            public bool Started { get; set; }

            public bool Disabled { get; set; }
        }

        public static void Initialize(
            ILogManager logManager,
            ILibraryManager libraryManager,
            IFileSystem fileSystem)
        {
            IntroScanRunner.logger = Plugin.SharedLogger ?? logManager.GetLogger(Plugin.PluginName);
            IntroScanRunner.libraryManager = libraryManager;
            IntroScanRunner.fileSystem = fileSystem;
        }

        /// <summary>
        /// 更新片头扫描 runner 的运行配置，使用片头探测最大并发数。
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
        /// 获取片头扫描 runner 的实时队列状态。
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

        /// <summary>按配置并发度扫描一批剧集的片头标记。</summary>
        public static Task ScanEpisodesAsync(IReadOnlyList<Episode> episodes, CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (episodes == null || episodes.Count == 0)
            {
                progress?.Report(100.0);
                logger.Info("扫描完成，条目数 0");
                return Task.CompletedTask;
            }

            logger.Info($"片头扫描提交队列，总条目 {episodes.Count}");
            foreach (var episode in episodes)
            {
                _ = IntroScanRunner.ScanEpisodeAsync(episode, "计划任务片头扫描", cancellationToken, RefreshPriority.Normal);
            }

            progress?.Report(100.0);
            return Task.CompletedTask;
        }

        /// <summary>判断条目当前是否已经存在片头标记。</summary>
        public static bool HasIntroMarkers(BaseItem item)
        {
            return Plugin.IntroSkipChapterApi.GetIntroStart(item).HasValue ||
                   Plugin.IntroSkipChapterApi.GetIntroEnd(item).HasValue;
        }

        /// <summary>执行单个剧集的片头扫描，并汇总批量扫描进度。</summary>
        public static Task<bool> ScanEpisodeAsync(
            Episode episode,
            string source,
            CancellationToken cancellationToken = default,
            RefreshPriority priority = RefreshPriority.Normal,
            bool replaceQueued = false)
        {
            if (episode == null)
            {
                return Task.FromResult(false);
            }

            if (HasIntroMarkers(episode))
            {
                logger.Info($"{source} 片头扫描跳过: {episode.FileName ?? episode.Path} 已存在片头标记");
                return Task.FromResult(false);
            }

            return ScanEpisodeAsync(episode.InternalId, source, cancellationToken, priority, replaceQueued);
        }

        /// <summary>执行单个剧集的片头扫描，同一条目重复请求会复用正在运行的任务。</summary>
        public static async Task<bool> ScanEpisodeAsync(
            long internalId,
            string source = "片头扫描",
            CancellationToken cancellationToken = default,
            RefreshPriority priority = RefreshPriority.Normal,
            bool replaceQueued = false)
        {
            if (internalId <= 0)
            {
                return false;
            }

            var scanTask = QueueScan(internalId, source, cancellationToken, priority, replaceQueued);
            return await scanTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private static Task<bool> QueueScan(
            long internalId,
            string source,
            CancellationToken cancellationToken,
            RefreshPriority priority = RefreshPriority.Normal,
            bool replaceQueued = false)
        {
            lock (QueueSync)
            {
                if (InFlightScans.TryGetValue(internalId, out var existing))
                {
                    if (!existing.Started && !existing.Disabled &&
                        (replaceQueued || priority < existing.Priority))
                    {
                        existing.Disabled = true;
                        waitingCount = Math.Max(0, waitingCount - 1);
                        Volatile.Write(ref waitingCount, waitingCount);

                        var replacement = new IntroScanRequest
                        {
                            InternalId = internalId,
                            Source = source,
                            Priority = priority,
                            CancellationToken = cancellationToken,
                            Completion = existing.Completion
                        };

                        InFlightScans[internalId] = replacement;
                        GetQueue(priority).Enqueue(replacement);
                        waitingCount++;
                        Volatile.Write(ref waitingCount, waitingCount);
                        StartWorkersInsideLock();
                    }

                    return existing.Completion.Task;
                }

                var request = new IntroScanRequest
                {
                    InternalId = internalId,
                    Source = source,
                    Priority = priority,
                    CancellationToken = cancellationToken,
                    Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                };

                InFlightScans[internalId] = request;
                GetQueue(priority).Enqueue(request);
                waitingCount++;
                Volatile.Write(ref waitingCount, waitingCount);
                StartWorkersInsideLock();
                return request.Completion.Task;
            }
        }

        private static Queue<IntroScanRequest> GetQueue(RefreshPriority priority)
        {
            switch (priority)
            {
                case RefreshPriority.Highest:
                    return HighestScanQueue;
                case RefreshPriority.High:
                    return HighScanQueue;
                case RefreshPriority.Low:
                    return LowScanQueue;
                case RefreshPriority.Normal:
                default:
                    return NormalScanQueue;
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
                IntroScanRequest request;
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

                await ProcessIntroScanAsync(request).ConfigureAwait(false);
            }
        }

        private static IntroScanRequest TakeNextRequestInsideLock()
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

        private static Queue<IntroScanRequest> GetNextQueueInsideLock()
        {
            if (HighestScanQueue.Count > 0)
            {
                return HighestScanQueue;
            }

            if (HighScanQueue.Count > 0)
            {
                return HighScanQueue;
            }

            return NormalScanQueue.Count > 0
                ? NormalScanQueue
                : LowScanQueue;
        }

        private static async Task ProcessIntroScanAsync(IntroScanRequest request)
        {
            try
            {
                request.CancellationToken.ThrowIfCancellationRequested();

                var episode = libraryManager?.GetItemById(request.InternalId) as Episode;
                var result = episode != null &&
                             await RunEpisodeScanAsync(episode, request.Source, request.CancellationToken)
                                 .ConfigureAwait(false);

                request.Completion.TrySetResult(result);
            }
            catch (OperationCanceledException ex)
            {
                request.Completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                var displayName = libraryManager?.GetItemById(request.InternalId) is Episode episode
                    ? episode.FileName ?? episode.Path ?? episode.Name
                    : request.InternalId.ToString();
                logger?.Error($"{request.Source} 片头扫描失败 item={displayName}");
                logger?.Error(ex.Message);
                logger?.Debug(ex.StackTrace);
                request.Completion.TrySetResult(false);
            }
            finally
            {
                lock (QueueSync)
                {
                    if (InFlightScans.TryGetValue(request.InternalId, out var current) &&
                        ReferenceEquals(current, request))
                    {
                        InFlightScans.Remove(request.InternalId);
                    }
                }
            }
        }

        private static int GetMaxConcurrent()
        {
            return Math.Max(1, Volatile.Read(ref maxConcurrentCount));
        }

        private static async Task<bool> RunEpisodeScanAsync(
            Episode episode,
            string source,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                logger.Info("扫描已取消");
                return false;
            }

            logger.Debug($"{source}: 新的扫描任务 {episode.FileName ?? episode.Path}");

            episode = libraryManager.GetItemById(episode.InternalId) as Episode ?? episode;
            if (HasIntroMarkers(episode))
            {
                logger.Info($"{source} 片头扫描跳过: {episode.FileName ?? episode.Path} 已存在片头标记");
                return false;
            }

            var preExtractSource = source + " 预提取";
            if (LibraryService.IsFileShortcut(episode.Path ?? episode.FileName))
            {
                var mountedPath = await Plugin.LibraryService.GetStrmMountPathAsync(episode.Path).ConfigureAwait(false);
                if (string.IsNullOrEmpty(mountedPath))
                {
                    logger.Warn($"{preExtractSource}: {episode.FileName} InternalId: {episode.InternalId} 挂载路径解析失败，跳过");
                    return false;
                }
            }

            if (!Plugin.MediaInfoService.HasMediaInfo(episode))
            {
                var extracted = await MediaInfoRunner
                    .ExtractMediaInfoAsync(episode.InternalId, preExtractSource, cancellationToken, new[] { MediaStreamType.Audio })
                    .ConfigureAwait(false);
                if (!extracted)
                {
                    logger.Warn($"{preExtractSource}: {episode.FileName} 提取后仍无 MediaInfo，跳过");
                    return false;
                }

                episode = libraryManager.GetItemById(episode.InternalId) as Episode ?? episode;
            }

            logger.Debug($"{source}: 预提取完成，开始片头检测 {episode.Path} InternalId: {episode.InternalId}");
            return await ScanEpisodeCoreAsync(episode, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>执行单个剧集的片头探测，并在成功后持久化标记结果。</summary>
        private static async Task<bool> ScanEpisodeCoreAsync(Episode episode, CancellationToken cancellationToken)
        {
            var displayName = episode?.FileName ?? episode?.Path;
            if (episode == null)
            {
                return false;
            }

            try
            {
                logger.Debug($"开始片头检测: {displayName}");
                var stopwatch = Stopwatch.StartNew();
                var detected = await DetectIntroAsync(episode, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                var hasMarkersAfterDetect = HasIntroMarkers(episode);
                logger.Info(
                    $"片头检测结果: 状态={(detected && hasMarkersAfterDetect ? "成功" : "失败")}, 已检测={(detected ? "是" : "否")}, 已写入标记={(hasMarkersAfterDetect ? "是" : "否")}, 耗时={stopwatch.ElapsedMilliseconds}ms, 条目={displayName}");

                if (!detected)
                {
                    logger.Warn($"片头检测失败: reason=DetectorReturnedFalse, item={displayName}");
                }
                else if (hasMarkersAfterDetect)
                {
                    logger.Debug($"片头检测成功: marker 已写入, item={displayName}");
                }
                else
                {
                    logger.Warn($"片头检测失败: reason=NoMarkerGenerated, item={displayName}");
                }

                return detected;
            }
            catch (OperationCanceledException)
            {
                logger.Info($"扫描已取消 {displayName}");
                throw;
            }
            catch (Exception e)
            {
                logger.Error($"片头检测失败: {displayName}");
                logger.Error(e.Message);
                logger.Debug(e.StackTrace);
                return false;
            }
        }

        /// <summary>调用 Emby 的 AudioFingerprint 流程，对单个剧集执行片头探测。</summary>
        private static async Task<bool> DetectIntroAsync(Episode episode, CancellationToken cancellationToken)
        {
            logger.Debug($"DetectIntroAsync: item={episode?.Path ?? episode?.Name}, id={episode?.InternalId}");

            try
            {
                var runtime = GetOrCreateAudioFingerprintRuntime();
                if (runtime?.ManagerType == null)
                {
                    logger.Warn("未找到 AudioFingerprintManager 类型");
                }
                else
                {
                    var detector = CreateAudioFingerprintManager(runtime);
                    if (detector == null)
                    {
                        logger.Warn($"AudioFingerprintManager 实例创建失败: {runtime.ManagerType.FullName}");
                    }
                    else
                    {
                        if (await RunAudioFingerprintWorkflowAsync(detector, episode, cancellationToken).ConfigureAwait(false))
                        {
                            return true;
                        }

                        logger.Debug($"AudioFingerprintManager 执行失败: {detector.GetType().FullName}");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn($"片头检测服务加载失败: {ex.Message}");
            }

            logger.Info("探测失败，可能尚未获取strm文件内容，请稍后再试。");
            return false;
        }

        /// <summary>解析并缓存 AudioFingerprint 相关类型与方法签名。</summary>
        private static AudioFingerprintRuntime GetOrCreateAudioFingerprintRuntime()
        {
            if (audioFingerprintRuntime != null)
            {
                return audioFingerprintRuntime;
            }

            lock (runtimeLock)
            {
                if (audioFingerprintRuntime != null)
                {
                    return audioFingerprintRuntime;
                }

                var providersAssembly = Assembly.Load("Emby.Providers");
                if (providersAssembly == null)
                {
                    return null;
                }

                var providersVersion = providersAssembly.GetName().Version;
                var managerType = providersAssembly.GetType("Emby.Providers.Markers.AudioFingerprintManager");
                var seasonFingerprintInfoType = providersAssembly.GetType("Emby.Providers.Markers.SeasonFingerprintInfo");
                if (managerType == null || seasonFingerprintInfoType == null)
                {
                    logger.Warn("AudioFingerprintManager 关键类型缺失");
                    return null;
                }

                var createTitleFingerprintAsync = PatchMethodResolver.Resolve(
                    managerType, providersVersion,
                    new MethodSignatureProfile
                    {
                        Name = "audiofingerprintmanager-createtitlefingerprint-async",
                        MethodName = "CreateTitleFingerprint",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        ParameterTypes = new[] { typeof(Episode), typeof(LibraryOptions), typeof(IDirectoryService), typeof(CancellationToken) }
                    },
                    logger, "IntroScanRunner.CreateTitleFingerprint");
                var isIntroDetectionSupported = PatchMethodResolver.Resolve(
                    managerType, providersVersion,
                    new MethodSignatureProfile
                    {
                        Name = "audiofingerprintmanager-isintrodetectionsupported",
                        MethodName = "IsIntroDetectionSupported",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        ParameterTypes = new[] { typeof(Episode), typeof(LibraryOptions) }
                    },
                    logger, "IntroScanRunner.IsIntroDetectionSupported");
                var getAllFingerprintFilesForSeason = PatchMethodResolver.Resolve(
                    managerType, providersVersion,
                    new MethodSignatureProfile
                    {
                        Name = "audiofingerprintmanager-getallfingerprintfilesforseason",
                        MethodName = "GetAllFingerprintFilesForSeason",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        ParameterTypes = new[] { typeof(Season), typeof(Episode[]), typeof(LibraryOptions), typeof(IDirectoryService), typeof(CancellationToken) }
                    },
                    logger, "IntroScanRunner.GetAllFingerprintFilesForSeason");
                var updateSequencesForSeason = PatchMethodResolver.Resolve(
                    managerType, providersVersion,
                    new MethodSignatureProfile
                    {
                        Name = "audiofingerprintmanager-updatesequencesforseason",
                        MethodName = "UpdateSequencesForSeason",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        ParameterTypes = new[] { typeof(Season), seasonFingerprintInfoType, typeof(Episode), typeof(LibraryOptions), typeof(IDirectoryService), typeof(CancellationToken) }
                    },
                    logger, "IntroScanRunner.UpdateSequencesForSeason");

                var managerConstructor = managerType.GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[]
                    {
                        typeof(IFileSystem),
                        typeof(ILogger),
                        typeof(IApplicationPaths),
                        typeof(IFfmpegManager),
                        typeof(IMediaEncoder),
                        typeof(IMediaMountManager),
                        typeof(IJsonSerializer),
                        typeof(IServerApplicationHost)
                    },
                    null);

                if (managerConstructor != null)
                {
                    PatchLog.ResolveHit(
                        logger, "IntroScanRunner.AudioFingerprintManager..ctor", "exact",
                        "audiofingerprintmanager-ctor-exact", BuildConstructorSignature(managerConstructor),
                        providersVersion?.ToString() ?? "<unknown>");
                }
                else
                {
                    PatchLog.ResolveFailed(
                        logger, "IntroScanRunner.AudioFingerprintManager..ctor", managerType.FullName,
                        providersVersion?.ToString() ?? "<unknown>");
                }

                if (isIntroDetectionSupported == null ||
                    createTitleFingerprintAsync == null ||
                    getAllFingerprintFilesForSeason == null ||
                    updateSequencesForSeason == null ||
                    managerConstructor == null)
                {
                    logger.Warn("AudioFingerprintManager 关键方法缺失");
                    return null;
                }

                audioFingerprintRuntime = new AudioFingerprintRuntime(
                    managerType, seasonFingerprintInfoType, managerConstructor, isIntroDetectionSupported,
                    createTitleFingerprintAsync, getAllFingerprintFilesForSeason, updateSequencesForSeason);
                return audioFingerprintRuntime;
            }
        }

        /// <summary>按 Emby 真实构造函数精确实例化 AudioFingerprintManager。</summary>
        private static object CreateAudioFingerprintManager(AudioFingerprintRuntime runtime)
        {
            var appHost = Plugin.Instance.AppHost;
            if (appHost == null)
            {
                logger.Debug("AudioFingerprintManager 实例创建失败: AppHost 为空");
                return null;
            }

            if (runtime?.ManagerConstructor == null)
            {
                logger.Debug("AudioFingerprintManager 实例创建失败: 构造函数缺失");
                return null;
            }

            if (appHost is not IServerApplicationHost serverAppHost)
            {
                logger.Debug($"AudioFingerprintManager 实例创建失败: AppHost 不实现 {typeof(IServerApplicationHost).FullName}");
                return null;
            }

            try
            {
                var applicationPaths = appHost.Resolve<IApplicationPaths>();
                var ffmpegManager = appHost.Resolve<IFfmpegManager>();
                var mediaEncoder = appHost.Resolve<IMediaEncoder>();
                var mediaMountManager = appHost.Resolve<IMediaMountManager>();
                var jsonSerializer = appHost.Resolve<IJsonSerializer>();

                if (applicationPaths == null ||
                    ffmpegManager == null ||
                    mediaEncoder == null ||
                    mediaMountManager == null ||
                    jsonSerializer == null)
                {
                    logger.Debug(
                        $"AudioFingerprintManager 实例创建失败: ctor 依赖缺失 paths={applicationPaths != null}, ffmpeg={ffmpegManager != null}, encoder={mediaEncoder != null}, mount={mediaMountManager != null}, json={jsonSerializer != null}");
                    return null;
                }

                var ctorArgs = new object[]
                {
                    fileSystem, logger, applicationPaths, ffmpegManager, mediaEncoder,
                    mediaMountManager, jsonSerializer, serverAppHost
                };

                logger.Debug($"AudioFingerprintManager 实例创建: {runtime.ManagerType.FullName}");
                return runtime.ManagerConstructor.Invoke(ctorArgs);
            }
            catch (Exception ex)
            {
                logger.Debug($"AudioFingerprintManager 实例创建异常: {ex.Message}");
                return null;
            }
        }

        /// <summary>串起 AudioFingerprint 的支持性检查、指纹生成与片头序列更新流程。</summary>
        private static async Task<bool> RunAudioFingerprintWorkflowAsync(
            object detector,
            Episode episode,
            CancellationToken cancellationToken)
        {
            logger.Debug($"AudioFingerprint workflow start: detector={detector.GetType().FullName}, item={episode?.Path ?? episode?.Name}, id={episode?.InternalId}");
            var runtime = GetOrCreateAudioFingerprintRuntime();
            if (runtime == null)
            {
                logger.Warn("AudioFingerprint workflow未完成方法初始化");
                return false;
            }

            var directoryService = new DirectoryService(logger, fileSystem);
            var libraryOptions = libraryManager.GetLibraryOptions(episode);
            var hasLibraryOptions = libraryOptions != null;
            logger.Debug($"LibraryOptions loaded: null={!hasLibraryOptions}");

            var supportedArgs = new object[] { episode, libraryOptions };
            LogMethodInvocation(runtime.IsIntroDetectionSupported, supportedArgs);
            var supportedResult = await InvokeMethodAsync(detector, runtime.IsIntroDetectionSupported, supportedArgs).ConfigureAwait(false);
            if (supportedResult is bool supported && !supported)
            {
                logger.Debug("AudioFingerprintManager.IsIntroDetectionSupported 返回 false");
                return false;
            }
            logger.Debug($"IsIntroDetectionSupported result: {supportedResult ?? "null"}");

            logger.Debug("触发 CreateTitleFingerprint 生成指纹");
            var fingerprintArgs = new object[] { episode, libraryOptions, directoryService, cancellationToken };
            LogMethodInvocation(runtime.CreateTitleFingerprintAsync, fingerprintArgs);
            await InvokeMethodAsync(detector, runtime.CreateTitleFingerprintAsync, fingerprintArgs).ConfigureAwait(false);

            Season season = episode?.Season;
            if (season == null && episode != null)
            {
                season = libraryManager.GetItemById(episode.ParentId) as Season;
            }

            if (season == null)
            {
                logger.Debug("无法获取 Season，跳过 UpdateSequencesForSeason");
                return true;
            }

            logger.Debug($"Season resolved: {season.Name} (id={season.InternalId})");
            var seasonEpisodes = season.GetEpisodes(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                HasPath = true,
                MediaTypes = new[] { MediaType.Video }
            }, cancellationToken).Items.OfType<Episode>().ToArray();
            logger.Debug($"Season episodes loaded: count={seasonEpisodes.Length}");

            logger.Debug("触发 GetAllFingerprintFilesForSeason 收集指纹");
            var getArgs = new object[] { season, seasonEpisodes, libraryOptions, directoryService, cancellationToken };
            LogMethodInvocation(runtime.GetAllFingerprintFilesForSeason, getArgs);
            var seasonFingerprintInfo = await InvokeMethodAsync(detector, runtime.GetAllFingerprintFilesForSeason, getArgs).ConfigureAwait(false);
            logger.Debug($"GetAllFingerprintFilesForSeason result type: {seasonFingerprintInfo?.GetType().FullName ?? "null"}");

            logger.Debug("触发 UpdateSequencesForSeason 生成片头序列");
            if (seasonFingerprintInfo == null && runtime.SeasonFingerprintInfoType != null && runtime.SeasonFingerprintInfoType.IsClass)
            {
                logger.Debug("SeasonFingerprintInfo 为空，跳过 UpdateSequencesForSeason");
                return false;
            }

            var updateArgs = new[] { season, seasonFingerprintInfo, (object)episode, libraryOptions, directoryService, cancellationToken };
            LogMethodInvocation(runtime.UpdateSequencesForSeason, updateArgs);
            await InvokeMethodAsync(detector, runtime.UpdateSequencesForSeason, updateArgs).ConfigureAwait(false);
            logger.Debug($"AudioFingerprint workflow完成: item={episode?.Path ?? episode?.Name}");

            return true;
        }

        /// <summary>记录反射调用的方法签名与实参数类型，便于排查版本差异。</summary>
        private static void LogMethodInvocation(MethodInfo method, object[] args)
        {
            if (method == null)
            {
                return;
            }

            var parameters = method.GetParameters();
            var parts = new List<string>(parameters.Length);
            for (var i = 0; i < parameters.Length; i++)
            {
                var value = i < args.Length ? args[i] : null;
                var valueType = value?.GetType().Name ?? "null";
                parts.Add($"{parameters[i].Name}:{parameters[i].ParameterType.Name}={valueType}");
            }

            logger.Debug($"调用 {method.DeclaringType?.Name}.{method.Name} 参数: {string.Join(", ", parts)}");
        }

        private static string BuildConstructorSignature(ConstructorInfo constructor)
        {
            if (constructor == null)
            {
                return "<null>";
            }

            var parameters = string.Join(",", constructor.GetParameters().Select(p => p.ParameterType.Name));
            return string.Format(
                "{0}..ctor({1})",
                constructor.DeclaringType?.FullName ?? "<unknown>",
                parameters);
        }

        /// <summary>统一调用可能返回 Task 的反射方法，并在需要时取出其结果值。</summary>
        private static async Task<object> InvokeMethodAsync(object target, MethodInfo method, object[] args)
        {
            var result = method.Invoke(target, args);
            if (result is Task task)
            {
                await task.ConfigureAwait(false);
                var taskType = task.GetType();
                if (taskType.IsGenericType)
                {
                    return taskType.GetProperty("Result")?.GetValue(task);
                }

                return null;
            }

            return result;
        }

        private sealed class AudioFingerprintRuntime
        {
            public AudioFingerprintRuntime(
                Type managerType,
                Type seasonFingerprintInfoType,
                ConstructorInfo managerConstructor,
                MethodInfo isIntroDetectionSupported,
                MethodInfo createTitleFingerprintAsync,
                MethodInfo getAllFingerprintFilesForSeason,
                MethodInfo updateSequencesForSeason)
            {
                ManagerType = managerType;
                SeasonFingerprintInfoType = seasonFingerprintInfoType;
                ManagerConstructor = managerConstructor;
                IsIntroDetectionSupported = isIntroDetectionSupported;
                CreateTitleFingerprintAsync = createTitleFingerprintAsync;
                GetAllFingerprintFilesForSeason = getAllFingerprintFilesForSeason;
                UpdateSequencesForSeason = updateSequencesForSeason;
            }

            public Type ManagerType { get; }

            public Type SeasonFingerprintInfoType { get; }

            public ConstructorInfo ManagerConstructor { get; }

            public MethodInfo IsIntroDetectionSupported { get; }

            public MethodInfo CreateTitleFingerprintAsync { get; }

            public MethodInfo GetAllFingerprintFilesForSeason { get; }

            public MethodInfo UpdateSequencesForSeason { get; }
        }
    }
}
