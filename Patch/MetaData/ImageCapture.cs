using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 在刷新快捷方式媒体时临时放开音视频图片抓取能力。
    /// </summary>
    public static class ImageCapture
    {
        private static readonly AsyncLocal<BaseItem> ShortcutItem = new AsyncLocal<BaseItem>();

        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo isShortcutGetter;
        private static MethodInfo supportsVideoImageCapture;
        private static MethodInfo getImage;
        private static MethodInfo supportsThumbnailsGetter;
        private static bool isEnabled;
        private static bool isPatched;

        public static bool IsReady => harmony != null && (!isEnabled || isPatched);

        public static void Initialize(ILogger pluginLogger, bool enable)
        {
            if (harmony != null)
            {
                Configure(enable);
                return;
            }

            logger = pluginLogger;
            isEnabled = enable;

            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var assemblyVersion = embyProviders?.GetName().Version;
                var videoImageProvider = embyProviders?.GetType("Emby.Providers.MediaInfo.VideoImageProvider");
                var controllerVersion = typeof(BaseItem).Assembly.GetName().Version;

                isShortcutGetter = PatchMethodResolver.Resolve(
                    typeof(BaseItem),
                    controllerVersion,
                    new MethodSignatureProfile
                    {
                        Name = "baseitem-get-isshortcut-exact",
                        MethodName = "get_IsShortcut",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        ParameterTypes = Type.EmptyTypes,
                        ReturnType = typeof(bool),
                        IsStatic = false
                    },
                    logger,
                    "ImageCapture.BaseItem.get_IsShortcut");
                supportsVideoImageCapture = PatchMethodResolver.Resolve(
                    videoImageProvider,
                    assemblyVersion,
                    new MethodSignatureProfile
                    {
                        Name = "videoimageprovider-supports-exact",
                        MethodName = "Supports",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        ParameterTypes = new[] { typeof(BaseItem) },
                        ReturnType = typeof(bool),
                        IsStatic = false
                    },
                    logger,
                    "ImageCapture.VideoImageProvider.Supports");
                getImage = PatchMethodResolver.Resolve(
                    videoImageProvider,
                    assemblyVersion,
                    new MethodSignatureProfile
                    {
                        Name = "videoimageprovider-getimage-exact",
                        MethodName = "GetImage",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        ParameterTypes = new[]
                        {
                            typeof(BaseMetadataResult),
                            typeof(BaseItem[]),
                            typeof(MediaBrowser.Model.Configuration.LibraryOptions),
                            typeof(ImageType),
                            typeof(CancellationToken)
                        },
                        ReturnType = typeof(Task<DynamicImageResponse>),
                        IsStatic = false
                    },
                    logger,
                    "ImageCapture.VideoImageProvider.GetImage");
                supportsThumbnailsGetter = PatchMethodResolver.Resolve(
                    typeof(Video),
                    controllerVersion,
                    new MethodSignatureProfile
                    {
                        Name = "video-get-supportsthumbnails-exact",
                        MethodName = "get_SupportsThumbnails",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        ParameterTypes = Type.EmptyTypes,
                        ReturnType = typeof(bool),
                        IsStatic = false
                    },
                    logger,
                    "ImageCapture.Video.get_SupportsThumbnails");

                if (isShortcutGetter == null ||
                    supportsVideoImageCapture == null ||
                    getImage == null ||
                    supportsThumbnailsGetter == null)
                {
                    PatchLog.InitFailed(logger, nameof(ImageCapture), "目标方法缺失");
                    return;
                }

                PatchLog.Patched(logger, nameof(ImageCapture), isShortcutGetter);
                PatchLog.Patched(logger, nameof(ImageCapture), supportsVideoImageCapture);
                PatchLog.Patched(logger, nameof(ImageCapture), getImage);
                PatchLog.Patched(logger, nameof(ImageCapture), supportsThumbnailsGetter);

                harmony = new Harmony("mediainfokeeper.imagecapture");

                if (isEnabled)
                {
                    Patch();
                }
            }
            catch (Exception ex)
            {
                logger?.Error("ImageCapture 初始化失败。");
                logger?.Error(ex.Message);
                logger?.Error(ex.ToString());
                harmony = null;
                isEnabled = false;
            }
        }

        public static void Configure(bool enable)
        {
            isEnabled = enable;

            if (harmony == null)
            {
                return;
            }

            if (isEnabled)
            {
                Patch();
            }
            else
            {
                Unpatch();
            }
        }

        private static void Patch()
        {
            if (isPatched || harmony == null)
            {
                return;
            }

            harmony.Patch(isShortcutGetter,
                prefix: new HarmonyMethod(typeof(ImageCapture), nameof(IsShortcutPrefix)));
            harmony.Patch(supportsVideoImageCapture,
                prefix: new HarmonyMethod(typeof(ImageCapture), nameof(SupportsImageCapturePrefix)),
                postfix: new HarmonyMethod(typeof(ImageCapture), nameof(SupportsImageCapturePostfix)));
            harmony.Patch(getImage,
                prefix: new HarmonyMethod(typeof(ImageCapture), nameof(GetImagePrefix)));
            harmony.Patch(supportsThumbnailsGetter,
                prefix: new HarmonyMethod(typeof(ImageCapture), nameof(SupportsThumbnailsGetterPrefix)),
                postfix: new HarmonyMethod(typeof(ImageCapture), nameof(SupportsThumbnailsGetterPostfix)));

            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null)
            {
                return;
            }

            harmony.Unpatch(isShortcutGetter, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Unpatch(supportsVideoImageCapture, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Unpatch(supportsVideoImageCapture, HarmonyPatchType.Postfix, harmony.Id);
            harmony.Unpatch(getImage, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Unpatch(getImage, HarmonyPatchType.Postfix, harmony.Id);
            harmony.Unpatch(supportsThumbnailsGetter, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Unpatch(supportsThumbnailsGetter, HarmonyPatchType.Postfix, harmony.Id);

            isPatched = false;
        }

        private static void PatchIsShortcutInstance(BaseItem item)
        {
            ShortcutItem.Value = item;
        }

        private static void UnpatchIsShortcutInstance()
        {
            ShortcutItem.Value = null;
        }

        [HarmonyPrefix]
        private static bool IsShortcutPrefix(BaseItem __instance, ref bool __result)
        {
            if (ShortcutItem.Value != null && __instance.InternalId == ShortcutItem.Value.InternalId)
            {
                __result = false;
                return false;
            }

            return true;
        }

        [HarmonyPrefix]
        private static bool SupportsImageCapturePrefix(BaseItem item, out bool __state)
        {
            __state = false;

            if (isEnabled &&
                item != null &&
                item.IsShortcut)
            {
                PatchIsShortcutInstance(item);
                __state = true;
            }

            return true;
        }

        [HarmonyPostfix]
        private static void SupportsImageCapturePostfix(BaseItem item, bool __result, bool __state)
        {
            if (__state)
            {
                UnpatchIsShortcutInstance();
            }
        }

        [HarmonyPrefix]
        private static bool GetImagePrefix(ref BaseMetadataResult itemResult)
        {
            var item = Traverse.Create(itemResult).Property("Item").GetValue<BaseItem>();
            var itemOptions = item == null ? null : Plugin.LibraryManager?.GetLibraryOptions(item);
            var itemHasMediaInfo = item != null && Plugin.MediaInfoService?.HasMediaInfo(item) == true;

            if (item != null && !itemHasMediaInfo)
            {
                Plugin.MediaSourceInfoStore?.ApplyToItem(item);
            }

            var streams = itemResult?.MediaStreams;
            if ((streams == null || streams.Length == 0) && item != null)
            {
                var restoredStreams = item.GetMediaStreams()?.ToArray();
                if (restoredStreams != null && restoredStreams.Length > 0)
                {
                    itemResult.MediaStreams = restoredStreams;
                    streams = restoredStreams;
                }
            }

            var mediaSource = Traverse.Create(itemResult).Property("MediaSource").GetValue<object>();
            if (mediaSource == null && item != null)
            {
                var restoredMediaSource = item.GetMediaSources(false, false, itemOptions)?.FirstOrDefault();
                if (restoredMediaSource != null)
                {
                    Traverse.Create(itemResult).Property("MediaSource").SetValue(restoredMediaSource);
                    mediaSource = restoredMediaSource;
                }
            }

            if (streams == null)
            {
                return true;
            }

            itemResult.MediaStreams = streams
                .Where(ms => ms.Type != MediaStreamType.EmbeddedImage)
                .ToArray();

            return true;
        }

        [HarmonyPrefix]
        private static bool SupportsThumbnailsGetterPrefix(BaseItem __instance, out (bool, ExtraType?) __state)
        {
            __state = (false, __instance?.ExtraType);

            if (__instance == null)
            {
                return true;
            }

            if (isEnabled && __instance.IsShortcut)
            {
                PatchIsShortcutInstance(__instance);
                __state.Item1 = true;
            }

            if (__instance.ExtraType.HasValue)
            {
                __instance.ExtraType = null;
            }

            return true;
        }

        [HarmonyPostfix]
        private static void SupportsThumbnailsGetterPostfix(BaseItem __instance, ref bool __result,
            (bool, ExtraType?) __state)
        {
            if (__state.Item1)
            {
                UnpatchIsShortcutInstance();
            }

            if (__instance == null || !__result || !__state.Item2.HasValue)
            {
                return;
            }

            __instance.ExtraType = __state.Item2;

            if (__state.Item2 == ExtraType.Trailer || __state.Item2 == ExtraType.ThemeVideo)
            {
                __result = false;
            }
        }
    }
}
