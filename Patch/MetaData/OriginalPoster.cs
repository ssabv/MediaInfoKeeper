using System;
using System.Collections;
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

namespace MediaInfoKeeper.Patch {
    /// <summary>
    ///     让作品原语言的图片排在远程图片列表最前，优先被 Emby 采用。
    ///     分两步，缺一不可：
    ///     1. prefix 把 query.IncludeAllLanguages 置为 true。Emby 只在 !IncludeAllLanguages 时按
    ///        「首选图片语言 + 无语言」过滤结果（ProviderManager.GetAvailableRemoteImages），
    ///        原语言图片会因不在首选语言里被丢掉，postfix 就没东西可排。
    ///     2. postfix 把 Language 等于原语言的图片稳定排到最前，其余保持 Emby 原有顺序。
    ///     之所以不再走「替换 PreferredImageLanguage」，是因为 MovieDb 的图片 Provider 显式丢弃了
    ///     LibraryOptions（`_ = options.LibraryOptions;`），TMDB 请求根本不会带上这个语言，
    ///     它只影响 Emby 的本地过滤；一旦该作品在 TMDB 既无该语言图、又无无语言图，结果就是空列表
    ///     （原语言解析成功却搜不出图），同时无条件 IncludeAllLanguages = false 还会掐掉
    ///     手动「所有语言」搜索。现在这两条副作用都不存在了。
    /// </summary>
    public static class OriginalPoster {
        /// <summary>
        ///     剧集出口国家 → 原语言。仅用于剧集（其 DTO 没有 original_language），命中不了再退化到 languages。
        /// </summary>
        private static readonly Dictionary<string, string> OriginCountryLanguages =
            new(StringComparer.OrdinalIgnoreCase) {
                ["JP"] = "ja",
                ["CN"] = "zh", ["TW"] = "zh", ["HK"] = "zh", ["MO"] = "zh", ["SG"] = "zh",
                ["KR"] = "ko", ["KP"] = "ko",
                ["US"] = "en", ["GB"] = "en", ["CA"] = "en", ["AU"] = "en", ["NZ"] = "en", ["IE"] = "en",
                ["FR"] = "fr", ["DE"] = "de", ["AT"] = "de", ["IT"] = "it",
                ["ES"] = "es", ["MX"] = "es", ["AR"] = "es", ["CL"] = "es", ["CO"] = "es",
                ["BR"] = "pt", ["PT"] = "pt", ["RU"] = "ru", ["UA"] = "uk", ["PL"] = "pl",
                ["NL"] = "nl", ["SE"] = "sv", ["NO"] = "no", ["DK"] = "da", ["FI"] = "fi",
                ["TH"] = "th", ["VN"] = "vi", ["IN"] = "hi", ["ID"] = "id", ["PH"] = "tl",
                ["MY"] = "ms", ["TR"] = "tr", ["SA"] = "ar", ["EG"] = "ar", ["IL"] = "he",
                ["GR"] = "el", ["CZ"] = "cs", ["HU"] = "hu", ["RO"] = "ro"
            };

        private static readonly object InitLock = new();

        private static Harmony harmony;
        private static ILogger logger;
        private static bool isEnabled;
        private static bool movieDbResolved;

        private static MethodInfo providerGetAvailableRemoteImages;
        private static MethodInfo providerGetAvailableRemoteImagesAsync;
        private static PropertyInfo movieDbProviderCurrent;
        private static PropertyInfo movieDbSeriesProviderCurrent;
        private static MethodInfo movieDbEnsureMovieInfo;
        private static MethodInfo movieDbEnsureSeriesInfo;

        public static bool IsReady { get; private set; }

        public static bool IsWaiting => false;

        public static void Initialize(ILogger pluginLogger, bool enable) {
            logger = pluginLogger;
            isEnabled = enable;

            lock (InitLock) {
                harmony ??= new Harmony("mediainfokeeper.preferoriginalposter");

                if (!IsReady) InstallProviderHooks();
            }
        }

        public static void Configure(bool enable) {
            isEnabled = enable;
        }

