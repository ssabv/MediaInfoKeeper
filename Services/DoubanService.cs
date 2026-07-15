using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaInfoKeeper.Common;
using MediaInfoKeeper.External;

namespace MediaInfoKeeper.Services {
    internal static class DoubanService {
        private const string DoubanCelebrityCacheScope = "douban-celebrities";
        private const string DoubanImdbSubjectCacheScope = "douban-imdb-subjects";
        private const string DoubanSearchSubjectCacheScope = "douban-search-subjects";

        private static readonly Regex MixedChineseEnglishRoleRegex =
            new(@"^(.+?[\u4E00-\u9FFF][^A-Za-z]*?)\s+[A-Za-z0-9].*$", RegexOptions.Compiled);

        private static readonly Regex DoubanSubjectIdRegex = new(@"(?<!\d)(\d{5,})(?!\d)", RegexOptions.Compiled);
        private static readonly TimeSpan DoubanSuccessCacheDuration = TimeSpan.FromHours(6);

        private static readonly JsonSerializerOptions JsonOptions = new() {
            PropertyNameCaseInsensitive = true
        };

        public static bool EnhancePeopleRole(BaseItem item, List<PersonInfo> people) {
            if (item == null || people == null || people.Count == 0) return false;

            var doubanCelebrities = GetDoubanCelebrities(item);
            if (doubanCelebrities == null || doubanCelebrities.Count == 0) return false;

            var changed = false;
            foreach (var person in people) {
                if (!ShouldHandlePerson(person)) continue;

                if (LanguageUtility.IsChinese(person.Role)) continue;

                var doubanCelebrity = MatchDoubanCelebrity(doubanCelebrities, person.Name);
                var chineseRole = CleanDoubanCharacter(doubanCelebrity?.character);
                if (!LanguageUtility.IsChinese(chineseRole)) continue;

                if (!string.Equals(person.Role, chineseRole, StringComparison.Ordinal)) {
                    person.Role = chineseRole;
                    changed = true;
                }
            }

            return changed;
        }

        public static DoubanMetadataPayload ResolveDoubanMetadata(ItemLookupInfo lookupInfo) {
            if (lookupInfo == null) return null;

            var context = BuildLookupContext(lookupInfo);
            if (context == null || string.IsNullOrWhiteSpace(context.DoubanSubjectId)) return null;

            return new DoubanMetadataPayload {
                DoubanSubjectId = context.DoubanSubjectId
            };
        }

        private static bool ShouldHandlePerson(PersonInfo person) {
            return person != null &&
                   (person.Type == PersonType.Actor || person.Type == PersonType.GuestStar);
        }

        private static List<DoubanCelebrity> GetDoubanCelebrities(BaseItem item) {
            var context = BuildMediaLookupContext(item);
            return GetDoubanCelebrities(context);
        }

        private static List<DoubanCelebrity> GetDoubanCelebrities(MediaLookupContext context) {
            if (context == null || string.IsNullOrWhiteSpace(context.DoubanSubjectId)) return null;

            var diskCachedCelebrities = PluginDiskCache.GetJson<List<DoubanCelebrity>>(
                DoubanCelebrityCacheScope,
                context.CacheKey,
                DoubanSuccessCacheDuration,
                JsonOptions);
            if (diskCachedCelebrities != null) return diskCachedCelebrities;

            var body = DoubanApiClient.GetJson(
                DoubanApiClient.BuildCelebritiesUrl(context.DoubanSubjectType, context.DoubanSubjectId));
            if (string.IsNullOrWhiteSpace(body)) return null;

            try {
                var actors = JsonSerializer.Deserialize<DoubanCelebrityResponse>(body, JsonOptions)?.actors ??
                             new List<DoubanCelebrity>();
                SetDoubanCelebrityCache(context.CacheKey, actors);
                return actors;
            }
            catch (Exception) {
                return null;
            }
        }

