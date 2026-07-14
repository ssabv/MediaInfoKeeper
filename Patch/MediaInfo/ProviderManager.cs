using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 在 ProviderManager 刷新媒体项期间携带条目上下文。
    /// 仅继承插件外层显式 ffprobe/ffmpeg 放行；普通 Emby 刷新不因条目类型自动放行。
    /// </summary>
    public static class ProviderManager
    {
        private static Harmony harmony;
        private static MethodInfo refreshItem;
        private static MethodInfo refreshItemByNameChildren;
        private static MethodInfo refreshSingleItem;
        private static ILogger logger;

        public static bool IsReady => harmony != null &&
                                      (refreshItem != null || refreshItemByNameChildren != null || refreshSingleItem != null);

        public static void Initialize(ILogger pluginLogger, bool enabled)
        {
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
                var embyProviders = Assembly.Load("Emby.Providers");
                var providerManagerType = embyProviders?.GetType("Emby.Providers.Manager.ProviderManager");
                if (providerManagerType == null)
                {
                    PatchLog.InitFailed(logger, nameof(ProviderManager), "未找到 ProviderManager 类型");
                    return;
                }

                var assemblyVersion = embyProviders.GetName().Version;
                refreshItem = ResolveMethod(
                    providerManagerType,
                    assemblyVersion,
                    "refresh-item-exact",
                    "RefreshItem",
                    new[]
                    {
                        typeof(BaseItem),
                        typeof(MetadataRefreshOptions),
                        typeof(CancellationToken)
                    },
                    typeof(Task),
                    "ProviderManager.RefreshItem");
                refreshItemByNameChildren = ResolveMethod(
                    providerManagerType,
                    assemblyVersion,
                    "refresh-item-by-name-children-exact",
                    "RefreshItemByNameChildren",
                    new[]
                    {
                        typeof(MusicAlbum),
                        typeof(MetadataRefreshOptions),
                        typeof(IProgress<double>),
                        typeof(CancellationToken)
                    },
                    typeof(Task),
                    "ProviderManager.RefreshItemByNameChildren");
                refreshSingleItem = ResolveRefreshSingleItem(providerManagerType, assemblyVersion);

                if (refreshItem == null && refreshItemByNameChildren == null && refreshSingleItem == null)
                {
                    PatchLog.InitFailed(logger, nameof(ProviderManager), "未命中任何 Refresh 方法");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.providermanager");
                PatchMethod(refreshItem, nameof(RefreshItemPrefix), nameof(RefreshItemPostfix));
                PatchMethod(refreshItemByNameChildren, nameof(RefreshItemByNameChildrenPrefix), nameof(RefreshItemByNameChildrenPostfix));
                PatchMethod(refreshSingleItem, nameof(RefreshSingleItemPrefix), nameof(RefreshSingleItemPostfix));
            }
            catch (Exception ex)
            {
                logger?.Error("ProviderManager patch 初始化失败");
                logger?.Error(ex.Message);
                logger?.Error(ex.ToString());
                harmony = null;
            }
        }

        public static void Configure(bool enabled)
        {
            // Harmony 安装后不卸载；是否真正放行由外层显式 scope 决定。
        }

        private static MethodInfo ResolveMethod(
            Type providerManagerType,
            Version assemblyVersion,
            string profileName,
            string methodName,
            Type[] parameterTypes,
            Type returnType,
            string context)
        {
            return PatchMethodResolver.Resolve(
                providerManagerType,
                assemblyVersion,
                new MethodSignatureProfile
                {
                    Name = profileName,
                    MethodName = methodName,
                    BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    ParameterTypes = parameterTypes,
                    ReturnType = returnType
                },
                logger,
                context);
        }

        private static MethodInfo ResolveRefreshSingleItem(Type providerManagerType, Version assemblyVersion)
        {
            var itemUpdateType = Assembly.Load("MediaBrowser.Controller")
                ?.GetType("MediaBrowser.Controller.Library.ItemUpdateType");
            if (itemUpdateType == null)
            {
                PatchLog.InitFailed(logger, nameof(ProviderManager), "未找到 ItemUpdateType");
                return null;
            }

            return ResolveMethod(
                providerManagerType,
                assemblyVersion,
                "refresh-single-item-exact",
                "RefreshSingleItem",
                new[]
                {
                    typeof(BaseItem),
                    typeof(MetadataRefreshOptions),
                    typeof(BaseItem[]),
                    typeof(LibraryOptions),
                    typeof(CancellationToken)
                },
                typeof(Task<>).MakeGenericType(itemUpdateType),
                "ProviderManager.RefreshSingleItem");
        }

        private static void PatchMethod(MethodInfo method, string prefix, string postfix)
        {
            if (method == null)
            {
                return;
            }

            PatchLog.Patched(logger, nameof(ProviderManager), method);
            harmony.Patch(
                method,
                prefix: new HarmonyMethod(typeof(ProviderManager), prefix),
                postfix: new HarmonyMethod(typeof(ProviderManager), postfix));
        }

        private static void RefreshItemPrefix(BaseItem __0, MetadataRefreshOptions __1, out FfProcessGuard.AllowanceHandle __state)
        {
            __state = BeginRefreshFfprocessAllowance(__0, __1);
        }

        private static void RefreshItemPostfix(BaseItem __0, ref Task __result, FfProcessGuard.AllowanceHandle __state)
        {
            CompleteRefreshFfprocessAllowance(__0, ref __result, __state);
        }

        private static void RefreshItemByNameChildrenPrefix(MusicAlbum __0, MetadataRefreshOptions __1, out FfProcessGuard.AllowanceHandle __state)
        {
            __state = BeginRefreshFfprocessAllowance(__0, __1);
        }

        private static void RefreshItemByNameChildrenPostfix(MusicAlbum __0, ref Task __result, FfProcessGuard.AllowanceHandle __state)
        {
            CompleteRefreshFfprocessAllowance(__0, ref __result, __state);
        }

        private static void RefreshSingleItemPrefix(BaseItem __0, MetadataRefreshOptions __1, out FfProcessGuard.AllowanceHandle __state)
        {
            __state = BeginRefreshFfprocessAllowance(__0, __1);
        }

        private static void RefreshSingleItemPostfix(BaseItem __0, ref object __result, FfProcessGuard.AllowanceHandle __state)
        {
            if (__state == null)
            {
                return;
            }

            if (__result is Task task)
            {
                __result = AwaitWithScope(task, __state);
                return;
            }

            FfProcessGuard.EndAllow(__state);
        }

        private static FfProcessGuard.AllowanceHandle BeginRefreshFfprocessAllowance(BaseItem item, MetadataRefreshOptions options)
        {
            if (item == null)
            {
                return null;
            }

            var itemPath = item.Path ?? item.FileName;
            var allowFfProcess = FfProcessGuard.HasExplicitAllowance();
            if (!allowFfProcess && item is Video && !item.IsShortcut && options?.DirectoryService != null)
            {
                allowFfProcess = Plugin.ExternalFiles?.HasExternalFilesChanged(item, options.DirectoryService, true) == true;
            }

            return FfProcessGuard.BeginAllow(new FfProcessGuard.AllowanceContext
            {
                ItemInternalId = item.InternalId,
                ItemPath = itemPath,
                AllowFfProcess = allowFfProcess
            });
        }

        private static void CompleteRefreshFfprocessAllowance(BaseItem item, ref Task task, FfProcessGuard.AllowanceHandle allowance)
        {
            if (allowance == null)
            {
                return;
            }

            task = task == null ? null : AwaitTask(task, allowance);
            if (task == null)
            {
                FfProcessGuard.EndAllow(allowance);
            }
        }

        private static object AwaitWithScope(Task task, FfProcessGuard.AllowanceHandle allowance)
        {
            var taskType = task.GetType();
            if (taskType == typeof(Task))
            {
                return AwaitTask(task, allowance);
            }

            if (taskType.IsGenericType && taskType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = taskType.GetGenericArguments()[0];
                var method = typeof(ProviderManager)
                    .GetMethod(nameof(AwaitGenericTask), BindingFlags.Static | BindingFlags.NonPublic)
                    ?.MakeGenericMethod(resultType);
                return method?.Invoke(null, new object[] { task, allowance }) ?? task;
            }

            return task;
        }

        private static async Task AwaitTask(Task task, FfProcessGuard.AllowanceHandle allowance)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            finally
            {
                FfProcessGuard.EndAllow(allowance);
            }
        }

        private static async Task<T> AwaitGenericTask<T>(Task<T> task, FfProcessGuard.AllowanceHandle allowance)
        {
            try
            {
                return await task.ConfigureAwait(false);
            }
            finally
            {
                FfProcessGuard.EndAllow(allowance);
            }
        }
    }
}
