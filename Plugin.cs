using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using MediaInfoKeeper.Common;
using MediaInfoKeeper.Options;
using MediaInfoKeeper.Options.Store;
using MediaInfoKeeper.Options.View;
using MediaInfoKeeper.Patch;
using MediaInfoKeeper.Services;
using MediaInfoKeeper.Services.IntroSkip;
using MediaInfoKeeper.Store;
using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Serialization;
using MediaInfoKeeper.Web;

namespace MediaInfoKeeper
{
    /// <summary>
    /// The plugin.
    /// </summary>
    public class Plugin : BasePlugin, IHasThumbImage, IHasUIPages
    {
        public const string PluginName = "MediaInfoKeeper";
        public const string TaskCategoryName = "Auto-MediaInfoKeeper";

        public static Plugin Instance { get; private set; }
        public static MediaInfoService MediaInfoService { get; private set; }
        
        public static ChaptersStore ChaptersStore { get; private set; }
        public static MediaSourceInfoStore MediaSourceInfoStore { get; private set; }
        public static EmbeddedInfoStore EmbeddedInfoStore { get; private set; }
        public static LibraryService LibraryService { get; private set; }
        public static NotificationApi NotificationApi { get; private set; }
        public static IntroSkipChapterApi IntroSkipChapterApi { get; private set; }
        public static IntroSkipPlaySessionMonitor IntroSkipPlaySessionMonitor { get; private set; }
        public static IntroScanService IntroScanService { get; private set; }
        public static PrefetchService PrefetchService { get; private set; }
        public static StrmFileWatcher StrmFileWatcher { get; private set; }
        public static ExternalSubtitle ExternalSubtitle { get; private set; }
        public static DanmuService DanmuService { get; private set; }

        private readonly Guid id = new Guid("874D7056-072D-43A4-16DD-BC32665B9563");
        private readonly ILogger logger;
        private List<IPluginUIPageController> pages;
        private Dictionary<string, string> lastLoggedOptionsSnapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        private Dictionary<string, string> pendingSavedOptionsSnapshot;

        private readonly ILibraryManager libraryManager;
        private readonly ILibraryMonitor libraryMonitor;
        private readonly IProviderManager providerManager;
        private readonly IItemRepository itemRepository;
        private readonly IFileSystem fileSystem;
        private readonly IUserManager userManager;
        private readonly IUserDataManager userDataManager;
        private readonly ISessionManager sessionManager;
        private readonly IMediaMountManager mediaMountManager;
        private readonly IApplicationHost applicationHost;
        private readonly IHttpClient httpClient;
        private readonly DirectoryService directoryService;

        internal static IProviderManager ProviderManager { get; private set; }
        internal static IFileSystem FileSystem { get; private set; }
        internal static ILibraryManager LibraryManager { get; private set; }
        internal static IDirectoryService DirectoryService { get; private set; }
        internal static IHttpClient SharedHttpClient { get; private set; }
        internal static ILogger SharedLogger { get; private set; }
        internal IApplicationHost AppHost => this.applicationHost;
        internal IItemRepository ItemRepository => this.itemRepository;

        private bool PlugginEnabled;
        internal readonly PluginOptionsStore OptionsStore;
        internal readonly MainPageOptionsStore MainPageOptionsStore;
        internal readonly MediaInfoOptionsStore MediaInfoOptionsStore;
        internal readonly GitHubOptionsStore GitHubOptionsStore;
        internal readonly IntroSkipOptionsStore IntroSkipOptionsStore;
        internal readonly NetWorkOptionsStore NetWorkOptionsStore;
        internal readonly EnhanceOptionsStore EnhanceOptionsStore;
        internal readonly MetaDataOptionsStore MetaDataOptionsStore;
#if DEBUG
        internal readonly DebugOptionsStore DebugOptionsStore;
#endif
        private static string latestReleaseVersionCache;
        private static string releaseHistoryChannelCache;
        private static readonly object ReleaseHistoryLock = new object();
        private static DateTimeOffset releaseHistoryCheckedAt = DateTimeOffset.MinValue;
        private static string releaseHistoryBodyCache;
        private static bool releaseHistoryRefreshInProgress;
        private static readonly TimeSpan LatestVersionCacheDuration = TimeSpan.FromMinutes(30);
        private const string GitHubReleaseHistoryUrl = "https://api.github.com/repos/honue/MediaInfoKeeper/releases?per_page=100&page=";

        private sealed class OptionLogEntry
        {
            public OptionLogEntry(string key, string section, string label, string value)
            {
                Key = key;
                Section = section;
                Label = label;
                Value = value;
            }

            public string Key { get; }
            public string Section { get; }
            public string Label { get; }
            public string Value { get; }
        }

        /// <summary>初始化插件并注册库事件处理。</summary>
        public Plugin(
            IApplicationHost applicationHost,
            ILogManager logManager,
            ILibraryManager libraryManager,
            ILibraryMonitor libraryMonitor,
            IProviderManager providerManager,
            IItemRepository itemRepository,
            IUserManager userManager,
            IUserDataManager userDataManager,
            ISessionManager sessionManager,
            INotificationManager notificationManager,
            IMediaMountManager mediaMountManager,
            IMediaSourceManager mediaSourceManager,
            IMediaProbeManager mediaProbeManager,
            IHttpClient httpClient,
            IServerConfigurationManager serverConfigurationManager,
            ILocalizationManager localizationManager,
            IJsonSerializer jsonSerializer,
            IFileSystem fileSystem)
        {
            Instance = this;
            this.logger = logManager.GetLogger(this.Name);
            this.logger.Info($"插件 {this.Name} 正在加载");

            this.applicationHost = applicationHost;
            this.libraryManager = libraryManager;
            this.libraryMonitor = libraryMonitor;
            this.providerManager = providerManager;
            this.itemRepository = itemRepository;
            this.fileSystem = fileSystem;
            this.directoryService = new DirectoryService(this.logger, fileSystem);
            this.userManager = userManager;
            this.userDataManager = userDataManager;
            this.sessionManager = sessionManager;
            this.mediaMountManager = mediaMountManager;
            this.httpClient = httpClient;
            ProviderManager = providerManager;
            FileSystem = fileSystem;
            LibraryManager = libraryManager;
            DirectoryService = this.directoryService;
            SharedHttpClient = httpClient;
            SharedLogger = this.logger;

            OptionsStore = new PluginOptionsStore(applicationHost, this.logger, this.Name,
                PrepareOptionsForUi, HandleOptionsSaving, HandleOptionsSaved);
            MainPageOptionsStore = new MainPageOptionsStore(OptionsStore);
            MediaInfoOptionsStore = new MediaInfoOptionsStore(OptionsStore);
            GitHubOptionsStore = new GitHubOptionsStore(OptionsStore);
            IntroSkipOptionsStore = new IntroSkipOptionsStore(OptionsStore);
            NetWorkOptionsStore = new NetWorkOptionsStore(OptionsStore);
            EnhanceOptionsStore = new EnhanceOptionsStore(OptionsStore);
            MetaDataOptionsStore = new MetaDataOptionsStore(OptionsStore);
#if DEBUG
            DebugOptionsStore = new DebugOptionsStore(OptionsStore);
#endif

            ExternalSubtitle = new ExternalSubtitle(
                libraryManager,
                fileSystem,
                mediaProbeManager,
                localizationManager,
                itemRepository);

            var initialOptions = this.Options;
            PersistOptionsOnStartup(initialOptions);
            PatchManager.Initialize(this.logger, initialOptions);

            this.PlugginEnabled = initialOptions.MainPage?.PlugginEnabled ?? true;
            LogOptionsSnapshot(initialOptions, "已加载");

            LibraryService = new LibraryService(libraryManager, providerManager, fileSystem, userManager, userDataManager, mediaMountManager);
            MediaInfoService = new MediaInfoService(libraryManager, mediaSourceManager, fileSystem);
            ChaptersStore = new ChaptersStore(itemRepository, fileSystem, jsonSerializer);
            MediaSourceInfoStore = new MediaSourceInfoStore(libraryManager, itemRepository, fileSystem, jsonSerializer);
            EmbeddedInfoStore = new EmbeddedInfoStore(jsonSerializer);
            DanmuService = new DanmuService(logManager, httpClient);

            NotificationApi = new NotificationApi(notificationManager, userManager, sessionManager);
            IntroSkipChapterApi = new IntroSkipChapterApi(libraryManager, itemRepository, this.logger);
            IntroScanService = new IntroScanService(logManager, libraryManager, fileSystem);
            IntroSkipPlaySessionMonitor = new IntroSkipPlaySessionMonitor(
                libraryManager, userManager, sessionManager, this.logger);
            PrefetchService = new PrefetchService(
                libraryManager, sessionManager, this.logger);
            StrmFileWatcher = new StrmFileWatcher(libraryManager, libraryMonitor, LibraryService, this.logger);
            PluginWebResourceLoader.Initialize(serverConfigurationManager);
            PrefetchService.Initialize();

            if (this.Options.IntroSkip?.EnableIntroMarker == true || this.Options.IntroSkip?.EnableCreditsMarker == true)
            {
                IntroSkipPlaySessionMonitor.Initialize();
                IntroSkipPlaySessionMonitor.UpdateLibraryPathsInScope(this.Options.IntroSkip.LibraryScope);
                IntroSkipPlaySessionMonitor.UpdateUsersInScope(this.Options.IntroSkip.UserScope);
            }

            ConfigureStrmFileWatcher();

            this.libraryManager.ItemAdded += this.OnItemAdded;
            this.libraryManager.ItemRemoved += this.OnItemRemoved;
            this.userDataManager.UserDataSaved += this.OnUserDataSaved;

            this.logger.Info($"插件 {this.Name} 加载完成");
        }