        private static MediaLookupContext BuildLookupContext(ItemLookupInfo info) {
            if (info == null) return null;

            if (info is MovieInfo movieInfo) return BuildLookupContextCore(movieInfo, "movie");

            if (info is SeriesInfo seriesInfo) return BuildLookupContextCore(seriesInfo, "tv");

            if (info is SeasonInfo seasonInfo)
                return BuildLookupContextCore(seasonInfo, "tv", seasonInfo.SeriesProviderIds, seasonInfo.SeriesName);

            if (info is EpisodeInfo episodeInfo)
                return BuildLookupContextCore(episodeInfo, "tv", episodeInfo.SeriesProviderIds, episodeInfo.Name);

            return null;
        }

        private static MediaLookupContext BuildMediaLookupContext(BaseItem item) {
            if (item == null) return null;

            var lookupItem = item;
            string subjectType;
            if (item is Movie) {
                subjectType = "movie";
            }
            else if (item is Series) {
                subjectType = "tv";
            }
            else if (item is Season season) {
                lookupItem = season.Series;
                subjectType = "tv";
            }
            else if (item is Episode episode) {
                lookupItem = episode.Series;
                subjectType = "tv";
            }
            else {
                return null;
            }

            var subjectId = NormalizeDoubanSubjectId(lookupItem.GetProviderId(DoubanExternalId.StaticName));
            if (string.IsNullOrWhiteSpace(subjectId)) return null;

            return new MediaLookupContext {
                DoubanSubjectId = subjectId,
                DoubanSubjectType = subjectType,
                CacheKey = subjectType + ":" + subjectId
            };
        }

        private static MediaLookupContext BuildLookupContextCore(
            ItemLookupInfo info,
            string subjectType,
            IReadOnlyDictionary<string, string> providerIdsOverride = null,
            string nameOverride = null) {
            var providerId = GetProviderId(providerIdsOverride ?? info.ProviderIds, "Douban", "douban", "DoubanId",
                "doubanid");
            var subjectId = NormalizeDoubanSubjectId(providerId);
            var fromCache = false;
            if (string.IsNullOrWhiteSpace(subjectId)) {
                var imdbId = GetProviderId(providerIdsOverride ?? info.ProviderIds, MetadataProviders.Imdb.ToString(),
                    "Imdb", "imdb");
                if (!string.IsNullOrWhiteSpace(imdbId)) {
                    var imdbLookup = GetDoubanSubjectFromImdb(imdbId);
                    subjectId = imdbLookup?.SubjectId;
                    fromCache = imdbLookup?.FromCache == true;
                }
            }

            if (string.IsNullOrWhiteSpace(subjectId)) {
                var searchResult = SearchDoubanSubject(nameOverride ?? info.Name, info.Year, subjectType);
                subjectId = searchResult?.SubjectId;
                fromCache = searchResult?.FromCache == true;
            }

            if (string.IsNullOrWhiteSpace(subjectId)) return null;

            return new MediaLookupContext {
                DoubanSubjectId = subjectId,
                DoubanSubjectType = subjectType,
                CacheKey = subjectType + ":" + subjectId,
                FromCache = fromCache
            };
        }

        private static DoubanSubjectResolution GetDoubanSubjectFromImdb(string imdbId) {
            var diskCachedSubject = PluginDiskCache.GetJson<DoubanSubjectCacheEntry>(
                DoubanImdbSubjectCacheScope,
                imdbId,
                DoubanSuccessCacheDuration,
                JsonOptions);
            if (!string.IsNullOrWhiteSpace(diskCachedSubject?.SubjectId))
                return new DoubanSubjectResolution {
                    SubjectId = diskCachedSubject.SubjectId,
                    FromCache = true
                };

            var body = DoubanApiClient.PostImdbLookup(imdbId);
            if (string.IsNullOrWhiteSpace(body)) return null;

            try {
                var rawSubjectId = JsonSerializer.Deserialize<DoubanImdbLookupResponse>(body, JsonOptions)?.id;
                var subjectId = NormalizeDoubanSubjectId(rawSubjectId);
                SetDoubanImdbSubjectCache(imdbId, subjectId);
                Plugin.SharedLogger?.Debug(
                    "DoubanService 豆瓣 IMDb 请求完成: imdb={0}, rawSubject={1}, subject={2}",
                    imdbId,
                    rawSubjectId ?? string.Empty,
                    subjectId ?? string.Empty);
                return new DoubanSubjectResolution {
                    SubjectId = subjectId
                };
            }
            catch (Exception) {
                return null;
            }
        }

