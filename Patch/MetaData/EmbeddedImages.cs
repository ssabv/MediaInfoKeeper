using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 在刷新快捷方式音频时临时放开 Embedded Images 提取能力。
    /// </summary>
    public static class EmbeddedImages
    {
        private static readonly AsyncLocal<BaseItem> ShortcutItem = new AsyncLocal<BaseItem>();

        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo isShortcutGetter;
        private static MethodInfo supportsAudioEmbeddedImages;
        private static MethodInfo getImage;
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
                var audioImageProvider = embyProviders?.GetType("Emby.Providers.MediaInfo.AudioImageProvider");
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
                    "EmbeddedImages.BaseItem.get_IsShortcut");
                supportsAudioEmbeddedImages = PatchMethodResolver.Resolve(
                    audioImageProvider,
                    assemblyVersion,
                    new MethodSignatureProfile
                    {
                        Name = "audioimageprovider-supports-exact",
                        MethodName = "Supports",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        ParameterTypes = new[] { typeof(BaseItem) },
                        ReturnType = typeof(bool),
                        IsStatic = false
                    },
                    logger,
                    "EmbeddedImages.AudioImageProvider.Supports");
                getImage = PatchMethodResolver.Resolve(
                    audioImageProvider,
                    assemblyVersion,
                    new MethodSignatureProfile
                    {
                        Name = "audioimageprovider-getimage-exact",
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
                    "EmbeddedImages.AudioImageProvider.GetImage");

                if (isShortcutGetter == null ||
                    supportsAudioEmbeddedImages == null ||
                    getImage == null)
                {
                    PatchLog.InitFailed(logger, nameof(EmbeddedImages), "目标方法缺失");
                    return;
                }

                PatchLog.Patched(logger, nameof(EmbeddedImages), isShortcutGetter);
                PatchLog.Patched(logger, nameof(EmbeddedImages), supportsAudioEmbeddedImages);
                PatchLog.Patched(logger, nameof(EmbeddedImages), getImage);

                harmony = new Harmony("mediainfokeeper.embeddedimages");

                if (isEnabled)
                {
                    Patch();
                }
            }
            catch (Exception ex)
            {
                logger?.Error("EmbeddedImages 初始化失败。");
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
                prefix: new HarmonyMethod(typeof(EmbeddedImages), nameof(IsShortcutPrefix)));
            harmony.Patch(supportsAudioEmbeddedImages,
                prefix: new HarmonyMethod(typeof(EmbeddedImages), nameof(SupportsPrefix)),
                postfix: new HarmonyMethod(typeof(EmbeddedImages), nameof(SupportsPostfix)));
            harmony.Patch(getImage,
                prefix: new HarmonyMethod(typeof(EmbeddedImages), nameof(GetImagePrefix)));

            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null)
            {
                return;
            }

            harmony.Unpatch(isShortcutGetter, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Unpatch(supportsAudioEmbeddedImages, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Unpatch(supportsAudioEmbeddedImages, HarmonyPatchType.Postfix, harmony.Id);
            harmony.Unpatch(getImage, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Unpatch(getImage, HarmonyPatchType.Postfix, harmony.Id);

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
        private static bool SupportsPrefix(BaseItem item, out bool __state)
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
        private static void SupportsPostfix(BaseItem item, bool __result, bool __state)
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
                }
            }

            var mediaSource = Traverse.Create(itemResult).Property("MediaSource").GetValue<object>();
            if (mediaSource == null && item != null)
            {
                var restoredMediaSource = item.GetMediaSources(false, false, itemOptions)?.FirstOrDefault();
                if (restoredMediaSource != null)
                {
                    Traverse.Create(itemResult).Property("MediaSource").SetValue(restoredMediaSource);
                }
            }

            return true;
        }
    }
}