        public override string Description => "Persist/restore MediaInfo to speed up first playback.";

        public override Guid Id => this.id;

        public sealed override string Name => PluginName;

        public PluginConfiguration Options
        {
            get
            {
                var options = this.OptionsStore.GetOptions();
                options.MainPage ??= new MainPageOptions();
                options.MainPage.PrepareScheduledTaskEditorForUi();
                options.GetMediaInfoOptions();
                return options;
            }
        }

        public ILogger Logger => this.logger;

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public Stream GetThumbImage()
        {
            var type = this.GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".Resources.ThumbImage.png");
        }

        public IReadOnlyCollection<IPluginUIPageController> UIPageControllers
        {
            get
            {
                if (this.pages == null)
                {
                    this.pages = new List<IPluginUIPageController>
                    {
                        new MainPageController(this.applicationHost, this.GetPluginInfo(), this.MainPageOptionsStore,
                            this.MediaInfoOptionsStore,
                            this.IntroSkipOptionsStore, this.NetWorkOptionsStore,
                            this.EnhanceOptionsStore, this.MetaDataOptionsStore
#if DEBUG
                            , this.DebugOptionsStore
#endif
                            )
                    };
                }

                return this.pages.AsReadOnly();
            }
        }

        internal void PrepareOptionsForUi(PluginConfiguration options)
        {
            if (options == null)
            {
                return;
            }

            options.MainPage ??= new MainPageOptions();
            options.GetMediaInfoOptions();
            options.IntroSkip ??= new IntroSkipOptions();
            options.GetNetWorkOptions();
            var effectiveUpdatePluginOptions = options.GetEffectiveUpdatePluginOptions();
            if (string.IsNullOrWhiteSpace(effectiveUpdatePluginOptions.UpdateChannel))
            {
                effectiveUpdatePluginOptions.UpdateChannel = MainPageOptions.UpdateChannelOption.Stable.ToString();
            }
            options.Enhance ??= new EnhanceOptions();
            options.MetaData ??= new MetaDataOptions();
#if DEBUG
            options.Debug ??= new DebugOptions();
#endif
            options.IntroSkip.Initialize();
            options.GetMediaInfoOptions().Initialize();
            effectiveUpdatePluginOptions.Initialize();
            options.Enhance.Initialize();
            options.MetaData.Initialize();

            var list = LibraryService.BuildLibrarySelectOptions();
            options.MainPage.LibraryList = list;
            options.IntroSkip.LibraryList = list;
            options.MainPage.UpdatePluginVersionStatus = BuildVersionStatusItem();
            options.MainPage.UpdatePluginReleaseHistoryBody = string.IsNullOrWhiteSpace(releaseHistoryBodyCache)
                ? "加载中"
                : releaseHistoryBodyCache;
            options.MainPage.PrepareScheduledTaskEditorForUi();
        }

        internal bool HandleOptionsSaving(PluginConfiguration options)
        {
            if (options?.MainPage == null)
            {
                return true;
            }

            var persistedOptions = this.OptionsStore.LoadOptionsFromDisk();
            this.pendingSavedOptionsSnapshot = persistedOptions == null
                ? null
                : CreateOptionSnapshot(BuildPersistedOptionLogEntries(persistedOptions));

            FinalizeOptionsForPersistence(options);
            var effectiveUpdatePluginOptions = options.GetEffectiveUpdatePluginOptions();
            if (string.IsNullOrWhiteSpace(effectiveUpdatePluginOptions.UpdateChannel))
            {
                effectiveUpdatePluginOptions.UpdateChannel = MainPageOptions.UpdateChannelOption.Stable.ToString();
            }
            var netWorkOptions = options.GetNetWorkOptions();
            if (!LocalDiscoveryAddress.TryValidateConfiguredValue(
                    netWorkOptions.CustomLocalDiscoveryAddress,
                    out var normalizedDiscoveryAddress,
                    out var validationError))
            {
                this.logger.Warn("自定义本地发现地址校验失败：{0}", validationError);
                return false;
            }

            netWorkOptions.CustomLocalDiscoveryAddress = normalizedDiscoveryAddress;
            return true;
        }

        /// <summary>应用配置变更并更新缓存标记。</summary>
        internal void HandleOptionsSaved(PluginConfiguration options)
        {
            if (options == null)
            {
                return;
            }

            options.MainPage ??= new MainPageOptions();
            options.GetMediaInfoOptions();
            options.IntroSkip ??= new IntroSkipOptions();
            var effectiveUpdatePluginOptions = options.GetEffectiveUpdatePluginOptions();
            if (string.IsNullOrWhiteSpace(effectiveUpdatePluginOptions.UpdateChannel))
            {
                effectiveUpdatePluginOptions.UpdateChannel = MainPageOptions.UpdateChannelOption.Stable.ToString();
            }
            options.Enhance ??= new EnhanceOptions();
            options.MetaData ??= new MetaDataOptions();
#if DEBUG
            options.Debug ??= new DebugOptions();
#endif
            var netWorkOptions = options.GetNetWorkOptions();

            this.PlugginEnabled = options.MainPage.PlugginEnabled;

            LogOptionsChanges(options, "已更新");
            
            PatchManager.Configure(options);

            if (options.IntroSkip.EnableIntroMarker || options.IntroSkip.EnableCreditsMarker)
            {
                IntroSkipPlaySessionMonitor.Initialize();
                IntroSkipPlaySessionMonitor.UpdateLibraryPathsInScope(options.IntroSkip.LibraryScope);
                IntroSkipPlaySessionMonitor.UpdateUsersInScope(options.IntroSkip.UserScope);
            }
            else
            {
                IntroSkipPlaySessionMonitor.Dispose();
            }

            ConfigureStrmFileWatcher();

        }