        private static DoubanSubjectResolution SearchDoubanSubject(BaseItem item, string subjectType) {
            var title = item?.Name?.Trim();
            return SearchDoubanSubject(title, item?.ProductionYear, subjectType);
        }

        private static DoubanSubjectResolution SearchDoubanSubject(string title, int? productionYear,
            string subjectType) {
            if (string.IsNullOrWhiteSpace(title)) return null;

            var query = productionYear.HasValue ? title + " " + productionYear.Value : title;
            var cacheKey = $"{subjectType}:{query}";
            var cachedSearch = PluginDiskCache.GetJson<DoubanSubjectCacheEntry>(
                DoubanSearchSubjectCacheScope,
                cacheKey,
                DoubanSuccessCacheDuration,
                JsonOptions);
            if (!string.IsNullOrWhiteSpace(cachedSearch?.SubjectId))
                return new DoubanSubjectResolution {
                    SubjectId = cachedSearch.SubjectId,
                    FromCache = true
                };

            var body = DoubanApiClient.GetJson(DoubanApiClient.BuildSearchUrl(query));
            if (string.IsNullOrWhiteSpace(body)) return null;

            try {
                var response = JsonSerializer.Deserialize<DoubanSearchResponse>(body, JsonOptions);
                foreach (var candidate in response?.items ?? Enumerable.Empty<DoubanSearchItem>()) {
                    if (!string.Equals(candidate?.layout, "subject", StringComparison.OrdinalIgnoreCase)) continue;

                    if (!IsMatchingTypeName(subjectType, candidate.type_name)) continue;

                    if (productionYear.HasValue &&
                        !string.IsNullOrWhiteSpace(candidate.target?.year) &&
                        !string.Equals(candidate.target.year, productionYear.Value.ToString(),
                            StringComparison.Ordinal))
                        continue;

                    if (!string.IsNullOrWhiteSpace(candidate.target?.id)) {
                        var subjectId = NormalizeDoubanSubjectId(candidate.target.id);
                        SetDoubanSearchSubjectCache(cacheKey, subjectId);
                        return new DoubanSubjectResolution {
                            SubjectId = subjectId
                        };
                    }
                }
            }
            catch (Exception) {
            }

            return null;
        }

        private static bool IsMatchingTypeName(string subjectType, string typeName) {
            if (string.IsNullOrWhiteSpace(typeName)) return false;

            if (string.Equals(subjectType, "movie", StringComparison.OrdinalIgnoreCase))
                return typeName.Contains("电影", StringComparison.Ordinal);

            return typeName.Contains("剧", StringComparison.Ordinal) ||
                   typeName.Contains("电视剧", StringComparison.Ordinal) ||
                   typeName.Contains("真人秀", StringComparison.Ordinal) ||
                   typeName.Contains("综艺", StringComparison.Ordinal) ||
                   typeName.Contains("动画", StringComparison.Ordinal);
        }

        private static DoubanCelebrity MatchDoubanCelebrity(IEnumerable<DoubanCelebrity> celebrities,
            string currentName) {
            if (celebrities == null) return null;

            var normalizedCurrent = NormalizeName(currentName);
            return celebrities.FirstOrDefault(candidate =>
                       !string.IsNullOrWhiteSpace(candidate?.latin_name) &&
                       string.Equals(NormalizeName(candidate.latin_name), normalizedCurrent,
                           StringComparison.OrdinalIgnoreCase))
                   ?? celebrities.FirstOrDefault(candidate =>
                       !string.IsNullOrWhiteSpace(candidate?.name) &&
                       string.Equals(NormalizeName(candidate.name), normalizedCurrent,
                           StringComparison.OrdinalIgnoreCase));
        }

