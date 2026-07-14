using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 章节写入数据库成功后，同步覆盖对应条目的 JSON 持久化内容。
    /// </summary>
    public static class ChapterJsonSync
    {
        private static readonly AsyncLocal<bool> SkipPersist = new AsyncLocal<bool>();
        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo saveChapters;
        private static MethodInfo deleteChapters;
        private static bool isEnabled;
        private static bool isPatched;
        private static readonly AsyncLocal<int> AllowClearPersistCount = new AsyncLocal<int>();

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
                saveChapters = PatchMethodResolver.Resolve(
                    sqliteItemRepository,
                    version,
                    new MethodSignatureProfile
                    {
                        Name = "sqliteitemrepository-savechapters-with-clear-flag",
                        MethodName = "SaveChapters",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                       BindingFlags.NonPublic,
                        ParameterTypes = new[] { typeof(long), typeof(bool), typeof(List<ChapterInfo>) }
                    },
                    logger,
                    "ChapterJsonSync.SaveChapters(long,bool,List<ChapterInfo>)");
                deleteChapters = PatchMethodResolver.Resolve(
                    sqliteItemRepository,
                    version,
                    new MethodSignatureProfile
                    {
                        Name = "sqliteitemrepository-deletechapters-exact",
                        MethodName = "DeleteChapters",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                       BindingFlags.NonPublic,
                        ParameterTypes = new[] { typeof(long), typeof(MarkerType[]) }
                    },
                    logger,
                    "ChapterJsonSync.DeleteChapters");

                if (saveChapters == null || deleteChapters == null)
                {
                    PatchLog.InitFailed(logger, nameof(ChapterJsonSync), "目标方法缺失");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.database.chapterjsonpersist");

                if (isEnabled)
                {
                    Patch();
                }
            }
            catch (Exception e)
            {
                logger?.Error("ChapterJsonSync 初始化失败。");
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

        public static IDisposable SkipPersisting()
        {
            var previousValue = SkipPersist.Value;
            SkipPersist.Value = true;
            return new SkipPersistScope(previousValue);
        }

        public static IDisposable AllowClearing()
        {
            var previousValue = AllowClearPersistCount.Value;
            AllowClearPersistCount.Value = previousValue + 1;
            return new AllowClearPersistScope(previousValue);
        }

        private static void Patch()
        {
            if (isPatched || harmony == null)
            {
                return;
            }

            harmony.Patch(saveChapters,
                postfix: new HarmonyMethod(typeof(ChapterJsonSync), nameof(SaveChaptersPostfix)));
            harmony.Patch(deleteChapters,
                postfix: new HarmonyMethod(typeof(ChapterJsonSync), nameof(DeleteChaptersPostfix)));
            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null)
            {
                return;
            }

            harmony.Unpatch(saveChapters, HarmonyPatchType.Postfix, harmony.Id);
            harmony.Unpatch(deleteChapters, HarmonyPatchType.Postfix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPostfix]
        private static void SaveChaptersPostfix(long itemId, List<ChapterInfo> chapters, bool __runOriginal)
        {
            if (__runOriginal && chapters?.Count > 0)
            {
                PersistSavedChapters(itemId);
            }
        }

        [HarmonyPostfix]
        private static void DeleteChaptersPostfix(long itemId, bool __runOriginal)
        {
            if (__runOriginal && AllowClearPersistCount.Value > 0)
            {
                PersistSavedChapters(itemId);
            }
        }

        private static void PersistSavedChapters(long itemId)
        {
            if (SkipPersist.Value)
            {
                return;
            }

            var item = Plugin.LibraryManager?.GetItemById(itemId);
            if (item == null)
            {
                return;
            }

            try
            {
                Plugin.ChaptersStore?.OverWriteToFile(item);
            }
            catch (Exception ex)
            {
                logger?.Error($"章节信息已写入数据库，但同步 JSON 失败: {item.FileName ?? item.Path ?? item.InternalId.ToString()}");
                logger?.Error(ex.Message);
                logger?.Debug(ex.StackTrace);
            }
        }

        private sealed class SkipPersistScope : IDisposable
        {
            private readonly bool previousValue;

            public SkipPersistScope(bool previousValue)
            {
                this.previousValue = previousValue;
            }

            public void Dispose()
            {
                SkipPersist.Value = previousValue;
            }
        }

        private sealed class AllowClearPersistScope : IDisposable
        {
            private readonly int previousValue;

            public AllowClearPersistScope(int previousValue)
            {
                this.previousValue = previousValue;
            }

            public void Dispose()
            {
                AllowClearPersistCount.Value = previousValue;
            }
        }
    }
}