        internal void UpdatePinyinSortNameLastProcessedAt(DateTimeOffset processedAt)
        {
            var options = this.OptionsStore.GetOptions();
            options.Enhance ??= new EnhanceOptions();

            var current = options.Enhance.PinyinSortNameLastProcessedAt;
            if (current.HasValue && current.Value >= processedAt)
            {
                return;
            }

            options.Enhance.PinyinSortNameLastProcessedAt = processedAt;
            this.OptionsStore.SetOptionsSilently(options);
        }

        private void ConfigureStrmFileWatcher()
        {
            var safeOptions = this.OptionsStore.GetOptions() ?? new PluginConfiguration();
            safeOptions.MainPage ??= new MainPageOptions();
            safeOptions.GetMediaInfoOptions();

            StrmFileWatcher?.Configure(this.PlugginEnabled, safeOptions.MainPage.FileChangeRefreshDelaySeconds);
        }

        private void PersistOptionsOnStartup(PluginConfiguration options)
        {
            if (options == null)
            {
                return;
            }

            FinalizeOptionsForPersistence(options);
            this.OptionsStore.SetOptionsSilently(options);
        }

        private void FinalizeOptionsForPersistence(PluginConfiguration options)
        {
            if (options == null)
            {
                return;
            }

            options.MainPage ??= new MainPageOptions();
            options.MainPage.ScheduledTasksEditor ??= new MainPageOptions.ScheduledTaskEditorOptions();
            options.GetMediaInfoOptions();
            options.IntroSkip ??= new IntroSkipOptions();
            options.GetNetWorkOptions();
            options.Enhance ??= new EnhanceOptions();
            options.MetaData ??= new MetaDataOptions();
#if DEBUG
            options.Debug ??= new DebugOptions();
#endif

            var effectiveUpdatePluginOptions = options.GetEffectiveUpdatePluginOptions();
            effectiveUpdatePluginOptions.Initialize();
            options.MainPage.ScheduledTasksEditor.UpdatePlugin = effectiveUpdatePluginOptions;

            options.MainPage.CatchupLibraries = NormalizeScopedLibraries(options.MainPage.CatchupLibraries);
            var scheduledTasksEditor = options.MainPage.ScheduledTasksEditor;
            scheduledTasksEditor.RefreshRecentMetadata.RefreshRecentMetadataLibraries =
                NormalizeScopedLibraries(scheduledTasksEditor.RefreshRecentMetadata.RefreshRecentMetadataLibraries);
            scheduledTasksEditor.ScanRecentIntro.ScanRecentIntroLibraries =
                NormalizeScopedLibraries(scheduledTasksEditor.ScanRecentIntro.ScanRecentIntroLibraries);
            scheduledTasksEditor.ExtractRecentMediaInfo.ExtractRecentMediaInfoLibraries =
                NormalizeScopedLibraries(scheduledTasksEditor.ExtractRecentMediaInfo.ExtractRecentMediaInfoLibraries);
            scheduledTasksEditor.DownloadDanmuXml.DownloadDanmuXmlLibraries =
                NormalizeScopedLibraries(scheduledTasksEditor.DownloadDanmuXml.DownloadDanmuXmlLibraries);
            scheduledTasksEditor.ExportExistingMediaInfo.ExportExistingMediaInfoLibraries =
                NormalizeScopedLibraries(scheduledTasksEditor.ExportExistingMediaInfo.ExportExistingMediaInfoLibraries);
            scheduledTasksEditor.RestoreMediaInfo.RestoreMediaInfoLibraries =
                NormalizeScopedLibraries(scheduledTasksEditor.RestoreMediaInfo.RestoreMediaInfoLibraries);
            scheduledTasksEditor.ScanExternalSubtitle.ScanExternalSubtitleLibraries =
                NormalizeScopedLibraries(scheduledTasksEditor.ScanExternalSubtitle.ScanExternalSubtitleLibraries);

            if (options.IntroSkip != null)
            {
                options.IntroSkip.LibraryScope = NormalizeScopedLibraries(options.IntroSkip.LibraryScope);
            }
        }
        private void LogOptionsSnapshot(PluginConfiguration options, string action)
        {
            if (options == null)
            {
                return;
            }

            var entries = BuildOptionLogEntries(options);
            this.lastLoggedOptionsSnapshot = CreateOptionSnapshot(entries);

            if (options.Enhance?.LogOptionsOnStartup != true)
            {
                return;
            }

            this.logger.Info($"{this.Name} 配置{action}。");
            LogOptionEntries(entries);
        }

        private void LogOptionsChanges(PluginConfiguration options, string action)
        {
            if (options == null)
            {
                return;
            }

            var entries = BuildPersistedOptionLogEntries(options);
            var currentSnapshot = CreateOptionSnapshot(entries);
            var baselineSnapshot = this.pendingSavedOptionsSnapshot ?? this.lastLoggedOptionsSnapshot;

            var changedEntries = entries
                .Where(entry =>
                    !baselineSnapshot.TryGetValue(entry.Key, out var previousValue) ||
                    !string.Equals(previousValue, entry.Value, StringComparison.Ordinal))
                .ToList();

            if (changedEntries.Count == 0)
            {
                this.pendingSavedOptionsSnapshot = null;
                this.lastLoggedOptionsSnapshot = currentSnapshot;
                return;
            }

            this.logger.Info($"{this.Name} 配置{action}。");
            LogChangedOptionEntries(changedEntries, baselineSnapshot);
            this.pendingSavedOptionsSnapshot = null;
            this.lastLoggedOptionsSnapshot = currentSnapshot;
        }