        private static string CleanDoubanCharacter(string character) {
            if (string.IsNullOrWhiteSpace(character)) return null;

            var value = character.Trim();
            if (value.StartsWith("饰", StringComparison.Ordinal)) value = value.Substring(1).Trim();

            if (string.Equals(value, "演员", StringComparison.OrdinalIgnoreCase)) return null;

            var mixedMatch = MixedChineseEnglishRoleRegex.Match(value);
            if (mixedMatch.Success) value = mixedMatch.Groups[1].Value.Trim();

            value = value.Replace("（演员）", string.Empty).Replace("(演员)", string.Empty).Trim();
            return LanguageUtility.IsChinese(value) ? value : null;
        }

        private static string NormalizeName(string name) {
            return string.IsNullOrWhiteSpace(name)
                ? null
                : name.Trim().Replace("·", string.Empty).Replace(" ", string.Empty);
        }

        private static string NormalizeDoubanSubjectId(string value) {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var trimmed = value.Trim();
            if (trimmed.All(char.IsDigit)) return trimmed;

            var match = DoubanSubjectIdRegex.Match(trimmed);
            return match.Success ? match.Groups[1].Value : trimmed;
        }

        private static void SetDoubanCelebrityCache(string key, List<DoubanCelebrity> value) {
            PluginDiskCache.SetJson(DoubanCelebrityCacheScope, key, value, JsonOptions);
        }

        private static void SetDoubanImdbSubjectCache(string key, string value) {
            if (string.IsNullOrWhiteSpace(value)) return;

            PluginDiskCache.SetJson(
                DoubanImdbSubjectCacheScope,
                key,
                new DoubanSubjectCacheEntry { SubjectId = value },
                JsonOptions);
        }

        private static void SetDoubanSearchSubjectCache(string key, string value) {
            if (string.IsNullOrWhiteSpace(value)) return;

            PluginDiskCache.SetJson(
                DoubanSearchSubjectCacheScope,
                key,
                new DoubanSubjectCacheEntry { SubjectId = value },
                JsonOptions);
        }

        private static string GetProviderId(IHasProviderIds item, params string[] keys) {
            if (item == null) return null;

            foreach (var key in keys) {
                var providerId = item.GetProviderId(key);
                if (!string.IsNullOrWhiteSpace(providerId)) return providerId;
            }

            return null;
        }

        private static string GetProviderId(IReadOnlyDictionary<string, string> providerIds, params string[] keys) {
            if (providerIds == null) return null;

            foreach (var key in keys)
                if (providerIds.TryGetValue(key, out var providerId) && !string.IsNullOrWhiteSpace(providerId))
                    return providerId;

            return null;
        }

        internal sealed class DoubanMetadataPayload {
            public string DoubanSubjectId { get; set; }
        }

        private sealed class DoubanSearchResponse {
            public List<DoubanSearchItem> items { get; set; }
        }

        private sealed class DoubanSearchItem {
            public string type_name { get; set; }

            public string layout { get; set; }

            public DoubanSearchTarget target { get; set; }
        }

        private sealed class DoubanSearchTarget {
            public string id { get; set; }

            public string year { get; set; }
        }

        private sealed class DoubanImdbLookupResponse {
            public string id { get; set; }
        }

        private sealed class DoubanCelebrityResponse {
            public List<DoubanCelebrity> actors { get; set; }
        }

        private sealed class DoubanCelebrity {
            public string name { get; set; }

            public string latin_name { get; set; }

            public string character { get; set; }
        }

        private sealed class MediaLookupContext {
            public string DoubanSubjectId { get; set; }

            public string DoubanSubjectType { get; set; }

            public string CacheKey { get; set; }

            public bool FromCache { get; set; }
        }

        private sealed class DoubanSubjectResolution {
            public string SubjectId { get; set; }

            public bool FromCache { get; set; }
        }

        private sealed class DoubanSubjectCacheEntry {
            public string SubjectId { get; set; }
        }
    }
}