        private static void InstallProviderHooks() {
            try {
                var embyProviders = Assembly.Load("Emby.Providers");
                var version = embyProviders.GetName().Version;
                var providerManager = embyProviders.GetType("Emby.Providers.Manager.ProviderManager", false);
                if (providerManager == null) {
                    PatchLog.InitFailed(logger, nameof(OriginalPoster), "ProviderManager 未找到");
                    return;
                }

                providerGetAvailableRemoteImages = PatchMethodResolver.Resolve(
                    providerManager,
                    version,
                    new MethodSignatureProfile {
                        Name = "providermanager-getavailableremoteimages-sync",
                        MethodName = "GetAvailableRemoteImages",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        IsStatic = false,
                        ParameterTypes = new[] {
                            typeof(BaseItem),
                            typeof(LibraryOptions),
                            typeof(RemoteImageQuery),
                            typeof(CancellationToken)
                        },
                        ReturnType = typeof(Task<IEnumerable<RemoteImageInfo>>)
                    },
                    logger,
                    "OriginalPoster.ProviderManager.GetAvailableRemoteImages(sync)");

                providerGetAvailableRemoteImagesAsync = PatchMethodResolver.Resolve(
                    providerManager,
                    version,
                    new MethodSignatureProfile {
                        Name = "providermanager-getavailableremoteimages-async",
                        MethodName = "GetAvailableRemoteImages",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        IsStatic = false,
                        ParameterTypes = new[] {
                            typeof(BaseItem),
                            typeof(LibraryOptions),
                            typeof(RemoteImageQuery),
                            typeof(IDirectoryService),
                            typeof(CancellationToken)
                        },
                        ReturnType = typeof(Task<IEnumerable<RemoteImageInfo>>)
                    },
                    logger,
                    "OriginalPoster.ProviderManager.GetAvailableRemoteImages(async)");

                var patched = 0;
                patched += PatchMethod(providerGetAvailableRemoteImages);
                patched += PatchMethod(providerGetAvailableRemoteImagesAsync);

                IsReady = patched > 0;
                if (!IsReady) PatchLog.InitFailed(logger, nameof(OriginalPoster), "Provider hooks 安装失败");

                ResolveMovieDbMembers(false);
            }
            catch (Exception ex) {
                PatchLog.InitFailed(logger, nameof(OriginalPoster), ex.Message);
                logger?.Error("OriginalPoster provider hooks failed: {0}", ex);
            }
        }

        private static int PatchMethod(MethodInfo method) {
            if (method == null || harmony == null) return 0;

            harmony.Patch(
                method,
                new HarmonyMethod(
                    typeof(OriginalPoster).GetMethod(nameof(GetAvailableRemoteImagesPrefix),
                        BindingFlags.Static | BindingFlags.NonPublic)),
                new HarmonyMethod(
                    typeof(OriginalPoster).GetMethod(nameof(GetAvailableRemoteImagesPostfix),
                        BindingFlags.Static | BindingFlags.NonPublic)));
            PatchLog.Patched(logger, nameof(OriginalPoster), method);
            return 1;
        }

        private static void ResolveMovieDbMembers(bool logFailure) {
            if (movieDbResolved) return;

            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "MovieDb", StringComparison.OrdinalIgnoreCase));
            if (assembly == null) return;

            var version = assembly.GetName().Version;
            var movieDbProvider = assembly.GetType("MovieDb.MovieDbProvider", false);
            var movieDbSeriesProvider = assembly.GetType("MovieDb.MovieDbSeriesProvider", false);

