using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 在 Emby 首次 Default 刷新新条目时屏蔽远程元数据和远程图片提供器。
    /// 插件后续主动发起的 FullRefresh 不在该作用域内，仍按媒体库配置执行远程刮削。
    /// </summary>
    public static class FirstRefreshRemoteBlock
    {
        private sealed class FirstRefreshScopeState
        {
            public BaseItem PreviousItem { get; set; }
        }

        private static readonly AsyncLocal<BaseItem> CurrentFirstRefreshItem = new AsyncLocal<BaseItem>();
        private static Harmony harmony;
        private static MethodInfo refreshSingleItem;
        private static MethodInfo canRefreshMetadata;
        private static MethodInfo canRefreshImage;
        private static ILogger logger;
        private static volatile bool configuredEnabled;

        public static bool IsReady => harmony != null &&
                                      refreshSingleItem != null &&
                                      canRefreshMetadata != null &&
                                      canRefreshImage != null;

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
                var embyProviders = Assembly.Load("Emby.Providers");
                var providerManagerType = embyProviders?.GetType("Emby.Providers.Manager.ProviderManager");
                if (providerManagerType == null)
                {
                    PatchLog.InitFailed(logger, nameof(FirstRefreshRemoteBlock), "未找到 ProviderManager 类型");
                    return;
                }

                var assemblyVersion = embyProviders.GetName().Version;
                refreshSingleItem = ResolveRefreshSingleItem(providerManagerType, assemblyVersion);
                canRefreshMetadata = ResolveCanRefreshMetadata(providerManagerType, assemblyVersion);
                canRefreshImage = ResolveCanRefreshImage(providerManagerType, assemblyVersion);

                if (refreshSingleItem == null || canRefreshMetadata == null || canRefreshImage == null)
                {
                    PatchLog.InitFailed(logger, nameof(FirstRefreshRemoteBlock), "ProviderManager 首次刷新远程屏蔽目标方法缺失");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.firstrefresh.remoteblock");
                PatchLog.Patched(logger, nameof(FirstRefreshRemoteBlock), refreshSingleItem);
                harmony.Patch(
                    refreshSingleItem,
                    prefix: new HarmonyMethod(typeof(FirstRefreshRemoteBlock), nameof(EnterFirstRefreshScopePrefix)),
                    postfix: new HarmonyMethod(typeof(FirstRefreshRemoteBlock), nameof(ExitFirstRefreshScopePostfix)));

                PatchLog.Patched(logger, nameof(FirstRefreshRemoteBlock), canRefreshMetadata);
                harmony.Patch(
                    canRefreshMetadata,
                    postfix: new HarmonyMethod(typeof(FirstRefreshRemoteBlock), nameof(BlockRemoteMetadataPostfix)));

                PatchLog.Patched(logger, nameof(FirstRefreshRemoteBlock), canRefreshImage);
                harmony.Patch(
                    canRefreshImage,
                    postfix: new HarmonyMethod(typeof(FirstRefreshRemoteBlock), nameof(BlockRemoteImagePostfix)));
            }
            catch (Exception ex)
            {
                logger?.Error("首次刷新远程刮削屏蔽补丁初始化失败");
                logger?.Error(ex.Message);
                logger?.Error(ex.ToString());
                harmony = null;
            }
        }

        public static void Configure(bool enabled)
        {
            configuredEnabled = enabled;
        }

        private static MethodInfo ResolveRefreshSingleItem(Type providerManagerType, Version assemblyVersion)
        {
            var itemUpdateType = Assembly.Load("MediaBrowser.Controller")
                ?.GetType("MediaBrowser.Controller.Library.ItemUpdateType");
            if (itemUpdateType == null)
            {
                PatchLog.InitFailed(logger, nameof(FirstRefreshRemoteBlock), "未找到 ItemUpdateType");
                return null;
            }

            return PatchMethodResolver.Resolve(
                providerManagerType,
                assemblyVersion,
                new MethodSignatureProfile
                {
                    Name = "refresh-single-item-exact",
                    MethodName = "RefreshSingleItem",
                    BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                    ParameterTypes = new[]
                    {
                        typeof(BaseItem),
                        typeof(MetadataRefreshOptions),
                        typeof(BaseItem[]),
                        typeof(LibraryOptions),
                        typeof(CancellationToken)
                    },
                    ReturnType = typeof(Task<>).MakeGenericType(itemUpdateType),
                    IsStatic = false
                },
                logger,
                "FirstRefreshRemoteBlock.ProviderManager.RefreshSingleItem");
        }

        private static MethodInfo ResolveCanRefreshMetadata(Type providerManagerType, Version assemblyVersion)
        {
            return PatchMethodResolver.Resolve(
                providerManagerType,
                assemblyVersion,
                new MethodSignatureProfile
                {
                    Name = "can-refresh-metadata-exact",
                    MethodName = "CanRefresh",
                    BindingFlags = BindingFlags.Static | BindingFlags.NonPublic,
                    ParameterTypes = new[]
                    {
                        typeof(IMetadataProvider),
                        typeof(BaseItem),
                        typeof(LibraryOptions),
                        typeof(bool),
                        typeof(bool),
                        typeof(bool)
                    },
                    ReturnType = typeof(bool),
                    IsStatic = true
                },
                logger,
                "FirstRefreshRemoteBlock.ProviderManager.CanRefreshMetadata");
        }

        private static MethodInfo ResolveCanRefreshImage(Type providerManagerType, Version assemblyVersion)
        {
            return PatchMethodResolver.Resolve(
                providerManagerType,
                assemblyVersion,
                new MethodSignatureProfile
                {
                    Name = "can-refresh-image-exact",
                    MethodName = "CanRefresh",
                    BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                    ParameterTypes = new[]
                    {
                        typeof(IImageProvider),
                        typeof(BaseItem),
                        typeof(LibraryOptions),
                        typeof(ImageRefreshOptions),
                        typeof(bool),
                        typeof(bool)
                    },
                    ReturnType = typeof(bool),
                    IsStatic = false
                },
                logger,
                "FirstRefreshRemoteBlock.ProviderManager.CanRefreshImage");
        }

        private static void EnterFirstRefreshScopePrefix(BaseItem __0, MetadataRefreshOptions __1, out FirstRefreshScopeState __state)
        {
            __state = null;
            if (!configuredEnabled ||
                __0 == null ||
                __1 == null ||
                __0.DateLastRefreshed != default(DateTimeOffset) ||
                __1.MetadataRefreshMode != MetadataRefreshMode.Default)
            {
                return;
            }

            __state = new FirstRefreshScopeState
            {
                PreviousItem = CurrentFirstRefreshItem.Value
            };
            CurrentFirstRefreshItem.Value = __0;
        }

        private static void ExitFirstRefreshScopePostfix(ref object __result, FirstRefreshScopeState __state)
        {
            if (__state == null)
            {
                return;
            }

            if (__result is Task task)
            {
                __result = WrapWithFirstRefreshScope(task, __state);
                return;
            }

            CurrentFirstRefreshItem.Value = __state.PreviousItem;
        }

        private static void BlockRemoteMetadataPostfix(IMetadataProvider provider, BaseItem item, ref bool __result)
        {
            var scopedItem = CurrentFirstRefreshItem.Value;
            if (__result &&
                configuredEnabled &&
                scopedItem != null &&
                item != null &&
                scopedItem.InternalId == item.InternalId &&
                provider is IRemoteMetadataProvider &&
                !(provider is IForcedProvider))
            {
                __result = false;
            }
        }

        private static void BlockRemoteImagePostfix(IImageProvider provider, BaseItem item, ref bool __result)
        {
            var scopedItem = CurrentFirstRefreshItem.Value;
            if (__result &&
                configuredEnabled &&
                scopedItem != null &&
                item != null &&
                scopedItem.InternalId == item.InternalId &&
                provider is IRemoteImageProvider)
            {
                __result = false;
            }
        }

        private static object WrapWithFirstRefreshScope(Task task, FirstRefreshScopeState state)
        {
            var taskType = task.GetType();
            if (taskType == typeof(Task))
            {
                return AwaitTask(task, state);
            }

            if (taskType.IsGenericType && taskType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = taskType.GetGenericArguments()[0];
                var method = typeof(FirstRefreshRemoteBlock)
                    .GetMethod(nameof(AwaitGenericTask), BindingFlags.Static | BindingFlags.NonPublic)
                    ?.MakeGenericMethod(resultType);
                return method?.Invoke(null, new object[] { task, state }) ?? task;
            }

            return task;
        }

        private static async Task AwaitTask(Task task, FirstRefreshScopeState state)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            finally
            {
                CurrentFirstRefreshItem.Value = state.PreviousItem;
            }
        }

        private static async Task<T> AwaitGenericTask<T>(Task<T> task, FirstRefreshScopeState state)
        {
            try
            {
                return await task.ConfigureAwait(false);
            }
            finally
            {
                CurrentFirstRefreshItem.Value = state.PreviousItem;
            }
        }
    }
}
