using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MediaBrowser.Controller.Api;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    public static class MetadataRefreshAllowFfProcess
    {
        private const string QueryParameterName = "AllowFfProcess";
        private static readonly AsyncLocal<bool> CurrentAllowance = new AsyncLocal<bool>();
        private static Harmony harmony;
        private static MethodInfo postMethod;
        private static ILogger logger;
        private static volatile bool configuredEnabled;

        public static bool IsReady => harmony != null && postMethod != null;

        public static bool HasCurrentAllowance => CurrentAllowance.Value;

        public static void Initialize(ILogger pluginLogger, bool enabled)
        {
            configuredEnabled = enabled;

            if (harmony != null)
            {
                return;
            }

            logger = pluginLogger;
            if (!enabled)
            {
                return;
            }

            try
            {
                var embyApi = Assembly.Load("Emby.Api");
                var itemRefreshServiceType = embyApi?.GetType("Emby.Api.ItemRefreshService");
                var refreshItemType = embyApi?.GetType("Emby.Api.RefreshItem");
                if (itemRefreshServiceType == null || refreshItemType == null)
                {
                    PatchLog.InitFailed(logger, nameof(MetadataRefreshAllowFfProcess), "未找到 ItemRefreshService 或 RefreshItem 类型");
                    return;
                }

                postMethod = PatchMethodResolver.Resolve(
                    itemRefreshServiceType,
                    embyApi.GetName().Version,
                    new MethodSignatureProfile
                    {
                        Name = "item-refresh-post-exact",
                        MethodName = "Post",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        ParameterTypes = new[] { refreshItemType },
                        ReturnType = typeof(void)
                    },
                    logger,
                    "MetadataRefreshAllowFfProcess.Post");

                if (postMethod == null)
                {
                    PatchLog.InitFailed(logger, nameof(MetadataRefreshAllowFfProcess), "未命中 ItemRefreshService.Post");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.metadatarefresh.allowffprocess");
                PatchLog.Patched(logger, nameof(MetadataRefreshAllowFfProcess), postMethod);
                harmony.Patch(
                    postMethod,
                    prefix: new HarmonyMethod(typeof(MetadataRefreshAllowFfProcess), nameof(PostPrefix)),
                    finalizer: new HarmonyMethod(typeof(MetadataRefreshAllowFfProcess), nameof(PostFinalizer)));
            }
            catch (Exception ex)
            {
                logger?.Error("刷新元数据允许 FF 处理补丁初始化失败");
                logger?.Error(ex.Message);
                logger?.Error(ex.ToString());
                harmony = null;
            }
        }

        public static void Configure(bool enabled)
        {
            configuredEnabled = enabled;
        }

        private static void PostPrefix(object __instance, out bool __state)
        {
            __state = CurrentAllowance.Value;
            CurrentAllowance.Value = configuredEnabled && ReadAllowFfProcess(__instance);
        }

        private static void PostFinalizer(bool __state)
        {
            CurrentAllowance.Value = __state;
        }

        private static bool ReadAllowFfProcess(object instance)
        {
            try
            {
                var request = (instance as BaseApiService)?.Request;
                var raw = request?.QueryString?[QueryParameterName];
                return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                logger?.Debug("读取允许 FF 处理参数失败: {0}", ex.Message);
                return false;
            }
        }
    }
}
