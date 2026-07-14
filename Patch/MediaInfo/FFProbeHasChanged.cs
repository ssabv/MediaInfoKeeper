using System;
using System.Reflection;
using HarmonyLib;
using MediaInfoKeeper.Services;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 让插件主动提取 MediaInfo 时，即使 ValidationOnly 没检测到文件变化，也执行 ffprobe provider。
    /// </summary>
    public static class FFProbeHasChanged
    {
        private static Harmony harmony;
        private static MethodInfo hasChanged;
        private static ILogger logger;
        private static bool isEnabled;
        private static bool isPatched;

        public static bool IsReady => harmony != null && hasChanged != null && (!isEnabled || isPatched);

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
                var ffProbeProvider = embyProviders?.GetType("Emby.Providers.MediaInfo.FFProbeProvider");
                if (ffProbeProvider == null)
                {
                    PatchLog.InitFailed(logger, nameof(FFProbeHasChanged), "未找到 FFProbeProvider 类型");
                    return;
                }

                hasChanged = PatchMethodResolver.Resolve(
                    ffProbeProvider,
                    embyProviders.GetName().Version,
                    new MethodSignatureProfile
                    {
                        Name = "ffprobeprovider-haschanged-exact",
                        MethodName = "HasChanged",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        ParameterTypes = new[]
                        {
                            typeof(BaseMetadataResult),
                            typeof(LibraryOptions),
                            typeof(MetadataRefreshOptions),
                            typeof(IDirectoryService)
                        },
                        ReturnType = typeof(bool)
                    },
                    logger,
                    "FFProbeHasChanged.HasChanged");

                if (hasChanged == null)
                {
                    PatchLog.InitFailed(logger, nameof(FFProbeHasChanged), "未命中 HasChanged");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.ffprobe.haschanged");

                if (isEnabled)
                {
                    Patch();
                }
            }
            catch (Exception ex)
            {
                logger?.Error("FFProbeHasChanged 初始化失败。");
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
            if (isPatched || harmony == null || hasChanged == null)
            {
                return;
            }

            PatchLog.Patched(logger, nameof(FFProbeHasChanged), hasChanged);
            harmony.Patch(
                hasChanged,
                prefix: new HarmonyMethod(typeof(FFProbeHasChanged), nameof(HasChangedPrefix)));
            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null || hasChanged == null)
            {
                return;
            }

            harmony.Unpatch(hasChanged, HarmonyPatchType.Prefix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPrefix]
        private static bool HasChangedPrefix(BaseMetadataResult __0, ref bool __result)
        {
            var itemPath = __0?.BaseItem?.Path ?? __0?.BaseItem?.FileName;
            if (!isEnabled || !FfProcessGuard.HasExplicitAllowance() || !LibraryService.IsFileShortcut(itemPath))
            {
                return true;
            }

            __result = true;
            return false;
        }
    }
}
