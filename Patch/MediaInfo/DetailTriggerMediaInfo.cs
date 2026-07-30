using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.Patch {
    /// <summary>
    ///     在详情接口访问视频或音频条目时，按需后台补齐 MediaInfo。
    /// </summary>
    public static class DetailTriggerMediaInfo {
        private static readonly object QueueSync = new();
        private static readonly HashSet<long> PendingItems = new();

        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo getItemMethod;
        private static PropertyInfo idProperty;
        private static bool isEnabled;
        private static bool isPatched;

        public static bool IsReady => harmony != null && (!isEnabled || isPatched);

        public static void Initialize(ILogger pluginLogger, bool enable) {
            if (harmony != null) {
                Configure(enable);
                return;
            }

            logger = pluginLogger;
            isEnabled = enable;

            try {
                var apiAssembly = Assembly.Load("Emby.Api");
                var assemblyVersion = apiAssembly?.GetName().Version;
                var userLibraryServiceType = apiAssembly?.GetType("Emby.Api.UserLibrary.UserLibraryService");
                var getItemRequestType = apiAssembly?.GetType("Emby.Api.UserLibrary.GetItem");
                idProperty = getItemRequestType?.GetProperty("Id", BindingFlags.Instance | BindingFlags.Public);

                if (getItemRequestType == null || idProperty == null) {
                    PatchLog.InitFailed(logger, nameof(DetailTriggerMediaInfo), "GetItem 请求类型缺失");
                    return;
                }

                getItemMethod = PatchMethodResolver.Resolve(
                    userLibraryServiceType,
                    assemblyVersion,
                    new MethodSignatureProfile {
                        Name = "userlibraryservice-get-item-exact",
                        MethodName = "Get",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        IsStatic = false,
                        ParameterTypes = new[] { getItemRequestType },
                        ReturnType = typeof(Task<object>)
                    },
                    logger,
                    "DetailTriggerMediaInfo.UserLibraryService.Get(GetItem)");

                if (getItemMethod == null) {
                    PatchLog.InitFailed(logger, nameof(DetailTriggerMediaInfo),
                        "UserLibraryService.Get(GetItem) 目标方法缺失");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.detailtriggermediainfo");

                if (isEnabled) Patch();
            }
            catch (Exception ex) {
                logger?.Error("DetailTriggerMediaInfo 初始化失败。");
                logger?.Error(ex.Message);
                logger?.Error(ex.ToString());
                harmony = null;
                isEnabled = false;
            }
        }

        public static void Configure(bool enable) {
            isEnabled = enable;

            if (harmony == null) return;

            if (isEnabled)
                Patch();
            else
                Unpatch();
        }

        private static void Patch() {
            if (isPatched || harmony == null || getItemMethod == null) return;

            harmony.Patch(
                getItemMethod,
                postfix: new HarmonyMethod(typeof(DetailTriggerMediaInfo), nameof(GetItemPostfix)));
            PatchLog.Patched(logger, nameof(DetailTriggerMediaInfo), getItemMethod);
            isPatched = true;
        }

        private static void Unpatch() {
            if (!isPatched || harmony == null || getItemMethod == null) return;

            harmony.Unpatch(getItemMethod, HarmonyPatchType.Postfix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPostfix]
        private static void GetItemPostfix(object request) {
            if (!isEnabled || Plugin.Instance?.Options?.MainPage?.PlugginEnabled != true) return;

            if (Plugin.LibraryManager?.IsScanRunning == true) return;

            var itemId = idProperty?.GetValue(request) as string;
            if (string.IsNullOrWhiteSpace(itemId)) return;

            BaseItem item;
            try {
                item = GetItemById(itemId);
            }
            catch (Exception ex) {
                logger?.Debug("DetailTriggerMediaInfo - 获取条目失败: {0}", ex.Message);
                return;
            }

            if (!(item is Video) && !(item is Audio)) return;

            var mediaInfoService = Plugin.MediaInfoService;
            if (mediaInfoService == null) return;

            // Strm 直链预解析：无论媒体信息是否已存在，浏览 strm 条目时都触发
            var itemPath = item.Path;
            if (!string.IsNullOrWhiteSpace(itemPath) && LibraryService.IsFileShortcut(itemPath)) {
                if (Plugin.Instance?.Options?.MediaInfo?.EnableStrmPrefetch == true) {
                    _ = Task.Run(() => {
                        try {
                            logger?.Info(
                                "DetailTriggerMediaInfo - 浏览详情触发 Strm 直链预解析: {0}",
                                item.FileName ?? item.Path ?? item.Name);

                            // 1. 触发完整媒体源解析链路（网盘插件拦截并换取真实直链）
                            var mediaSources = mediaInfoService.GetStaticMediaSources(item, true);
                            var resolvedCount = mediaSources.Count(
                                ms => ms != null && !string.IsNullOrWhiteSpace(ms.Path));

                            // 2. 读取 strm 内容，对 HTTP(S) URL 发起 HEAD 请求触发远端秒传
                            var strmUrl = ReadStrmUrl(itemPath);
                            if (!string.IsNullOrWhiteSpace(strmUrl) &&
                                Uri.TryCreate(strmUrl, UriKind.Absolute, out var uri) &&
                                (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))) {
                                TriggerStrmHttpPrefetch(uri.AbsoluteUri, item.FileName ?? item.Path ?? item.Name);
                            }

                            logger?.Info(
                                "DetailTriggerMediaInfo - Strm 直链预解析完成: {0}, 媒体源={1}",
                                item.FileName ?? item.Path ?? item.Name, resolvedCount);
                        }
                        catch (Exception ex) {
                            logger?.Error(
                                "DetailTriggerMediaInfo - Strm 直链预解析失败: {0}", ex.Message);
                        }
                    });
                }
            }

            // 媒体信息提取：仅缺流条目
            foreach (var mediaSource in mediaInfoService.GetStaticMediaSources(item, true)) {
                if (mediaSource?.MediaStreams?.Any(stream =>
                        stream != null &&
                        !stream.IsExternal &&
                        (stream.Type == MediaStreamType.Audio || stream.Type == MediaStreamType.Video)) == true)
                    continue;

                QueueExtraction(GetItemById(mediaSource?.ItemId) ?? item);
            }
        }

        private static BaseItem GetItemById(string itemId) {
            if (long.TryParse(itemId, out var internalId)) return Plugin.LibraryManager?.GetItemById(internalId);

            if (Guid.TryParse(itemId, out var guid)) return Plugin.LibraryManager?.GetItemById(guid);

            return null;
        }

        private static void QueueExtraction(BaseItem item) {
            lock (QueueSync) {
                if (!PendingItems.Add(item.InternalId)) return;
            }

            Task.Run(async () => {
                try {
                    logger?.Info("DetailTriggerMediaInfo - 浏览详情触发媒体信息提取: {0}", item.FileName ?? item.Path ?? item.Name);
                    await MediaInfoRunner
                        .ExtractMediaInfoAsync(item.InternalId, "浏览详情", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) {
                    logger?.Error("DetailTriggerMediaInfo - 提取媒体信息失败: {0}", ex.Message);
                    logger?.Debug(ex.StackTrace);
                }
                finally {
                    lock (QueueSync) {
                        PendingItems.Remove(item.InternalId);
                    }
                }
            }).ConfigureAwait(false);
        }

        private static void TriggerStrmHttpPrefetch(string url, string itemName) {
            var httpClient = Plugin.SharedHttpClient;
            if (httpClient == null) return;

            try {
                using var response = httpClient.SendAsync(
                    new HttpRequestOptions {
                        Url = url,
                        TimeoutMs = 5000,
                        BufferContent = false,
                        LogErrors = false,
                        LogRequest = false,
                        LogResponse = false,
                        EnableHttpCompression = false,
                        EnableKeepAlive = false,
                        EnableDefaultUserAgent = false,
                        ThrowOnErrorResponse = false
                    },
                    "HEAD").GetAwaiter().GetResult();

                logger?.Debug(
                    "DetailTriggerMediaInfo - Strm HTTP 触发完成: {0}, StatusCode={1}",
                    itemName,
                    response?.StatusCode);
            }
            catch (Exception ex) {
                logger?.Debug(
                    "DetailTriggerMediaInfo - Strm HTTP 触发异常（不影响浏览）: {0}, {1}",
                    itemName, ex.Message);
            }
        }

        private static string ReadStrmUrl(string strmPath) {
            try {
                if (!File.Exists(strmPath)) return null;
                return File.ReadLines(strmPath)
                    .Select(l => l?.Trim())
                    .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#", StringComparison.Ordinal));
            }
            catch {
                return null;
            }
        }
    }
}
