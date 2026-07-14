using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 在详情接口访问视频或音频条目时，按需后台补齐 MediaInfo。
    /// </summary>
    public static class DetailTriggerMediaInfo
    {
        private static readonly object QueueSync = new object();
        private static readonly HashSet<long> PendingItems = new HashSet<long>();

        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo getItemMethod;
        private static PropertyInfo idProperty;
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
                var apiAssembly = Assembly.Load("Emby.Api");
                var assemblyVersion = apiAssembly?.GetName().Version;
                var userLibraryServiceType = apiAssembly?.GetType("Emby.Api.UserLibrary.UserLibraryService");
                var getItemRequestType = apiAssembly?.GetType("Emby.Api.UserLibrary.GetItem");
                idProperty = getItemRequestType?.GetProperty("Id", BindingFlags.Instance | BindingFlags.Public);

                if (getItemRequestType == null || idProperty == null)
                {
                    PatchLog.InitFailed(logger, nameof(DetailTriggerMediaInfo), "GetItem 请求类型缺失");
                    return;
                }

                getItemMethod = PatchMethodResolver.Resolve(
                    userLibraryServiceType,
                    assemblyVersion,
                    new MethodSignatureProfile
                    {
                        Name = "userlibraryservice-get-item-exact",
                        MethodName = "Get",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        IsStatic = false,
                        ParameterTypes = new[] { getItemRequestType },
                        ReturnType = typeof(Task<object>)
                    },
                    logger,
                    "DetailTriggerMediaInfo.UserLibraryService.Get(GetItem)");

                if (getItemMethod == null)
                {
                    PatchLog.InitFailed(logger, nameof(DetailTriggerMediaInfo), "UserLibraryService.Get(GetItem) 目标方法缺失");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.detailtriggermediainfo");

                if (isEnabled)
                {
                    Patch();
                }
            }
            catch (Exception ex)
            {
                logger?.Error("DetailTriggerMediaInfo 初始化失败。");
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
            if (isPatched || harmony == null || getItemMethod == null)
            {
                return;
            }

            harmony.Patch(
                getItemMethod,
                postfix: new HarmonyMethod(typeof(DetailTriggerMediaInfo), nameof(GetItemPostfix)));
            PatchLog.Patched(logger, nameof(DetailTriggerMediaInfo), getItemMethod);
            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null || getItemMethod == null)
            {
                return;
            }

            harmony.Unpatch(getItemMethod, HarmonyPatchType.Postfix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPostfix]
        private static void GetItemPostfix(object request)
        {
            if (!isEnabled || Plugin.Instance?.Options?.MainPage?.PlugginEnabled != true)
            {
                return;
            }

            if (Plugin.LibraryManager?.IsScanRunning == true)
            {
                return;
            }

            var itemId = idProperty?.GetValue(request) as string;
            if (string.IsNullOrWhiteSpace(itemId))
            {
                return;
            }

            BaseItem item;
            try
            {
                item = GetItemById(itemId);
            }
            catch (Exception ex)
            {
                logger?.Debug("DetailTriggerMediaInfo - 获取条目失败: {0}", ex.Message);
                return;
            }

            if (!(item is Video) && !(item is Audio))
            {
                return;
            }

            var mediaInfoService = Plugin.MediaInfoService;
            if (mediaInfoService == null)
            {
                return;
            }

            foreach (var mediaSource in mediaInfoService.GetStaticMediaSources(item, true))
            {
                if (mediaSource?.MediaStreams?.Any(stream =>
                        stream != null &&
                        !stream.IsExternal &&
                        (stream.Type == MediaStreamType.Audio || stream.Type == MediaStreamType.Video)) == true)
                {
                    continue;
                }

                QueueExtraction(GetItemById(mediaSource?.ItemId) ?? item);
            }
        }

        private static BaseItem GetItemById(string itemId)
        {
            if (long.TryParse(itemId, out var internalId))
            {
                return Plugin.LibraryManager?.GetItemById(internalId);
            }

            if (Guid.TryParse(itemId, out var guid))
            {
                return Plugin.LibraryManager?.GetItemById(guid);
            }

            return null;
        }

        private static void QueueExtraction(BaseItem item)
        {
            lock (QueueSync)
            {
                if (!PendingItems.Add(item.InternalId))
                {
                    return;
                }
            }

            Task.Run(async () =>
            {
                try
                {
                    logger?.Info("DetailTriggerMediaInfo - 浏览详情触发媒体信息提取: {0}", item.FileName ?? item.Path ?? item.Name);
                    await MediaInfoRunner
                        .ExtractMediaInfoAsync(item.InternalId, "浏览详情", cancellationToken: CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger?.Error("DetailTriggerMediaInfo - 提取媒体信息失败: {0}", ex.Message);
                    logger?.Debug(ex.StackTrace);
                }
                finally
                {
                    lock (QueueSync)
                    {
                        PendingItems.Remove(item.InternalId);
                    }
                }
            }).ConfigureAwait(false);
        }
    }
}