        private List<OptionLogEntry> BuildOptionLogEntries(PluginConfiguration options)
        {
            options.MainPage ??= new MainPageOptions();
            var mediaInfoOptions = options.GetMediaInfoOptions();
            options.IntroSkip ??= new IntroSkipOptions();
            options.Enhance ??= new EnhanceOptions();
            options.MetaData ??= new MetaDataOptions();
#if DEBUG
            options.Debug ??= new DebugOptions();
#endif
            var netWorkOptions = options.GetNetWorkOptions();
            options.MainPage.EnsureScheduledTaskEditors();
            var scheduledTasksEditor = options.MainPage.ScheduledTasksEditor;
            var refreshRecentMetadata = scheduledTasksEditor.RefreshRecentMetadata;
            var scanRecentIntro = scheduledTasksEditor.ScanRecentIntro;
            var extractRecentMediaInfo = scheduledTasksEditor.ExtractRecentMediaInfo;
            var downloadDanmuXml = scheduledTasksEditor.DownloadDanmuXml;
            var exportExistingMediaInfo = scheduledTasksEditor.ExportExistingMediaInfo;
            var restoreMediaInfo = scheduledTasksEditor.RestoreMediaInfo;
            var scanExternalSubtitle = scheduledTasksEditor.ScanExternalSubtitle;

            return new List<OptionLogEntry>
            {
                new OptionLogEntry("Main.PlugginEnabled", "Main", "启用插件", options.MainPage.PlugginEnabled.ToString()),
                new OptionLogEntry("Main.StrmFileWatcher", "Main", "启用 Strm 新入库监听", "开"),
                new OptionLogEntry("Main.CatchupLibraries", "Main", "追更媒体库", FormatOptionValue(options.MainPage.CatchupLibraries)),
                new OptionLogEntry("Main.RefreshRecentMetadataLibraries", "Main", "刷新媒体元数据范围", FormatOptionValue(refreshRecentMetadata.RefreshRecentMetadataLibraries)),
                new OptionLogEntry("Main.RefreshRecentMetadataDays", "Main", "刷新媒体元数据时间窗口", FormatTaskIntDisplayValue(refreshRecentMetadata.RefreshRecentMetadataDays, true)),
                new OptionLogEntry("Main.RefreshMetadataMode", "Main", "刷新模式", refreshRecentMetadata.RefreshMetadataMode.ToString()),
                new OptionLogEntry("Main.ReplaceExistingImages", "Main", "替换现有图像", refreshRecentMetadata.ReplaceExistingImages.ToString()),
                new OptionLogEntry("Main.ReplaceExistingVideoPreviewThumbnails", "Main", "替换现有视频预览缩略图", refreshRecentMetadata.ReplaceExistingVideoPreviewThumbnails.ToString()),
                new OptionLogEntry("Main.ScanRecentIntroLibraries", "Main", "扫描片头范围", FormatOptionValue(scanRecentIntro.ScanRecentIntroLibraries)),
                new OptionLogEntry("Main.ScanRecentIntroLimit", "Main", "扫描片头最近条目数量", FormatTaskIntDisplayValue(scanRecentIntro.ScanRecentIntroLimit, false)),
                new OptionLogEntry("Main.ExtractRecentMediaInfoLibraries", "Main", "提取媒体信息范围", FormatOptionValue(extractRecentMediaInfo.ExtractRecentMediaInfoLibraries)),
                new OptionLogEntry("Main.ExtractRecentMediaInfoLimit", "Main", "提取媒体信息最近条目数量", FormatTaskIntDisplayValue(extractRecentMediaInfo.ExtractRecentMediaInfoLimit, false)),
                new OptionLogEntry("Main.DownloadDanmuXmlLibraries", "Main", "下载弹幕范围", FormatOptionValue(downloadDanmuXml.DownloadDanmuXmlLibraries)),
                new OptionLogEntry("Main.DownloadDanmuXmlDays", "Main", "下载弹幕时间窗口", FormatTaskIntDisplayValue(downloadDanmuXml.DownloadDanmuXmlDays, true)),
                new OptionLogEntry("Main.ExportExistingMediaInfoLibraries", "Main", "备份媒体信息范围", FormatOptionValue(exportExistingMediaInfo.ExportExistingMediaInfoLibraries)),
                new OptionLogEntry("Main.RestoreMediaInfoLibraries", "Main", "恢复媒体信息范围", FormatOptionValue(restoreMediaInfo.RestoreMediaInfoLibraries)),
                new OptionLogEntry("Main.ScanExternalSubtitleLibraries", "Main", "扫描外挂字幕范围", FormatOptionValue(scanExternalSubtitle.ScanExternalSubtitleLibraries)),
                new OptionLogEntry(
                    "Main.FileChangeRefreshDelaySeconds",
                    "Main",
                    "Emby入库扫描延迟",
                    options.MainPage.FileChangeRefreshDelaySeconds < 0
                        ? "已禁用覆盖"
                        : $"{options.MainPage.FileChangeRefreshDelaySeconds} 秒"),

                new OptionLogEntry("MediaInfo.ExtractMediaInfoOnItemAdded", "MediaInfo", "入库时提取媒体信息", mediaInfoOptions.ExtractMediaInfoOnItemAdded.ToString()),
                new OptionLogEntry("MediaInfo.MediaInfoJsonRootFolder", "MediaInfo", "MediaInfo JSON 存储根目录", FormatOptionValue(mediaInfoOptions.MediaInfoJsonRootFolder)),
                new OptionLogEntry("MediaInfo.DeleteMediaInfoJsonOnRemove", "MediaInfo", "条目移除时删除 JSON", mediaInfoOptions.DeleteMediaInfoJsonOnRemove.ToString()),
                new OptionLogEntry("MediaInfo.EnableMediaInfoPrefetch", "MediaInfo", "启用 MediaInfo 预加载", mediaInfoOptions.EnableMediaInfoPrefetch.ToString()),
                new OptionLogEntry("MediaInfo.MaxConcurrentCount", "MediaInfo", "扫描最多并发数", mediaInfoOptions.MaxConcurrentCount.ToString()),

                new OptionLogEntry("IntroSkip.UnlockIntroSkip", "IntroSkip", "启用 Strm 片头检测解锁", options.IntroSkip.UnlockIntroSkip.ToString()),
                new OptionLogEntry("IntroSkip.ScanIntroOnItemAdded", "IntroSkip", "入库时扫描片头", options.IntroSkip.ScanIntroOnItemAdded.ToString()),
                new OptionLogEntry("IntroSkip.ScanIntroOnFavorite", "IntroSkip", "收藏时扫描片头", options.IntroSkip.ScanIntroOnFavorite.ToString()),
                new OptionLogEntry("IntroSkip.ProtectMarkers", "IntroSkip", "保护片头标记", "开"),
                new OptionLogEntry("IntroSkip.EnableIntroMarker", "IntroSkip", "启用片头打标", options.IntroSkip.EnableIntroMarker.ToString()),
                new OptionLogEntry("IntroSkip.EnableCreditsMarker", "IntroSkip", "启用片尾打标", options.IntroSkip.EnableCreditsMarker.ToString()),
                new OptionLogEntry("IntroSkip.LibraryScope", "IntroSkip", "打标库范围", FormatOptionValue(options.IntroSkip.LibraryScope)),
                new OptionLogEntry("IntroSkip.UserScope", "IntroSkip", "用户范围", FormatOptionValue(options.IntroSkip.UserScope)),
                new OptionLogEntry("Enhance.EnhanceChineseSearch", "Enhance", "启用增强搜索", options.Enhance.EnhanceChineseSearch.ToString()),
                new OptionLogEntry("Enhance.EnableStrmDirectRedirect", "Enhance", "启用 Strm 302 直连", options.Enhance.EnableStrmDirectRedirect.ToString()),
                new OptionLogEntry("Enhance.StrmDirectRedirectFollow302", "Enhance", "跟踪 302 跳转", options.Enhance.StrmDirectRedirectFollow302.ToString()),
                new OptionLogEntry("Enhance.EnableDeepDelete", "Enhance", "启用深度删除", options.Enhance.EnableDeepDelete.ToString()),
                new OptionLogEntry("Enhance.EnableNfoMetadataEnhance", "Enhance", "启用 NFO 增强", options.Enhance.EnableNfoMetadataEnhance.ToString()),
                new OptionLogEntry("Enhance.HidePersonNoImage", "Enhance", "隐藏无图人物", options.Enhance.HidePersonNoImage.ToString()),
                new OptionLogEntry("Enhance.HidePersonPreference", "Enhance", "人物隐藏偏好", FormatOptionValue(options.Enhance.HidePersonPreference)),
                new OptionLogEntry("Enhance.EnablePinyinSortName", "Enhance", "拼音首字母排序", options.Enhance.EnablePinyinSortName.ToString()),
                new OptionLogEntry("Enhance.NoBoxsetsAutoCreation", "Enhance", "禁止自动合集", options.Enhance.NoBoxsetsAutoCreation.ToString()),
                new OptionLogEntry("Enhance.EnforceLibraryOrder", "Enhance", "统一媒体库顺序", options.Enhance.EnforceLibraryOrder.ToString()),
                new OptionLogEntry("Enhance.TakeOverSystemLibraryNew", "Enhance", "接管系统入库通知", options.Enhance.TakeOverSystemLibraryNew.ToString()),
                new OptionLogEntry("Enhance.EnableNotificationEnhance", "Enhance", "通知系统增强", options.Enhance.EnableNotificationEnhance.ToString()),
                new OptionLogEntry("Enhance.SearchScope", "Enhance", "搜索范围", FormatOptionValue(options.Enhance.SearchScope)),
                new OptionLogEntry("Enhance.ExcludeOriginalTitleFromSearch", "Enhance", "排除原始标题", options.Enhance.ExcludeOriginalTitleFromSearch.ToString()),
                new OptionLogEntry("Enhance.SystemLogNameBlacklist", "Enhance", "日志来源黑名单", FormatOptionValue(options.Enhance.SystemLogNameBlacklist)),
                new OptionLogEntry("Enhance.EnableDetailedNetworkRequestLogging", "Enhance", "日志显示详细网络请求", options.Enhance.EnableDetailedNetworkRequestLogging.ToString()),
                new OptionLogEntry("Enhance.LogOptionsOnStartup", "Enhance", "启动时输出配置日志", options.Enhance.LogOptionsOnStartup.ToString()),
                new OptionLogEntry("Enhance.EnableSystemLogReverse", "Enhance", "系统日志倒序显示", options.Enhance.EnableSystemLogReverse.ToString()),

                new OptionLogEntry("MetaData.MetadataChangeWatcher", "MetaData", "启用剧集元数据变动监听", "开"),
                new OptionLogEntry("MetaData.EnableAlternativeTitleFallback", "MetaData", "启用 TMDB 中文回退", options.MetaData.EnableAlternativeTitleFallback.ToString()),
                new OptionLogEntry("MetaData.EnablePersonRoleDoubanFallback", "MetaData", "启用豆瓣角色中文化", options.MetaData.EnablePersonRoleDoubanFallback.ToString()),
                new OptionLogEntry("MetaData.EnableDoubanLinkWriteback", "MetaData", "写入豆瓣链接", options.MetaData.EnableDoubanLinkWriteback.ToString()),
                new OptionLogEntry("MetaData.EnableTvdbFallback", "MetaData", "启用 TVDB 中文回退", options.MetaData.EnableTvdbFallback.ToString()),
                new OptionLogEntry("MetaData.FallbackLanguages", "MetaData", "TMDB 备选语言", FormatOptionValue(options.MetaData.FallbackLanguages)),
                new OptionLogEntry("MetaData.TvdbFallbackLanguages", "MetaData", "TVDB 备选语言", FormatOptionValue(options.MetaData.TvdbFallbackLanguages)),
                new OptionLogEntry("MetaData.EnableDanmuApi", "MetaData", "启用弹幕 API", options.MetaData.EnableDanmuApi.ToString()),
                new OptionLogEntry("MetaData.DanmuApiBaseUrl", "MetaData", "弹幕 API BaseUrl", FormatOptionValue(options.MetaData.DanmuApiBaseUrl)),
                new OptionLogEntry("MetaData.DanmuFetchMode", "MetaData", "弹幕拉取策略", FormatOptionValue(options.MetaData.DanmuFetchMode)),
                new OptionLogEntry("MetaData.EnableDanmuPrefetch", "MetaData", "预加载弹幕", options.MetaData.EnableDanmuPrefetch.ToString()),
                new OptionLogEntry("MetaData.EnableDanmakuJs", "MetaData", "加载弹幕 JS", options.MetaData.EnableDanmakuJs.ToString()),
                new OptionLogEntry("MetaData.BlockNonFallbackLanguage", "MetaData", "屏蔽非备选语言简介", options.MetaData.BlockNonFallbackLanguage.ToString()),
                new OptionLogEntry("MetaData.EnableMovieDbEpisodeGroup", "MetaData", "启用 TMDB 剧集组刮削", options.MetaData.EnableMovieDbEpisodeGroup.ToString()),
                new OptionLogEntry("MetaData.EnableOriginalPoster", "MetaData", "优先原语言海报", options.MetaData.EnableOriginalPoster.ToString()),
                new OptionLogEntry("MetaData.EnableLocalEpisodeGroup", "MetaData", "启用本地剧集组文件", options.MetaData.EnableLocalEpisodeGroup.ToString()),
                new OptionLogEntry("MetaData.EnableImageCapture", "MetaData", "启用图片提取", options.MetaData.EnableImageCapture.ToString()),

                new OptionLogEntry("GitHub.GitHubToken", "GitHub", "GitHub 访问令牌", FormatSecretValue(options.GetEffectiveUpdatePluginOptions().GitHubToken)),
                new OptionLogEntry("GitHub.DownloadUrlPrefix", "GitHub", "下载前缀", FormatOptionValue(options.GetEffectiveUpdatePluginOptions().DownloadUrlPrefix)),
                new OptionLogEntry("GitHub.UpdateChannel", "GitHub", "更新频道", FormatOptionValue(options.GetEffectiveUpdatePluginOptions().UpdateChannel)),
                new OptionLogEntry("GitHub.VersionStatus", "GitHub", "版本信息", FormatOptionValue(options.MainPage.UpdatePluginVersionStatus?.StatusText)),

                new OptionLogEntry("NetWork.EnableProxyServer", "NetWork", "启用代理", netWorkOptions.EnableProxyServer.ToString()),
                new OptionLogEntry("NetWork.ProxyServerUrl", "NetWork", "代理服务器地址", FormatOptionValue(netWorkOptions.ProxyServerUrl)),
                new OptionLogEntry("NetWork.ProxyDomains", "NetWork", "需要使用代理的域名", FormatOptionValue(netWorkOptions.ProxyDomains)),
                new OptionLogEntry("NetWork.IgnoreCertificateValidation", "NetWork", "忽略证书验证", netWorkOptions.IgnoreCertificateValidation.ToString()),
                new OptionLogEntry("NetWork.WriteProxyEnvVars", "NetWork", "写入环境变量", netWorkOptions.WriteProxyEnvVars.ToString()),
                new OptionLogEntry("NetWork.EnableGzip", "NetWork", "启用压缩传输", netWorkOptions.EnableGzip.ToString()),
                new OptionLogEntry("NetWork.CustomLocalDiscoveryAddress", "NetWork", "自定义本地发现地址", FormatOptionValue(netWorkOptions.CustomLocalDiscoveryAddress)),
                new OptionLogEntry("NetWork.AlternativeTmdbApiUrl", "NetWork", "自定义 TMDB API 域名", FormatOptionValue(netWorkOptions.AlternativeTmdbApiUrl)),
                new OptionLogEntry("NetWork.AlternativeTmdbImageUrl", "NetWork", "自定义 TMDB 图像域名", FormatOptionValue(netWorkOptions.AlternativeTmdbImageUrl)),
                new OptionLogEntry("NetWork.AlternativeTmdbApiKey", "NetWork", "自定义 TMDB API 密钥", FormatSecretValue(netWorkOptions.AlternativeTmdbApiKey)),
#if DEBUG
                new OptionLogEntry("Debug.EnableFfProcessGuard", "Debug", "启用 ffprocess 拦截", options.Debug.EnableFfProcessGuard.ToString()),
#endif
            };
        }