            movieDbProviderCurrent = movieDbProvider?.GetProperty("Current",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            movieDbSeriesProviderCurrent = movieDbSeriesProvider?.GetProperty("Current",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            movieDbEnsureMovieInfo = PatchMethodResolver.Resolve(
                movieDbProvider,
                version,
                new MethodSignatureProfile {
                    Name = "moviedbprovider-ensuremovieinfo-exact",
                    MethodName = "EnsureMovieInfo",
                    BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                    IsStatic = false,
                    ParameterTypes = new[] {
                        typeof(string),
                        typeof(bool),
                        typeof(string),
                        typeof(CancellationToken)
                    }
                },
                logger,
                "OriginalPoster.MovieDbProvider.EnsureMovieInfo");

            movieDbEnsureSeriesInfo = PatchMethodResolver.Resolve(
                movieDbSeriesProvider,
                version,
                new MethodSignatureProfile {
                    Name = "moviedbseriesprovider-ensureseriesinfo-exact",
                    MethodName = "EnsureSeriesInfo",
                    BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                    IsStatic = false,
                    ParameterTypes = new[] {
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
            if (!movieDbResolved && logFailure) PatchLog.InitFailed(logger, nameof(OriginalPoster), "MovieDb 原语言入口解析失败");
        }

        /// <summary>
        ///     让 Emby 不要按语言把远程图片列表收窄。
        ///     Emby 仅在 !IncludeAllLanguages 时按「首选图片语言 + 无语言」过滤（见 ProviderManager.GetAvailableRemoteImages），
        ///     作品原语言不在首选语言里时会被直接丢掉，postfix 就没有东西可排。
        ///     这个字段在整个 Emby.Providers 里只被那一处过滤读取，置 true 的副作用仅限于「不去掉候选」；
        ///     它不影响发往 TMDB 的请求（图片 Provider 不传语言），也不影响实际下载张数
        ///     （自动刮削每个单图类型只下载列表里的第一张，Backdrop 受 backdropLimit 限制）。
        /// </summary>
        private static void GetAvailableRemoteImagesPrefix([HarmonyArgument(0)] BaseItem item,
            ref RemoteImageQuery query) {
            if (!isEnabled || item == null || query == null) return;
            if (query.IncludeAllLanguages) return;
            if (GetTmdbMediaType(item) == null) return;

            query.IncludeAllLanguages = true;
        }

        private static void GetAvailableRemoteImagesPostfix([HarmonyArgument(0)] BaseItem item,
            ref Task<IEnumerable<RemoteImageInfo>> __result) {
            if (!isEnabled || __result == null || item == null) return;

            __result = PreferOriginalLanguageAsync(__result, item);
        }

        private static async Task<IEnumerable<RemoteImageInfo>> PreferOriginalLanguageAsync(
            Task<IEnumerable<RemoteImageInfo>> task, BaseItem item) {
            var images = await task.ConfigureAwait(false);
            if (images == null) return null;

            var list = images as IList<RemoteImageInfo> ?? images.ToList();
            if (list.Count < 2) return list;

            var originalLanguage = GetOriginalLanguage(item, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(originalLanguage)) return list;

            var matched = list.Count(image => IsLanguageMatch(image, originalLanguage));
            if (matched == 0 || matched == list.Count) return list;

            var ordered = new List<RemoteImageInfo>(list.Count);
            ordered.AddRange(list.Where(image => IsLanguageMatch(image, originalLanguage)));
            ordered.AddRange(list.Where(image => !IsLanguageMatch(image, originalLanguage)));

            logger?.Debug("OriginalPoster 重排：item={0}，原语言={1}，命中={2}，总数={3}",
                GetItemLabel(item), originalLanguage, matched, list.Count);
            return ordered;
        }

        private static bool IsLanguageMatch(RemoteImageInfo image, string language) {
            return string.Equals(image?.Language, language, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetOriginalLanguage(BaseItem item, CancellationToken cancellationToken) {
            var lookupItem = GetTmdbLookupItem(item);
            var tmdbId = lookupItem?.GetProviderId(MetadataProviders.Tmdb)?.Trim();
            if (string.IsNullOrWhiteSpace(tmdbId)) return null;

            var mediaType = GetTmdbMediaType(item);
            if (mediaType == null) return null;

            return GetMovieDbOriginalLanguage(mediaType, tmdbId, cancellationToken);
        }

        private static BaseItem GetTmdbLookupItem(BaseItem item) {
            long seriesId;
            if (item is Season season)
                seriesId = season.SeriesId != 0 ? season.SeriesId : season.FindSeriesId();
            else if (item is Episode episode)
                seriesId = episode.SeriesId != 0 ? episode.SeriesId : episode.FindSeriesId();
            else
                return item;

            return seriesId == 0
                ? null
                : Plugin.LibraryManager?.GetItemById(seriesId) as Series;
        }

        private static string GetTmdbMediaType(BaseItem item) {
            if (item is Movie) return "movie";

            if (item is Series || item is Season || item is Episode) return "tv";

            return null;
        }

        private static string GetMovieDbOriginalLanguage(string mediaType, string tmdbId,
            CancellationToken cancellationToken) {
            ResolveMovieDbMembers(true);
            if (!movieDbResolved) return null;

            try {
                object provider;
                MethodInfo ensureMethod;
                if (string.Equals(mediaType, "movie", StringComparison.OrdinalIgnoreCase)) {
                    provider = movieDbProviderCurrent.GetValue(null);
                    ensureMethod = movieDbEnsureMovieInfo;
                }
                else {
                    provider = movieDbSeriesProviderCurrent.GetValue(null);
                    ensureMethod = movieDbEnsureSeriesInfo;
                }

                var arguments = string.Equals(mediaType, "movie", StringComparison.OrdinalIgnoreCase)
                    ? new object[] { tmdbId, true, null, cancellationToken }
                    : new object[] { tmdbId, null, cancellationToken };
                var task = ensureMethod.Invoke(provider, arguments) as Task;
                task?.GetAwaiter().GetResult();
                return NormalizeLanguage(GetOriginalLanguageFromTaskResult(mediaType, task));
            }
            catch (Exception ex) {
                logger?.Debug("OriginalPoster ensure MovieDb original language failed: {0}", ex.Message);
                return null;
            }
        }

        private static string GetOriginalLanguageFromTaskResult(string mediaType, Task task) {
            if (task == null) return null;

            var resultProperty = task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public);
            var result = resultProperty?.GetValue(task);
            if (result == null) return null;

            if (string.Equals(mediaType, "movie", StringComparison.OrdinalIgnoreCase))
                return GetStringProperty(result, "original_language");

            if (string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase))
                return GetSeriesOriginalLanguage(result);

            return null;
        }

        /// <summary>
        ///     推断剧集的原语言。
        ///     剧集的 DTO（SeriesRootObject）**没有 original_language 属性**（只有电影 DTO 有），
        ///     所以不能像电影那样直接读权威字段，只能按 origin_country 映射，再退化到 languages。
        ///     注意不能直接用 languages[0]：它是 TMDB 的 spoken/available 语言列表，顺序不可靠。
        ///     例：tv/278043「正反対な君と僕」original_language = ja，但 languages = ['en','ja']、
        ///     spoken_languages 里 en 也在前，取 languages[0] 会把英文当成原语言。
        /// </summary>
        private static string GetSeriesOriginalLanguage(object result) {
            var byCountry = MapOriginCountryToLanguage(result);
            if (!string.IsNullOrWhiteSpace(byCountry)) return byCountry;

            var languages = GetStrings(result.GetType()
                .GetProperty("languages", BindingFlags.Instance | BindingFlags.Public)?.GetValue(result));
            if (languages.Count == 0) return null;

            foreach (var language in languages)
                if (!string.Equals(language, "en", StringComparison.OrdinalIgnoreCase))
                    return language;

            return languages[0];
        }

        private static string MapOriginCountryToLanguage(object result) {
            var countries = GetStrings(result.GetType()
                .GetProperty("origin_country", BindingFlags.Instance | BindingFlags.Public)?.GetValue(result));

            foreach (var country in countries) {
                var code = country?.Trim().ToUpperInvariant();
                if (!string.IsNullOrEmpty(code) && OriginCountryLanguages.TryGetValue(code, out var language))
                    return language;
            }

            return null;
        }

        private static string GetStringProperty(object source, string propertyName) {
            return source?.GetType()
                .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)
                ?.GetValue(source)
                ?.ToString();
        }

        private static List<string> GetStrings(object source) {
            var result = new List<string>();
            if (!(source is IEnumerable values)) return result;

            foreach (var value in values) {
                var text = value?.ToString();
                if (!string.IsNullOrWhiteSpace(text)) result.Add(text);
            }

            return result;
        }

        private static string NormalizeLanguage(string language) {
            if (string.IsNullOrWhiteSpace(language)) return null;

            var value = language.Trim();
            var dashIndex = value.IndexOf('-');
            if (dashIndex > 0) value = value.Substring(0, dashIndex);

            value = value.ToLowerInvariant();
            return string.Equals(value, "cn", StringComparison.OrdinalIgnoreCase)
                ? "zh"
                : value;
        }

        private static string GetItemLabel(BaseItem item) {
            return item?.Name ?? item?.FileName ?? item?.Path ?? item?.InternalId.ToString() ?? "<null>";
        }
    }
}
