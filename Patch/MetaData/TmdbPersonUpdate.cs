using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MediaInfoKeeper.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    public static class TmdbPersonUpdate
    {
        private static readonly object InitLock = new object();

        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo updatePeopleMethod;
        private static readonly object MovieDbAccessLock = new object();
        private static bool movieDbAccessResolved;
        private static bool movieDbAccessAvailable;
        private static PropertyInfo movieDbPersonProviderCurrentProperty;
        private static MethodInfo movieDbPersonProviderEnsurePersonInfoMethod;
        private static PropertyInfo movieDbPersonBiographyProperty;
        private static MethodInfo movieDbProviderBaseGetTmdbLanguagesMethod;
        private static MethodInfo movieDbProviderBaseGetMovieDbMetadataLanguagesMethod;
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

            lock (InitLock)
            {
                if (harmony != null)
                {
                    Configure(enable);
                    return;
                }

                var libraryManagerType = Plugin.LibraryManager?.GetType() ??
                                         Type.GetType("Emby.Server.Implementations.Library.LibraryManager, Emby.Server.Implementations");
                if (libraryManagerType == null)
                {
                    PatchLog.InitFailed(logger, nameof(TmdbPersonUpdate), "LibraryManager 类型缺失");
                    return;
                }

                var version = libraryManagerType.Assembly.GetName().Version;
                updatePeopleMethod = PatchMethodResolver.Resolve(
                    libraryManagerType,
                    version,
                    new MethodSignatureProfile
                    {
                        Name = "librarymanager-updatepeople-exact",
                        MethodName = "UpdatePeople",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        ParameterTypes = new[] { typeof(BaseItem), typeof(List<PersonInfo>), typeof(bool) },
                        ReturnType = typeof(void),
                        IsStatic = false
                    },
                    logger,
                    "TmdbPersonUpdate.LibraryManager.UpdatePeople");

                if (updatePeopleMethod == null)
                {
                    PatchLog.InitFailed(logger, nameof(TmdbPersonUpdate), "UpdatePeople 目标方法缺失");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.tmdbpersonupdate");
                if (isEnabled)
                {
                    Patch();
                }
            }
        }

        public static void Configure(bool enable)
        {
            isEnabled = enable;
            if (harmony == null)
            {
                return;
            }

            if (enable)
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
            if (isPatched || harmony == null || updatePeopleMethod == null)
            {
                return;
            }

            harmony.Patch(
                updatePeopleMethod,
                prefix: new HarmonyMethod(typeof(TmdbPersonUpdate), nameof(UpdatePeoplePrefix)));
            PatchLog.Patched(logger, nameof(TmdbPersonUpdate), updatePeopleMethod);
            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null || updatePeopleMethod == null)
            {
                return;
            }

            harmony.Unpatch(updatePeopleMethod, HarmonyPatchType.Prefix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPrefix]
        private static void UpdatePeoplePrefix(BaseItem item, List<PersonInfo> people)
        {
            if (!isEnabled || item == null || people == null || people.Count == 0)
            {
                return;
            }

            try
            {
                SyncTmdbNames(item, people);
            }
            catch (Exception ex)
            {
                logger?.Error("TmdbPersonUpdate prefix 异常: {0}", ex);
            }
        }

        private static void SyncTmdbNames(BaseItem item, List<PersonInfo> people)
        {
            var libraryManager = Plugin.LibraryManager;
            if (libraryManager == null)
            {
                return;
            }

            var itemLabel = FormatItemLabel(item);

            foreach (var person in people)
            {
                var tmdbPersonId = person?.GetProviderId(MetadataProviders.Tmdb);
                var newName = person?.Name?.Trim();
                if (string.IsNullOrWhiteSpace(tmdbPersonId) || string.IsNullOrWhiteSpace(newName))
                {
                    continue;
                }

                if (!long.TryParse(tmdbPersonId, out _))
                {
                    continue;
                }

                var existingPeople = libraryManager.GetPeopleItems(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { typeof(Person).Name },
                    Recursive = true,
                    AnyProviderIdEquals = new ProviderIdDictionary
                    {
                        [MetadataProviders.Tmdb.ToString()] = tmdbPersonId
                    }
                });

                var existingPerson = existingPeople?.Items?.OfType<Person>().FirstOrDefault();
                if (existingPerson == null)
                {
                    continue;
                }

                var currentName = existingPerson.Name?.Trim();
                if (string.Equals(currentName, newName, StringComparison.Ordinal))
                {
                    continue;
                }

                existingPerson.Name = newName;
                existingPerson.UpdateToRepository(ItemUpdateType.MetadataImport);
                logger?.Info(
                    "TMDB演员名称已更新 {0}: {1} -> {2} tmdbPersonId={3}",
                    itemLabel,
                    string.IsNullOrWhiteSpace(currentName) ? "空" : currentName,
                    newName,
                    tmdbPersonId);

                UpdatePersonOverview(existingPerson, tmdbPersonId, itemLabel);
            }
        }

        private static void UpdatePersonOverview(Person existingPerson, string tmdbPersonId, string itemLabel)
        {
            if (existingPerson == null || string.IsNullOrWhiteSpace(tmdbPersonId))
            {
                return;
            }

            if (!TryResolveMovieDbPersonAccess())
            {
                return;
            }

            try
            {
                var provider = movieDbPersonProviderCurrentProperty?.GetValue(null);
                if (provider == null)
                {
                    return;
                }

                var directoryService = new DirectoryService(logger, Plugin.FileSystem);
                var preferredLanguage = GetPreferredTmdbLanguage(provider, existingPerson);
                var task = movieDbPersonProviderEnsurePersonInfoMethod?.Invoke(
                    provider,
                    new object[] { tmdbPersonId, preferredLanguage, directoryService, CancellationToken.None });
                if (task == null)
                {
                    return;
                }

                var resultProperty = task.GetType().GetProperty("Result");
                var personResult = resultProperty?.GetValue(task);
                var biography = movieDbPersonBiographyProperty?.GetValue(personResult) as string;
                var newOverview = string.IsNullOrWhiteSpace(biography) ? null : biography.Trim();
                var currentOverview = string.IsNullOrWhiteSpace(existingPerson.Overview) ? null : existingPerson.Overview.Trim();
                if (string.IsNullOrWhiteSpace(newOverview) ||
                    string.Equals(currentOverview, newOverview, StringComparison.Ordinal))
                {
                    return;
                }

                existingPerson.Overview = newOverview;
                existingPerson.UpdateToRepository(ItemUpdateType.MetadataImport);
                logger?.Info(
                    "TMDB演员简介已更新 {0}: {1} tmdbPersonId={2}",
                    itemLabel,
                    existingPerson.Name ?? "空",
                    tmdbPersonId);
            }
            catch (Exception ex)
            {
                logger?.Error("TmdbPersonUpdate 更新人物简介异常: {0}", ex);
            }
        }

        private static string GetPreferredTmdbLanguage(object provider, BaseItem item)
        {
            if (provider == null || item == null)
            {
                return null;
            }

            try
            {
                var languageTask = movieDbProviderBaseGetTmdbLanguagesMethod?.Invoke(
                    provider,
                    new object[] { CancellationToken.None });
                var providerLanguages = languageTask?.GetType().GetProperty("Result")?.GetValue(languageTask) as string[];
                if (providerLanguages == null || providerLanguages.Length == 0)
                {
                    return null;
                }

                var lookupInfo = new ItemLookupInfo
                {
                    MetadataLanguage = item.GetPreferredMetadataLanguage(),
                    MetadataCountryCode = item.GetPreferredMetadataCountryCode()
                };

                var metadataLanguages = movieDbProviderBaseGetMovieDbMetadataLanguagesMethod?.Invoke(
                    provider,
                    new object[] { lookupInfo, providerLanguages }) as string[];
                return metadataLanguages?.FirstOrDefault(language => !string.IsNullOrWhiteSpace(language));
            }
            catch (Exception ex)
            {
                logger?.Error("TmdbPersonUpdate 获取TMDB语言异常: {0}", ex);
                return null;
            }
        }

        private static bool TryResolveMovieDbPersonAccess()
        {
            if (movieDbAccessResolved)
            {
                return movieDbAccessAvailable;
            }

            lock (MovieDbAccessLock)
            {
                if (movieDbAccessResolved)
                {
                    return movieDbAccessAvailable;
                }

                var movieDbAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "MovieDb", StringComparison.OrdinalIgnoreCase));
                var movieDbPersonProviderType = movieDbAssembly?.GetType("MovieDb.MovieDbPersonProvider", throwOnError: false);
                var personResultType = movieDbPersonProviderType?.GetNestedType("PersonResult", BindingFlags.Public | BindingFlags.NonPublic);

                movieDbPersonProviderCurrentProperty = movieDbPersonProviderType?.GetProperty(
                    "Current",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                movieDbPersonProviderEnsurePersonInfoMethod = movieDbPersonProviderType?.GetMethod(
                    "EnsurePersonInfo",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: new[] { typeof(string), typeof(string), typeof(IDirectoryService), typeof(CancellationToken) },
                    modifiers: null);
                movieDbPersonBiographyProperty = personResultType?.GetProperty(
                    "biography",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                movieDbProviderBaseGetTmdbLanguagesMethod = movieDbPersonProviderType?.GetMethod(
                    "GetTmdbLanguages",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: new[] { typeof(CancellationToken) },
                    modifiers: null);
                movieDbProviderBaseGetMovieDbMetadataLanguagesMethod = movieDbPersonProviderType?.GetMethod(
                    "GetMovieDbMetadataLanguages",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: new[] { typeof(ItemLookupInfo), typeof(string[]) },
                    modifiers: null);

                movieDbAccessAvailable =
                    movieDbPersonProviderCurrentProperty != null &&
                    movieDbPersonProviderEnsurePersonInfoMethod != null &&
                    movieDbPersonBiographyProperty != null &&
                    movieDbProviderBaseGetTmdbLanguagesMethod != null &&
                    movieDbProviderBaseGetMovieDbMetadataLanguagesMethod != null;
                movieDbAccessResolved = true;
            }

            return movieDbAccessAvailable;
        }

        private static string FormatItemLabel(BaseItem item)
        {
            if (item == null)
            {
                return "未知条目";
            }
            return item.FileNameWithoutExtension;
        }
    }
}