        private List<OptionLogEntry> BuildPersistedOptionLogEntries(PluginConfiguration options)
        {
            return BuildOptionLogEntries(options)
                .Where(entry =>
                    !string.Equals(entry.Key, "GitHub.VersionStatus", StringComparison.Ordinal))
                .ToList();
        }

        private void LogOptionEntries(IEnumerable<OptionLogEntry> entries)
        {
            string currentSection = null;
            foreach (var entry in entries)
            {
                if (!string.Equals(currentSection, entry.Section, StringComparison.Ordinal))
                {
                    currentSection = entry.Section;
                    this.logger.Info($"[{currentSection}]");
                }

                this.logger.Info($"{entry.Label} 设置为 {entry.Value}");
            }
        }

        private void LogChangedOptionEntries(IEnumerable<OptionLogEntry> entries, Dictionary<string, string> baselineSnapshot)
        {
            string currentSection = null;
            foreach (var entry in entries)
            {
                if (!string.Equals(currentSection, entry.Section, StringComparison.Ordinal))
                {
                    currentSection = entry.Section;
                    this.logger.Info($"[{currentSection}]");
                }

                var previousValue = baselineSnapshot.TryGetValue(entry.Key, out var value)
                    ? value
                    : "未设置";
                this.logger.Info($"{entry.Label}: {previousValue} -> {entry.Value}");
            }
        }

