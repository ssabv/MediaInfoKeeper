using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 在远程图片统一入口临时改用作品原语言作为图片语言。
    /// </summary>
    public static class OriginalPoster
    {
        private static readonly object InitLock = new object();

        private static Harmony harmony;
        private static ILogger logger;
        private static bool isEnabled;
        private static bool providerHookInstalled;
        private static bool movieDbResolved;

        private static MethodInfo providerGetAvailableRemoteImages;
        private static MethodInfo providerGetAvailableRemoteImagesAsync;
        private static PropertyInfo movieDbProviderCurrent;
        private static PropertyInfo movieDbSeriesProviderCurrent;
        private static MethodInfo movieDbEnsureMovieInfo;
        private static MethodInfo movieDbEnsureSeriesInfo;

        public static bool IsReady => providerHookInstalled;

        public static bool IsWaiting => false;

        public static void Initialize(ILogger pluginLogger, bool enable)
        {
            logger = pluginLogger;
            isEnabled = enable;

            lock (InitLock)
            {
                harmony ??= new Harmony("mediainfokeeper.preferoriginalposter");

                if (!providerHookInstalled)
                {
                    InstallProviderHooks();
                }
            }
        }

        public static void Configure(bool enable)
        {
            isEnabled = enable;
        }

        private static void InstallProviderHooks()
        {
            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var version = embyProviders.GetName().Version;
                var providerManager = embyProviders.GetType("Emby.Providers.Manager.ProviderManager", false);
                if (providerManager == null)
                {
                    PatchLog.InitFailed(logger, nameof(OriginalPoster), "ProviderManager 未找到");
                    return;
                }

                providerGetAvailableRemoteImages = PatchMethodResolver.Resolve(
                    providerManager,
                    version,
                    new MethodSignatureProfile
                    {
                        Name = "providermanager-getavailableremoteimages-sync",
                        MethodName = "GetAvailableRemoteImages",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        IsStatic = false,
                        ParameterTypes = new[]
                        {
                            typeof(BaseItem),
                            typeof(LibraryOptions),
                            typeof(RemoteImageQuery),
                            typeof(CancellationToken)
                        },
                        ReturnType = typeof(System.Threading.Tasks.Task<IEnumerable<RemoteImageInfo>>)
                    },
                    logger,
                    "OriginalPoster.ProviderManager.GetAvailableRemoteImages(sync)");

                providerGetAvailableRemoteImagesAsync = PatchMethodResolver.Resolve(
                    providerManager,
                    version,
                    new MethodSignatureProfile
                    {
                        Name = "providermanager-getavailableremoteimages-async",
                        MethodName = "GetAvailableRemoteImages",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        IsStatic = false,
                        ParameterTypes = new[]
                        {
                            typeof(BaseItem),
                            typeof(LibraryOptions),
                            typeof(RemoteImageQuery),
                            typeof(IDirectoryService),
                            typeof(CancellationToken)
                        },
                        ReturnType = typeof(System.Threading.Tasks.Task<IEnumerable<RemoteImageInfo>>)
                    },
                    logger,
                    "OriginalPoster.ProviderManager.GetAvailableRemoteImages(async)");

                var patched = 0;
                patched += PatchMethod(providerGetAvailableRemoteImages, nameof(GetAvailableRemoteImagesPrefix));
                patched += PatchMethod(providerGetAvailableRemoteImagesAsync, nameof(GetAvailableRemoteImagesPrefix));

                providerHookInstalled = patched > 0;
                if (!providerHookInstalled)
                {
                    PatchLog.InitFailed(logger, nameof(OriginalPoster), "Provider hooks 安装失败");
                }

                ResolveMovieDbMembers(logFailure: false);
            }
            catch (Exception ex)
            {
                PatchLog.InitFailed(logger, nameof(OriginalPoster), ex.Message);
                logger?.Error("OriginalPoster provider hooks failed: {0}", ex);
            }
        }

        private static int PatchMethod(MethodInfo method, string prefix)
        {
            if (method == null || harmony == null)
            {
                return 0;
            }

            var prefixMethod = new HarmonyMethod(typeof(OriginalPoster).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic));
            harmony.Patch(method, prefix: prefixMethod);
            PatchLog.Patched(logger, nameof(OriginalPoster), method);
            return 1;
        }

        private static void ResolveMovieDbMembers(bool logFailure)
        {
            if (movieDbResolved)
            {
                return;
            }

            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "MovieDb", StringComparison.OrdinalIgnoreCase));
            if (assembly == null)
            {
                return;
            }

            var version = assembly.GetName().Version;
            var movieDbProvider = assembly.GetType("MovieDb.MovieDbProvider", false);
            var movieDbSeriesProvider = assembly.GetType("MovieDb.MovieDbSeriesProvider", false);

            movieDbProviderCurrent = movieDbProvider?.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            movieDbSeriesProviderCurrent = movieDbSeriesProvider?.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            movieDbEnsureMovieInfo = PatchMethodResolver.Resolve(
                movieDbProvider,
                version,
                new MethodSignatureProfile
                {
                    Name = "moviedbprovider-ensuremovieinfo-exact",
                    MethodName = "EnsureMovieInfo",
                    BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                    IsStatic = false,
                    ParameterTypes = new[]
                    {
                        typeof(string),
                        typeof(string),
                        typeof(CancellationToken)
                    }
                },
                logger,
                "OriginalPoster.MovieDbProvider.EnsureMovieInfo");

            movieDbEnsureSeriesInfo = PatchMethodResolver.Resolve(
                movieDbSeriesProvider,
                version,
                new MethodSignatureProfile
                {
                    Name = "moviedbseriesprovider-ensureseriesinfo-exact",
                    MethodName = "EnsureSeriesInfo",
                    BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                    IsStatic = false,
                    ParameterTypes = new[]
                    {
                        typeof(string),
                        typeof(string),
                        typeof(CancellationToken)
                    }
                },
                logger,
                "OriginalPoster.MovieDbSeriesProvider.EnsureSeriesInfo");

            movieDbResolved = movieDbProviderCurrent != null &&
                              movieDbSeriesProviderCurrent != null &&
                              movieDbEnsureMovieInfo != null &&
                              movieDbEnsureSeriesInfo != null;
            if (!movieDbResolved && logFailure)
            {
                PatchLog.InitFailed(logger, nameof(OriginalPoster), "MovieDb 原语言入口解析失败");
            }
        }

        [HarmonyPrefix]
        private static void GetAvailableRemoteImagesPrefix(BaseItem item, ref LibraryOptions libraryOptions, ref RemoteImageQuery query, CancellationToken cancellationToken)
        {
            if (!isEnabled || item == null || libraryOptions == null || query == null)
            {
                return;
            }

            var originalLanguage = GetOriginalLanguage(item, cancellationToken);
            if (string.IsNullOrWhiteSpace(originalLanguage))
            {
                return;
            }

            query.IncludeAllLanguages = false;
            libraryOptions = CopyLibraryOptions(libraryOptions);
            libraryOptions.PreferredImageLanguage = originalLanguage;
            logger?.Debug("OriginalPoster image language: item={0}, originalLanguage={1}", GetItemLabel(item), originalLanguage);
        }

        private static string GetOriginalLanguage(BaseItem item, CancellationToken cancellationToken)
        {
            var lookupItem = GetTmdbLookupItem(item);
            var tmdbId = lookupItem?.GetProviderId(MetadataProviders.Tmdb)?.Trim();
            if (string.IsNullOrWhiteSpace(tmdbId))
            {
                return null;
            }

            var mediaType = GetTmdbMediaType(item);
            if (mediaType == null)
            {
                return null;
            }

            return GetMovieDbOriginalLanguage(mediaType, tmdbId, cancellationToken);
        }

        private static BaseItem GetTmdbLookupItem(BaseItem item)
        {
            long seriesId;
            if (item is Season season)
            {
                seriesId = season.SeriesId != 0 ? season.SeriesId : season.FindSeriesId();
            }
            else if (item is Episode episode)
            {
                seriesId = episode.SeriesId != 0 ? episode.SeriesId : episode.FindSeriesId();
            }
            else
            {
                return item;
            }

            return seriesId == 0
                ? null
                : Plugin.LibraryManager?.GetItemById(seriesId) as Series;
        }

        private static string GetTmdbMediaType(BaseItem item)
        {
            if (item is Movie)
            {
                return "movie";
            }

            if (item is Series || item is Season || item is Episode)
            {
                return "tv";
            }

            return null;
        }

        private static string GetMovieDbOriginalLanguage(string mediaType, string tmdbId, CancellationToken cancellationToken)
        {
            ResolveMovieDbMembers(logFailure: true);
            if (!movieDbResolved)
            {
                return null;
            }

            try
            {
                object provider;
                MethodInfo ensureMethod;
                if (string.Equals(mediaType, "movie", StringComparison.OrdinalIgnoreCase))
                {
                    provider = movieDbProviderCurrent.GetValue(null);
                    ensureMethod = movieDbEnsureMovieInfo;
                }
                else
                {
                    provider = movieDbSeriesProviderCurrent.GetValue(null);
                    ensureMethod = movieDbEnsureSeriesInfo;
                }

                var task = ensureMethod.Invoke(provider, new object[] { tmdbId, null, cancellationToken }) as Task;
                task?.GetAwaiter().GetResult();
                return NormalizeLanguage(GetOriginalLanguageFromTaskResult(mediaType, task));
            }
            catch (Exception ex)
            {
                logger?.Debug("OriginalPoster ensure MovieDb original language failed: {0}", ex.Message);
                return null;
            }
        }

        private static string GetOriginalLanguageFromTaskResult(string mediaType, Task task)
        {
            if (task == null)
            {
                return null;
            }

            var resultProperty = task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public);
            var result = resultProperty?.GetValue(task);
            if (result == null)
            {
                return null;
            }

            if (string.Equals(mediaType, "movie", StringComparison.OrdinalIgnoreCase))
            {
                return GetStringProperty(result, "original_language");
            }

            if (string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase))
            {
                return GetFirstString(result.GetType().GetProperty("languages", BindingFlags.Instance | BindingFlags.Public)?.GetValue(result)) ??
                       GetStringProperty(result, "original_language");
            }

            return null;
        }

        private static string GetStringProperty(object source, string propertyName)
        {
            return source?.GetType()
                .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)
                ?.GetValue(source)
                ?.ToString();
        }

        private static string GetFirstString(object source)
        {
            if (!(source is System.Collections.IEnumerable values))
            {
                return null;
            }

            foreach (var value in values)
            {
                var text = value?.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            return null;
        }

        private static LibraryOptions CopyLibraryOptions(LibraryOptions source)
        {
            var copy = new LibraryOptions();
            foreach (var property in typeof(LibraryOptions).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead || !property.CanWrite)
                {
                    continue;
                }

                property.SetValue(copy, property.GetValue(source));
            }

            return copy;
        }

        private static string NormalizeLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return null;
            }

            var value = language.Trim();
            var dashIndex = value.IndexOf('-');
            if (dashIndex > 0)
            {
                value = value.Substring(0, dashIndex);
            }

            value = value.ToLowerInvariant();
            return string.Equals(value, "cn", StringComparison.OrdinalIgnoreCase)
                ? "zh"
                : value;
        }

        private static string GetItemLabel(BaseItem item)
        {
            return item?.Name ?? item?.FileName ?? item?.Path ?? item?.InternalId.ToString() ?? "<null>";
        }

    }
}
