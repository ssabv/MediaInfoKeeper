using System;
using System.Reflection;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 图片列表写入数据库成功后，同步覆盖音频主图 JSON。
    /// </summary>
    public static class EmbeddedImageJsonSync
    {
        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo saveImages;
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
                var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
                var sqliteItemRepository =
                    embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.Data.SqliteItemRepository");
                var version = embyServerImplementationsAssembly.GetName().Version;
                saveImages = PatchMethodResolver.Resolve(
                    sqliteItemRepository,
                    version,
                    new MethodSignatureProfile
                    {
                        Name = "sqliteitemrepository-saveimages-exact",
                        MethodName = "SaveImages",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                       BindingFlags.NonPublic,
                        ParameterTypes = new[] { typeof(long), typeof(ItemImageInfo[]) }
                    },
                    logger,
                    "EmbeddedImageJsonSync.SaveImages");

                if (saveImages == null)
                {
                    PatchLog.InitFailed(logger, nameof(EmbeddedImageJsonSync), "目标方法缺失");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.database.embeddedimagejsonpersist");

                if (isEnabled)
                {
                    Patch();
                }
            }
            catch (Exception e)
            {
                logger?.Error("EmbeddedImageJsonSync 初始化失败。");
                logger?.Error(e.Message);
                logger?.Error(e.ToString());
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

            harmony.Patch(saveImages,
                postfix: new HarmonyMethod(typeof(EmbeddedImageJsonSync), nameof(SaveImagesPostfix)));
            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null)
            {
                return;
            }

            harmony.Unpatch(saveImages, HarmonyPatchType.Postfix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPostfix]
        private static void SaveImagesPostfix(long id, ItemImageInfo[] images, bool __runOriginal)
        {
            if (!__runOriginal ||
                images == null ||
                !Array.Exists(images, image => image?.Type == ImageType.Primary))
            {
                return;
            }

            var item = Plugin.LibraryManager?.GetItemById(id);
            if (item is not Audio)
            {
                return;
            }

            try
            {
                Plugin.EmbeddedInfoStore?.OverWriteImageToFile(item);
            }
            catch (Exception ex)
            {
                logger?.Error($"音频主图已写入数据库，但同步 JSON 失败: {item.FileName ?? item.Path ?? item.InternalId.ToString()}");
                logger?.Error(ex.Message);
                logger?.Debug(ex.StackTrace);
            }
        }
    }
}
