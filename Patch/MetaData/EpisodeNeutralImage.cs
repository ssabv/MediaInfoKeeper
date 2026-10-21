using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;

namespace MediaInfoKeeper.Patch {
    /// <summary>
    ///     对集（Episode）的远程图片结果做稳定重排，让无语言图片排在前面，优先被 Emby 采用。
    ///     Emby 请求 TMDB 图片时携带 include_image_language={首选语言},null，无语言图片本来就在结果内，
    ///     这里只调整顺序，既不改变可选范围，也不改动媒体库的图片语言设置。
    /// </summary>
    public static class EpisodeNeutralImage {
        private static readonly object InitLock = new();

        private static Harmony harmony;
        private static ILogger logger;
        private static bool isEnabled;

        private static MethodInfo providerGetAvailableRemoteImages;
        private static MethodInfo providerGetAvailableRemoteImagesAsync;

        public static bool IsReady { get; private set; }

        public static bool IsWaiting => false;

        public static void Initialize(ILogger pluginLogger, bool enable) {
            logger = pluginLogger;
            isEnabled = enable;

            lock (InitLock) {
                harmony ??= new Harmony("mediainfokeeper.episodeneutralimage");

                if (!IsReady) InstallProviderHooks();
            }
        }

        public static void Configure(bool enable) {
            isEnabled = enable;
        }

        private static void InstallProviderHooks() {
            try {
                var embyProviders = Assembly.Load("Emby.Providers");
                var version = embyProviders.GetName().Version;
                var providerManager = embyProviders.GetType("Emby.Providers.Manager.ProviderManager", false);
                if (providerManager == null) {
                    PatchLog.InitFailed(logger, nameof(EpisodeNeutralImage), "ProviderManager 未找到");
                    return;
                }

                providerGetAvailableRemoteImages = PatchMethodResolver.Resolve(
                    providerManager,
                    version,
                    new MethodSignatureProfile {
                        Name = "providermanager-getavailableremoteimages-sync",
                        MethodName = "GetAvailableRemoteImages",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        IsStatic = false,
                        ParameterTypes = new[] {
                            typeof(BaseItem),
                            typeof(LibraryOptions),
                            typeof(RemoteImageQuery),
                            typeof(CancellationToken)
                        },
                        ReturnType = typeof(Task<IEnumerable<RemoteImageInfo>>)
                    },
                    logger,
                    "EpisodeNeutralImage.ProviderManager.GetAvailableRemoteImages(sync)");

                providerGetAvailableRemoteImagesAsync = PatchMethodResolver.Resolve(
                    providerManager,
                    version,
                    new MethodSignatureProfile {
                        Name = "providermanager-getavailableremoteimages-async",
                        MethodName = "GetAvailableRemoteImages",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        IsStatic = false,
                        ParameterTypes = new[] {
                            typeof(BaseItem),
                            typeof(LibraryOptions),
                            typeof(RemoteImageQuery),
                            typeof(IDirectoryService),
                            typeof(CancellationToken)
                        },
                        ReturnType = typeof(Task<IEnumerable<RemoteImageInfo>>)
                    },
                    logger,
                    "EpisodeNeutralImage.ProviderManager.GetAvailableRemoteImages(async)");

                var patched = 0;
                patched += PatchMethod(providerGetAvailableRemoteImages);
                patched += PatchMethod(providerGetAvailableRemoteImagesAsync);

                IsReady = patched > 0;
                if (!IsReady) PatchLog.InitFailed(logger, nameof(EpisodeNeutralImage), "Provider hooks 安装失败");
            }
            catch (Exception ex) {
                PatchLog.InitFailed(logger, nameof(EpisodeNeutralImage), ex.Message);
                logger?.Error("EpisodeNeutralImage provider hooks failed: {0}", ex);
            }
        }

        private static int PatchMethod(MethodInfo method) {
            if (method == null || harmony == null) return 0;

            harmony.Patch(
                method,
                postfix: new HarmonyMethod(
                    typeof(EpisodeNeutralImage).GetMethod(nameof(GetAvailableRemoteImagesPostfix),
                        BindingFlags.Static | BindingFlags.NonPublic)));
            PatchLog.Patched(logger, nameof(EpisodeNeutralImage), method);
            return 1;
        }

        private static void GetAvailableRemoteImagesPostfix([HarmonyArgument(0)] BaseItem item,
            ref Task<IEnumerable<RemoteImageInfo>> __result) {
            if (!isEnabled || __result == null || !(item is Episode)) return;

            __result = PreferNeutralImagesAsync(__result, item);
        }

        private static async Task<IEnumerable<RemoteImageInfo>> PreferNeutralImagesAsync(
            Task<IEnumerable<RemoteImageInfo>> task, BaseItem item) {
            var images = await task.ConfigureAwait(false);
            if (images == null) return null;

            var list = images as IList<RemoteImageInfo> ?? images.ToList();
            if (list.Count < 2) return list;

            var neutralCount = list.Count(image => IsNeutral(image));
            if (neutralCount == 0 || neutralCount == list.Count) return list;

            var ordered = new List<RemoteImageInfo>(list.Count);
            ordered.AddRange(list.Where(IsNeutral));
            ordered.AddRange(list.Where(image => !IsNeutral(image)));

            logger?.Debug("EpisodeNeutralImage 重排：item={0}，无语言={1}，总数={2}",
                GetItemLabel(item), neutralCount, list.Count);
            return ordered;
        }

        private static bool IsNeutral(RemoteImageInfo image) {
            return string.IsNullOrWhiteSpace(image?.Language);
        }

        private static string GetItemLabel(BaseItem item) {
            return item?.Name ?? item?.FileName ?? item?.Path ?? item?.InternalId.ToString() ?? "<null>";
        }
    }
}