        private Dictionary<string, string> CreateOptionSnapshot(IEnumerable<OptionLogEntry> entries)
        {
            return entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        }

        private static string FormatOptionValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "空" : value;
        }

        private static string FormatTaskIntDisplayValue(int value, bool zeroMeansUnlimited)
        {
            if (zeroMeansUnlimited && value == 0)
            {
                return "不限制";
            }

            return value.ToString();
        }

        private static string FormatSecretValue(string value)
        {
            return string.IsNullOrEmpty(value) ? "空" : "***";
        }

        private string NormalizeScopedLibraries(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in this.libraryManager.GetVirtualFolders())
            {
                if (folder == null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(folder.ItemId))
                {
                    lookup[folder.ItemId] = folder.ItemId;
                }

                if (!string.IsNullOrWhiteSpace(folder.Name))
                {
                    lookup[folder.Name.Trim()] = folder.ItemId;
                }
            }

            var tokens = raw.Split(new[] { ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var normalized = new List<string>();
            foreach (var token in tokens)
            {
                var value = token.Trim();
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                if (lookup.TryGetValue(value, out var mapped))
                {
                    normalized.Add(mapped);
                }
                else
                {
                    normalized.Add(value);
                }
            }

            return string.Join(",", normalized);
        }

        /// <summary>处理新入库条目，按配置执行持久化或恢复。</summary>
        private async void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            try
            {
                if (!this.PlugginEnabled)
                {
                    // 未启用持久化，直接跳过。
                    return;
                }

                if (!(e.Item is Video) && !(e.Item is Audio))
                {
                    // 仅处理音视频条目。
                    return;
                }
                this.logger.Info($"新入库事件 {e.Item.FileName ?? e.Item.Path}");

                if (!LibraryService.IsItemInCatchupLibraryScope(e.Item))
                {
                    // 条目不在选定媒体库范围内。
                    this.logger.Info("跳过处理: 不在选定媒体库范围");
                    return;
                }

                // 判断当前条目是否已有 MediaInfo。
                var hasMediaInfo = MediaInfoService.HasMediaInfo(e.Item);
                if (!hasMediaInfo)
                {
                    // 优先尝试从 JSON 恢复，减少首次提取耗时。
                    this.logger.Debug("尝试从 JSON 恢复 MediaInfo");
                    var restoreResult = MediaSourceInfoStore.ApplyToItem(e.Item);
                    var shouldRefreshAfterRestore = restoreResult == MediaInfoDocument.MediaInfoRestoreResult.Failed;
                    if (e.Item is Video)
                    {
                        ChaptersStore.ApplyToItem(e.Item);
                    }
                    else if (e.Item is Audio)
                    {
                        EmbeddedInfoStore.ApplyToItem(e.Item);
                    }

                    // 如果不存在Json文件，则使用ffprobe 提取一次
                    if (shouldRefreshAfterRestore)
                    {
                        if (!this.Options.GetMediaInfoOptions().ExtractMediaInfoOnItemAdded)
                        {
                            this.logger.Info($"已关闭入库提取媒体信息，跳过提取 item={e.Item.FileName ?? e.Item.Path}");
                        }
                        else
                        {
                            // 恢复失败时先触发媒体信息提取，再写入 JSON。
                            this.logger.Info($"入库媒体信息: 媒体信息缺失，开始提取 item={e.Item.FileName ?? e.Item.Path}");

                            // 触发一次刷新以提取 MediaInfo。
                            using (FfProcessGuard.Allow())
                            {
                                // 构建用于媒体信息提取的刷新参数与库选项。
                                var metadataRefreshOptions = new MetadataRefreshOptions(this.directoryService)
                                {
                                    EnableRemoteContentProbe = true,
                                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                                    ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                                    ReplaceAllMetadata = true,
                                    ReplaceAllImages = false
                                };

                                var itemCollectionFolders = this.libraryManager.GetCollectionFolders(e.Item).Cast<BaseItem>().ToArray();
                                var itemLibraryOptions = this.libraryManager.GetLibraryOptions(e.Item);
                                e.Item.DateLastRefreshed = new DateTimeOffset();
                                await RefreshTaskRunner.RunAsync(
                                        () => this.providerManager
                                            .RefreshSingleItem(e.Item, metadataRefreshOptions, itemCollectionFolders, itemLibraryOptions, CancellationToken.None))
                                    .ConfigureAwait(false);
                            }
                            this.logger.Info($"入库媒体信息: 提取完成并写入 JSON item={e.Item.FileName ?? e.Item.Path}");
                        }
                    }
                    // 使用Json媒体信息数据，恢复成功后扫描所在物理路径，确保库状态刷新。
                    else if (restoreResult == MediaInfoDocument.MediaInfoRestoreResult.Restored)
                    {
                        var itemPath = e.Item.Path ?? e.Item.ContainingFolderPath ?? e.Item.Id.ToString();
                        var parentPath = e.Item.ContainingFolderPath;
                        this.logger.Info($"入库媒体信息: JSON 恢复成功 item={itemPath}");

                        if (string.IsNullOrEmpty(parentPath))
                        {
                            this.logger.Info($"未找到条目所在物理路径，跳过扫描 item: {itemPath}");
                        }
                        else if (!this.fileSystem.DirectoryExists(parentPath))
                        {
                            this.logger.Info($"物理路径不存在，跳过扫描: {parentPath}");
                        }
                        else
                        {
                            var parentFolder = this.libraryManager.FindByPath(parentPath, true) as Folder;
                            if (parentFolder == null)
                            {
                                this.logger.Info($"未找到物理路径对应的文件夹项，跳过刷新: {parentPath}");
                            }
                            else
                            {
                                // 仅触发目录校验/发现，不做元数据覆盖与远端抓取。
                                var discoverOnlyOptions = new MetadataRefreshOptions(this.directoryService)
                                {
                                    EnableRemoteContentProbe = false,
                                    MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                                    ReplaceAllMetadata = false,
                                    ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                                    ReplaceAllImages = false,
                                    EnableThumbnailImageExtraction = false,
                                    EnableSubtitleDownloading = false
                                };

                                this.logger.Info($"刷新父级条目: {parentPath}");
                                try
                                {
                                    var collectionFolders = this.libraryManager.GetCollectionFolders(parentFolder).Cast<BaseItem>().ToArray();
                                    var libraryOptions = this.libraryManager.GetLibraryOptions(parentFolder);
                                    await RefreshTaskRunner.RunAsync(
                                            () => this.providerManager
                                                .RefreshSingleItem(parentFolder, discoverOnlyOptions, collectionFolders, libraryOptions, CancellationToken.None))
                                        .ConfigureAwait(false);
                                }
                                catch (Exception refreshEx)
                                {
                                    this.logger.Error($"刷新父级条目失败: {parentPath}");
                                    this.logger.Error(refreshEx.Message);
                                    this.logger.Debug(refreshEx.StackTrace);
                                }
                            }
                        }
                    }
                }
                // 已有 MediaInfo 时，直接用媒体信息覆盖写入 JSON，保持最新。
                else
                {
                    this.logger.Debug("已有 MediaInfo，覆盖写入 JSON");
                    MediaInfoPersist.OverWritePersistedMedia(e.Item);
                }
                
                // 入库加入扫描片头队列
                if (this.Options.IntroSkip?.ScanIntroOnItemAdded == true && e.Item is Episode episode)
                {
                    IntroScanService.QueueEpisodeScan(episode, "OnItemAdded");
                }

                // 收藏入库通知分支
                if (e.Item is Episode newEpisode && newEpisode.ExtraType == null)
                {
                    var series = LibraryService.GetSeries(newEpisode.SeriesId);
                    if (series == null)
                    {
                        this.logger.Info($"收藏入库通知跳过: 未找到所属剧集，episodeId={newEpisode.InternalId}");
                    }
                    else
                    {
                        var users = LibraryService.GetFavoriteUsersBySeriesId(series.InternalId);
                        if (users.Count != 0)
                        {
                            this.logger.Info($"收藏入库事件: 剧集={series.Name} {newEpisode.Name}, 收藏用户={string.Join(", ", users)}");
                            var sentCount = NotificationApi.LibraryNewSendNotification(series, newEpisode, users);
                            if (sentCount > 0)
                            {
                                this.logger.Info($"已发送入库通知: 剧集={series.Name} {newEpisode.Name}, 通知用户数={sentCount}");
                            }
                            // 开始执行扫描收藏媒体信息和片头,避免重复，判断未开启所有入库扫描
                            var canScanIntro = this.Options.IntroSkip?.ScanIntroOnFavorite == true && this.Options.IntroSkip?.ScanIntroOnItemAdded == false;
                            if (canScanIntro)
                            {
                                IntroScanService.QueueEpisodeScan(newEpisode, "收藏入库");
                            }
                        }
                        else
                        {
                            this.logger.Debug($"收藏入库通知跳过: 剧集={series.Name}，无收藏用户");
                        }
                    }
                }

            }
            catch (Exception ex)
            {
                // 记录异常，避免影响库事件流程。
                this.logger.Error(ex.Message);
                this.logger.Debug(ex.StackTrace);
            }
        }

        /// <summary> 收藏喜爱事件处 </summary>
        private void OnUserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            try
            {
                if (!this.PlugginEnabled)
                {
                    return;
                }

                var item = e.Item;
                var userData = e.UserData;
                if (item == null || userData == null)
                {
                    return;
                }

                if (item.ExtraType != null)
                {
                    return;
                }

                if (!userData.IsFavorite)
                {
                    return;
                }

                var userName = e.User?.Name ?? "unknown";
                logger.Info($"收藏事件: 用户={userName}, 条目={(item.FileName ?? item.Path ?? item.Id.ToString())}");

                var canScanIntro = this.Options.IntroSkip?.ScanIntroOnFavorite == true &&
                                (item is Episode || item is Season || item is Series);

                if (!canScanIntro)
                {
                    return;
                }

                if (canScanIntro)
                {
                    var episodes = LibraryService.GetSeriesEpisodesFromItem(item);
                    if (episodes.Count > 0)
                    {
                        foreach (var seriesEpisode in episodes)
                        {
                            IntroScanService.QueueEpisodeScan(seriesEpisode, "OnFavorite");
                        }
                    }
                    else
                    {
                        this.logger.Info("OnFavorite 片头扫描跳过: 未找到系列条目");
                    }
                }

            }
            catch (Exception ex)
            {
                this.logger.Error("收藏事件处理异常");
                this.logger.Error(ex.Message);
                this.logger.Debug(ex.StackTrace);
            }
        }

        /// <summary>条目移除且非恢复模式时，删除已持久化的 JSON。</summary>
        private void OnItemRemoved(object sender, ItemChangeEventArgs e)
        {
            this.logger.Info($"{e.Item.Path} 删除媒体事件");
            // 未开启删除开关时直接跳过。
            if (!this.Options.GetMediaInfoOptions().DeleteMediaInfoJsonOnRemove || !this.Options.MainPage.PlugginEnabled)
            {
                return;
            }

            if (!(e.Item is Video) && !(e.Item is Audio))
            {
                return;
            }

            if (!LibraryService.IsItemInCatchupLibraryScope(e.Item))
            {
                return;
            }

            logger.Info("同步删除 媒体信息 Json");
            MediaInfoDocument.DeleteMediaInfoJson(e.Item, this.directoryService, "Item Removed Event");
        }

        private string GetCurrentVersion()
        {
            var releaseTag = GetAssemblyReleaseTag(this.GetType().Assembly);
            if (!string.IsNullOrWhiteSpace(releaseTag))
            {
                return releaseTag;
            }

            var version = this.GetType().Assembly.GetName().Version;
            return version == null ? "未知" : $"v{version.ToString(4)}";
        }

        private StatusItem BuildVersionStatusItem()
        {
            var currentVersion = GetCurrentVersion();
            var latestVersion = latestReleaseVersionCache;

            var normalizedCurrent = NormalizeVersionLabel(currentVersion);
            var normalizedLatest = NormalizeVersionLabel(latestVersion);
            var currentText = string.IsNullOrWhiteSpace(currentVersion) ? "未知" : currentVersion;
            var latestText = string.IsNullOrWhiteSpace(latestVersion) ? "加载中" : latestVersion;

            var status = ItemStatus.Unknown;
            var caption = "版本信息";

            if (!string.IsNullOrWhiteSpace(normalizedCurrent) && !string.IsNullOrWhiteSpace(normalizedLatest))
            {
                if (string.Equals(normalizedCurrent, normalizedLatest, StringComparison.OrdinalIgnoreCase))
                {
                    status = ItemStatus.Succeeded;
                    caption = "已是最新版本";
                }
                else
                {
                    status = ItemStatus.Warning;
                    caption = "发现新版本";
                }
            }

            return new StatusItem(
                caption,
                $"当前版本：{currentText}\n最新版本：{latestText}",
                status);
        }

        private static string NormalizeVersionLabel(string version)
        {
            return string.IsNullOrWhiteSpace(version)
                ? string.Empty
                : version.Trim().TrimStart('v', 'V');
        }

        private static string GetAssemblyReleaseTag(Assembly assembly)
        {
            if (assembly == null)
            {
                return null;
            }

            var releaseTagAttribute = assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attr => string.Equals(attr.Key, "ReleaseTag", StringComparison.Ordinal));

            return string.IsNullOrWhiteSpace(releaseTagAttribute?.Value) ? null : releaseTagAttribute.Value.Trim();
        }

        internal void RefreshReleaseInfoInBackground()
        {
            var shouldStart = false;
            lock (ReleaseHistoryLock)
            {
                if (!releaseHistoryRefreshInProgress)
                {
                    releaseHistoryRefreshInProgress = true;
                    shouldStart = true;
                }
            }

            if (!shouldStart)
            {
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    RefreshReleaseHistoryCache(forceRefresh: false);
                }
                catch (Exception ex)
                {
                    this.logger.Info($"后台获取 GitHub 历史版本失败: {ex.Message}");
                    this.logger.Debug(ex.StackTrace);
                }
                finally
                {
                    lock (ReleaseHistoryLock)
                    {
                        releaseHistoryRefreshInProgress = false;
                    }
                }
            });
        }

        private void RefreshReleaseHistoryCache(bool forceRefresh)
        {
            var now = ConfiguredDateTime.NowOffset;
            var currentChannel = GetSelectedUpdateChannel();
            if (!forceRefresh &&
                now - releaseHistoryCheckedAt < LatestVersionCacheDuration &&
                string.Equals(releaseHistoryChannelCache, currentChannel, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(releaseHistoryBodyCache))
            {
                return;
            }

            lock (ReleaseHistoryLock)
            {
                if (!forceRefresh &&
                    now - releaseHistoryCheckedAt < LatestVersionCacheDuration &&
                    string.Equals(releaseHistoryChannelCache, currentChannel, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(releaseHistoryBodyCache))
                {
                    return;
                }

                var historyInfo = FetchReleaseHistoryInfo(currentChannel);
                if (historyInfo.IsSuccess)
                {
                    releaseHistoryCheckedAt = now;
                    releaseHistoryChannelCache = currentChannel;
                    releaseHistoryBodyCache = historyInfo.HistoryBody;
                    latestReleaseVersionCache = historyInfo.LatestVersion;
                    return;
                }

                releaseHistoryCheckedAt = DateTimeOffset.MinValue;
                releaseHistoryChannelCache = null;
                releaseHistoryBodyCache = historyInfo.HistoryBody;
                latestReleaseVersionCache = historyInfo.LatestVersion;
            }
        }

        private ReleaseHistoryInfo FetchReleaseHistoryInfo(string updateChannel)
        {
            try
            {
                var latestVersion = "未知";
                var preferBeta = string.Equals(
                    updateChannel,
                    MainPageOptions.UpdateChannelOption.Beta.ToString(),
                    StringComparison.OrdinalIgnoreCase);
                var requestOptions = new HttpRequestOptions
                {
                    Url = $"{GitHubReleaseHistoryUrl}1",
                    AcceptHeader = "application/vnd.github+json",
                    UserAgent = "MediaInfoKeeper",
                    EnableDefaultUserAgent = false,
                    LogRequest = true,
                    LogResponse = true,
                    TimeoutMs = 3000
                };
                var token = this.Options?.GetEffectiveUpdatePluginOptions()?.GitHubToken;
                if (!string.IsNullOrWhiteSpace(token))
                {
                    requestOptions.RequestHeaders["Authorization"] = $"token {token}";
                }

                using var response = this.httpClient.SendAsync(requestOptions, "GET").GetAwaiter().GetResult();
                using var responseReader = new StreamReader(response.Content);
                var responseBody = responseReader.ReadToEnd();
                if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                {
                    this.logger.Info($"获取 GitHub 历史版本失败: {(int)response.StatusCode} {response.StatusCode}");
                    this.logger.Info($"GitHub 响应体: {responseBody}");
                    return new ReleaseHistoryInfo("获取失败", "获取失败", false);
                }

                using var document = JsonDocument.Parse(responseBody);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var releases = new List<ReleaseHistoryEntry>();
                    foreach (var release in document.RootElement.EnumerateArray())
                    {
                        var isDraft = release.TryGetProperty("draft", out var draftElement) &&
                                      draftElement.ValueKind == JsonValueKind.True;
                        if (isDraft)
                        {
                            continue;
                        }

                        var isPrerelease = release.TryGetProperty("prerelease", out var prereleaseElement) &&
                                           prereleaseElement.ValueKind == JsonValueKind.True;

                        var tag = release.TryGetProperty("tag_name", out var tagElement)
                            ? tagElement.GetString()
                            : string.Empty;
                        var name = release.TryGetProperty("name", out var nameElement)
                            ? nameElement.GetString()
                            : string.Empty;
                        var body = release.TryGetProperty("body", out var bodyElement)
                            ? bodyElement.GetString()
                            : string.Empty;
                        var publishedAt = release.TryGetProperty("published_at", out var publishedElement)
                            ? publishedElement.GetString()
                            : string.Empty;
                        var createdAt = release.TryGetProperty("created_at", out var createdElement)
                            ? createdElement.GetString()
                            : string.Empty;
                        var publishedAtLocal = publishedAt;
                        if (!string.IsNullOrWhiteSpace(publishedAt) &&
                            DateTimeOffset.TryParse(publishedAt, out var publishedOffset))
                        {
                            publishedAtLocal = ConfiguredDateTime
                                .ToConfiguredOffset(publishedOffset)
                                .ToString("yyyy-MM-dd HH:mm:ss");
                        }

                        if (string.IsNullOrWhiteSpace(body))
                        {
                            body = "无更新说明";
                        }

                        releases.Add(new ReleaseHistoryEntry(
                            tag,
                            name,
                            body,
                            publishedAtLocal,
                            isPrerelease,
                            GetReleaseSortTime(publishedAt, createdAt)));
                    }

                    var orderedReleases = releases
                        .OrderByDescending(r => r.SortTime)
                        .ThenByDescending(r => r.Tag ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .ThenByDescending(r => r.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var latestRelease = preferBeta
                        ? orderedReleases.FirstOrDefault()
                        : orderedReleases.FirstOrDefault(r => !r.IsPrerelease);
                    if (latestRelease != null)
                    {
                        latestVersion = !string.IsNullOrWhiteSpace(latestRelease.Tag) ? latestRelease.Tag.Trim()
                            : !string.IsNullOrWhiteSpace(latestRelease.Name) ? latestRelease.Name.Trim()
                            : "未知";
                    }

                    var sb = new StringBuilder();
                    foreach (var release in orderedReleases)
                    {
                        sb.Append(string.IsNullOrWhiteSpace(release.Tag) ? "Release" : release.Tag.Trim());
                        if (!string.IsNullOrWhiteSpace(release.Name))
                        {
                            sb.Append(" - ").Append(release.Name.Trim());
                        }
                        if (release.IsPrerelease)
                        {
                            sb.Append(" [Prerelease]");
                        }

                        if (!string.IsNullOrWhiteSpace(release.PublishedAtLocal))
                        {
                            sb.Append(" (").Append(release.PublishedAtLocal.Trim()).Append(')');
                        }

                        sb.AppendLine();
                        sb.AppendLine(release.Body.Trim());
                        sb.AppendLine("----");
                    }

                    if (sb.Length == 0)
                    {
                        return new ReleaseHistoryInfo("暂无发布记录", latestVersion, true);
                    }

                    return new ReleaseHistoryInfo(sb.ToString().TrimEnd(), latestVersion, true);
                }
                
                return new ReleaseHistoryInfo("暂无发布记录", latestVersion, true);
            }
            catch (Exception ex)
            {
                this.logger.Info($"获取 GitHub 历史版本失败: {ex.Message}");
                this.logger.Debug(ex.StackTrace);
                return new ReleaseHistoryInfo("获取失败", "获取失败", false);
            }
        }

        private string GetSelectedUpdateChannel()
        {
            var updateChannel = this.Options?.GetEffectiveUpdatePluginOptions()?.UpdateChannel;
            return string.IsNullOrWhiteSpace(updateChannel)
                ? MainPageOptions.UpdateChannelOption.Stable.ToString()
                : updateChannel;
        }

        internal static DateTimeOffset GetReleaseSortTime(string publishedAt, string createdAt)
        {
            if (DateTimeOffset.TryParse(publishedAt, out var publishedOffset))
            {
                return publishedOffset;
            }

            if (DateTimeOffset.TryParse(createdAt, out var createdOffset))
            {
                return createdOffset;
            }

            return DateTimeOffset.MinValue;
        }

        private readonly struct ReleaseHistoryInfo
        {
            public ReleaseHistoryInfo(string historyBody, string latestVersion, bool isSuccess)
            {
                HistoryBody = historyBody;
                LatestVersion = latestVersion;
                IsSuccess = isSuccess;
            }

            public string HistoryBody { get; }

            public string LatestVersion { get; }

            public bool IsSuccess { get; }
        }

        private sealed class ReleaseHistoryEntry
        {
            public ReleaseHistoryEntry(
                string tag,
                string name,
                string body,
                string publishedAtLocal,
                bool isPrerelease,
                DateTimeOffset sortTime)
            {
                Tag = tag;
                Name = name;
                Body = body;
                PublishedAtLocal = publishedAtLocal;
                IsPrerelease = isPrerelease;
                SortTime = sortTime;
            }

            public string Tag { get; }

            public string Name { get; }

            public string Body { get; }

            public string PublishedAtLocal { get; }

            public bool IsPrerelease { get; }

            public DateTimeOffset SortTime { get; }
        }
    }
}
