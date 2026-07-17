using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using MediaInfoKeeper.Common;

namespace MediaInfoKeeper.Provider
{
    public sealed class BangumiCharacterProvider :
        ICustomMetadataProvider<Series>,
        ICustomMetadataProvider<Movie>,
        ICustomMetadataProvider<Episode>,
        IRemoteMetadataProvider<Series, SeriesInfo>,
        IHasOrder
    {
        public string Name => ProviderName;
        public const string ProviderName = "BangumiCharacter";
        public int Order => int.MaxValue - 5;

        private readonly ILogger logger;

        private sealed class SubjectIndex
        {
            public Dictionary<string, string> ByEn;
            public Dictionary<string, string> ByActor;
            public Dictionary<string, string> ActorMap;
            public string OriginalLanguage;
        }

        private static readonly ConcurrentDictionary<string, SubjectIndex> IndexCache
            = new ConcurrentDictionary<string, SubjectIndex>();

        public BangumiCharacterProvider(ILogManager logManager)
        {
            logger = logManager.GetLogger(ProviderName);
        }

        private (string apiKey, string baseUrl) GetTmdbConfig()
        {
            var networkOptions = Plugin.Instance?.Options?.GetNetWorkOptions();
            var apiKey = networkOptions?.AlternativeTmdbApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey = "f6bd687ffa63cd282b6ff2c6877f2669";
                logger.Debug("Bangumi 角色增强: TMDB 使用 Emby 内置 Key");
            }

            var baseUrl = networkOptions?.AlternativeTmdbApiUrl?.Trim();
            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = "https://api.themoviedb.org";
            else if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                     !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                baseUrl = "https://" + baseUrl;

            return (apiKey, baseUrl.TrimEnd('/'));
        }

        private async Task<(string EnglishTitle, string OriginalLanguage)> FetchItemInfoAsync(string tmdbId, string mediaType)
        {
            try
            {
                var (apiKey, baseUrl) = GetTmdbConfig();
                var url = $"{baseUrl}/3/{mediaType}/{Uri.EscapeDataString(tmdbId)}?api_key={apiKey}&language=en";
                logger.Debug("Bangumi 角色增强: 获取条目信息 tmdbId={0}", tmdbId);

                var opts = new HttpRequestOptions
                {
                    Url = url,
                    AcceptHeader = "application/json",
                    UserAgent = "MediaInfoKeeper/1.0 (Bangumi)",
                    TimeoutMs = 10000,
                    LogRequest = true, LogRequestAsDebug = true
                };
                using var resp = await Task.Run(() => Plugin.SharedHttpClient.SendAsync(opts, "GET"));
                using var reader = new StreamReader(resp.Content);
                var json = await reader.ReadToEndAsync();

                using var doc = JsonDocument.Parse(json);
                var englishTitle = doc.RootElement.TryGetProperty(mediaType == "tv" ? "name" : "title", out var nameEl)
                    ? nameEl.GetString() : null;
                var originalLanguage = doc.RootElement.TryGetProperty("original_language", out var langEl)
                    ? langEl.GetString() : null;

                logger.Debug("Bangumi 角色增强: 条目信息 enTitle={0}, origLang={1}", englishTitle ?? "(null)", originalLanguage ?? "(null)");
                return (englishTitle, originalLanguage);
            }
            catch (Exception ex)
            {
                logger.Debug("Bangumi 角色增强: 获取条目信息失败: {0}", ex.Message);
                return (null, null);
            }
        }

        public Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken ct)
            => Task.FromResult(new MetadataResult<Series> { Item = new Series() });

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken ct)
            => Task.FromResult<IEnumerable<RemoteSearchResult>>(Array.Empty<RemoteSearchResult>());

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken ct)
            => Task.FromResult<HttpResponseInfo>(null);

        public Task<ItemUpdateType> FetchAsync(MetadataResult<Movie> r, MetadataRefreshOptions o, LibraryOptions l, CancellationToken ct)
            => EnhanceAsync(r?.Item, r?.People, ct);

        public Task<ItemUpdateType> FetchAsync(MetadataResult<Series> r, MetadataRefreshOptions o, LibraryOptions l, CancellationToken ct)
            => EnhanceAsync(r?.Item, r?.People, ct);

        public Task<ItemUpdateType> FetchAsync(MetadataResult<Episode> r, MetadataRefreshOptions o, LibraryOptions l, CancellationToken ct)
            => EnhanceEpisodeAsync(r?.Item, r?.People, ct);

        private async Task<ItemUpdateType> EnhanceEpisodeAsync(Episode episode, List<PersonInfo> people, CancellationToken ct)
        {
            if (episode == null || people == null || people.Count == 0) return ItemUpdateType.None;
            if (Plugin.Instance?.Options?.MainPage?.PlugginEnabled != true) return ItemUpdateType.None;
            if (Plugin.Instance?.Options?.MetaData?.EnableBangumiCharacters != true) return ItemUpdateType.None;

            var series = episode.Series;
            if (series == null) return ItemUpdateType.None;

            var tmdbId = series.GetProviderId(MetadataProviders.Tmdb);
            if (string.IsNullOrWhiteSpace(tmdbId)) return ItemUpdateType.None;

            if (!IndexCache.TryGetValue(tmdbId, out var indexes))
            {
                logger.Debug("Bangumi 角色增强(剧集): 索引缓存未命中, 跳过 tmdbId={0}", tmdbId);
                return ItemUpdateType.None;
            }

            var charToActor = indexes.ActorMap;
            if (charToActor == null || charToActor.Count == 0)
            {
                var lang = indexes.OriginalLanguage ?? "ja";
                logger.Debug("Bangumi 角色增强(剧集): ActorMap 缓存未命中, 按原始语言获取 lang={0}", lang);
                charToActor = await FetchTmdbActorsAsync(series, lang);
                if (charToActor == null || charToActor.Count == 0)
                {
                    logger.Info("Bangumi 角色增强(剧集): 原始语言声优数据为空, 降级使用 ja-JP");
                    charToActor = await FetchTmdbActorsAsync(series, "ja-JP");
                }
                indexes.ActorMap = charToActor;
            }

            // Build episode-level actor map from the episode's own people list
            // so guest characters (only in specific episodes) can also match via actor name
            var episodeActorMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in people.Where(p => p.Type == PersonType.Actor))
            {
                var role = (p.Role ?? "").Trim().Replace(" (voice)", "").Replace("(voice)", "").Trim();
                var actorName = (p.Name ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(role) && !string.IsNullOrWhiteSpace(actorName))
                    episodeActorMap[role] = actorName;
            }

            var mergedActorMap = new Dictionary<string, string>(charToActor ?? new(), StringComparer.OrdinalIgnoreCase);
            foreach (var kv in episodeActorMap)
            {
                if (!mergedActorMap.ContainsKey(kv.Key))
                    mergedActorMap[kv.Key] = kv.Value;
            }

            var actorCount = people.Count(p => p.Type == PersonType.Actor);
            var matchCount = MatchPeople(people, indexes.ByEn, indexes.ByActor, mergedActorMap);
            if (matchCount > 0)
            {
                logger.Debug("Bangumi 角色增强(剧集): 成功匹配 {0}/{1} 个角色", matchCount, actorCount);
                return ItemUpdateType.MetadataImport;
            }

            return ItemUpdateType.None;
        }

        private async Task<ItemUpdateType> EnhanceAsync(BaseItem item, List<PersonInfo> people, CancellationToken ct)
        {
            var itemName = item?.Name ?? "(null)";
            if (!ShouldFetch(item, people, out var skipReason))
            {
                if (skipReason != null)
                    logger.Debug("Bangumi 角色增强跳过: {0} - {1}", itemName, skipReason);
                return ItemUpdateType.None;
            }

            try
            {
                logger.Info("Bangumi 角色增强 V2: 启动");
                var tmdbId = item.GetProviderId(MetadataProviders.Tmdb);
                var mediaType = item is Series ? "tv" : "movie";
                logger.Debug("Bangumi 角色增强: 条目={0}, tmdbId={1}, 现有角色数={2}", itemName, tmdbId, people.Count);

                var (englishTitle, originalLanguage) = await FetchItemInfoAsync(tmdbId, mediaType);
                if (string.IsNullOrWhiteSpace(originalLanguage))
                    originalLanguage = "ja";

                int? subjectId = null;

                if (originalLanguage == "zh")
                {
                    var cnTitle = item.Name;
                    if (!string.IsNullOrWhiteSpace(cnTitle))
                    {
                        logger.Debug("Bangumi 角色增强: 国漫中文标题搜索, keyword={0}", cnTitle);
                        subjectId = await SearchBangumiAsync(cnTitle);
                    }
                }
                else if (originalLanguage == "ja")
                {
                    var japTitle = GetJapaneseTitle(item);
                    if (!string.IsNullOrWhiteSpace(japTitle))
                    {
                        logger.Debug("Bangumi 角色增强: 日漫日文标题搜索, keyword={0}", japTitle);
                        subjectId = await SearchBangumiAsync(japTitle);
                    }
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(englishTitle))
                    {
                        logger.Debug("Bangumi 角色增强: 英文标题搜索, keyword={0}", englishTitle);
                        subjectId = await SearchBangumiAsync(englishTitle);
                    }
                }

                if (!subjectId.HasValue && !string.IsNullOrWhiteSpace(englishTitle))
                {
                    logger.Info("Bangumi 角色增强: 源语言搜索无结果, 降级使用英文标题={0}", englishTitle);
                    subjectId = await SearchBangumiAsync(englishTitle);
                }

                if (!subjectId.HasValue)
                {
                    logger.Info("Bangumi 角色增强: 搜索无结果");
                    return ItemUpdateType.None;
                }
                logger.Debug("Bangumi 角色增强: 匹配科目 id={0}", subjectId.Value);

                var charList = await FetchCharacterListAsync(subjectId.Value);
                if (charList.Count == 0)
                {
                    logger.Info("Bangumi 角色增强: 角色列表为空, subject_id={0}", subjectId.Value);
                    return ItemUpdateType.None;
                }
                var charsWithActors = charList.Count(c => c.Actors.Count > 0);
                logger.Debug("Bangumi 角色增强: 获取到 {0} 个角色, 其中 {1} 个有声优", charList.Count, charsWithActors);

                var (byEn, byActor) = await BuildIndexesAsync(charList, originalLanguage);
                logger.Debug("Bangumi 角色增强: 索引构建完成, by_en={0}条, by_actor={1}条", byEn.Count, byActor.Count);

                var cacheEntry = new SubjectIndex { ByEn = byEn, ByActor = byActor, OriginalLanguage = originalLanguage };
                if (!string.IsNullOrWhiteSpace(tmdbId))
                {
                    IndexCache[tmdbId] = cacheEntry;
                    logger.Debug("Bangumi 角色增强: 索引已缓存, tmdbId={0}, origLang={1}", tmdbId, originalLanguage);
                }

                var charToActor = await FetchTmdbActorsAsync(item, originalLanguage);
                if (charToActor == null || charToActor.Count == 0)
                {
                    logger.Info("Bangumi 角色增强: 原始语言声优数据为空, 降级使用 ja-JP");
                    charToActor = await FetchTmdbActorsAsync(item, "ja-JP");
                }
                cacheEntry.ActorMap = charToActor;

                var actorCount = people.Count(p => p.Type == PersonType.Actor);
                var matchCount = MatchPeople(people, byEn, byActor, charToActor);
                if (matchCount > 0)
                {
                    logger.Info("Bangumi 角色增强: 成功匹配 {0}/{1} 个角色, lang={2}", matchCount, actorCount, originalLanguage);
                    return ItemUpdateType.MetadataImport;
                }

                logger.Debug("Bangumi 角色增强: 未匹配到任何角色 ({0} 个演员)", actorCount);
                return ItemUpdateType.None;
            }
            catch (Exception ex)
            {
                logger.Error("Bangumi 角色增强异常: {0}", ex.Message);
                return ItemUpdateType.None;
            }
        }

        private static string GetJapaneseTitle(BaseItem item)
        {
            if (item is Series series)
                return series.OriginalTitle ?? series.Name;

            if (item is Movie movie)
                return movie.OriginalTitle ?? movie.Name;

            return item?.Name;
        }

        private async Task<int?> SearchBangumiAsync(string keyword)
        {
            try
            {
                var body = BangumiApiClient.BuildSearchBody(keyword);
                var opts = new HttpRequestOptions
                {
                    Url = BangumiApiClient.GetSearchUrl(),
                    RequestContent = body.AsMemory(),
                    RequestContentType = "application/json",
                    AcceptHeader = "application/json",
                    UserAgent = "MediaInfoKeeper/1.0 (Bangumi)",
                    TimeoutMs = 10000,
                    LogRequest = true, LogRequestAsDebug = true
                };
                using var resp = await Task.Run(() => Plugin.SharedHttpClient.SendAsync(opts, "POST"));
                using var reader = new StreamReader(resp.Content);
                var json = await reader.ReadToEndAsync();
                return BangumiApiClient.ParseSearchResult(json);
            }
            catch (Exception ex)
            {
                logger.Error("Bangumi 搜索异常: {0}", ex.Message);
                return null;
            }
        }

        private async Task<List<(int Id, string NameJp, List<string> Actors, List<int> ActorIds)>> FetchCharacterListAsync(int subjectId)
        {
            try
            {
                var charUrl = BangumiApiClient.BuildCharactersUrl(subjectId);
                var charOpts = new HttpRequestOptions
                {
                    Url = charUrl,
                    AcceptHeader = "application/json",
                    UserAgent = "MediaInfoKeeper/1.0 (Bangumi)",
                    TimeoutMs = 10000,
                    LogRequest = true, LogRequestAsDebug = true
                };
                using var chrResp = await Task.Run(() => Plugin.SharedHttpClient.SendAsync(charOpts, "GET"));
                using var chrReader = new StreamReader(chrResp.Content);
                var charJson = await chrReader.ReadToEndAsync();
                return BangumiApiClient.ParseCharacterList(charJson);
            }
            catch (Exception ex)
            {
                logger.Error("Bangumi 获取角色列表异常: {0}", ex.Message);
                return new List<(int, string, List<string>, List<int>)>();
            }
        }

        private async Task<(Dictionary<string, string> ByEn, Dictionary<string, string> ByActor)> BuildIndexesAsync(
            List<(int Id, string NameJp, List<string> Actors, List<int> ActorIds)> charList, string originalLanguage)
        {
            var byEn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byActor = new Dictionary<string, string>(StringComparer.Ordinal);
            var detailCount = 0;
            var actorDetailCount = 0;
            var isChinese = originalLanguage == "zh";

            foreach (var (id, _, actors, actorIds) in charList)
            {
                try
                {
                    var detailUrl = BangumiApiClient.BuildCharacterDetailUrl(id);
                    var detailOpts = new HttpRequestOptions
                    {
                        Url = detailUrl,
                        AcceptHeader = "application/json",
                        UserAgent = "MediaInfoKeeper/1.0 (Bangumi)",
                        TimeoutMs = 5000,
                        LogRequest = true, LogRequestAsDebug = true
                    };
                    using var dResp = await Task.Run(() => Plugin.SharedHttpClient.SendAsync(detailOpts, "GET"));
                    using var dReader = new StreamReader(dResp.Content);
                    var dJson = await dReader.ReadToEndAsync();

                    var (chs, en) = BangumiApiClient.ParseCharacterDetail(dJson);
                    if (string.IsNullOrWhiteSpace(chs)) continue;

                    detailCount++;

                    if (!string.IsNullOrWhiteSpace(en) && !byEn.ContainsKey(en))
                        byEn[en] = chs;

                    if (actors.Count > 0 && !string.IsNullOrWhiteSpace(actors[0]) && !byActor.ContainsKey(actors[0]))
                        byActor[actors[0]] = chs;

                    if (isChinese && actorIds.Count > 0)
                    {
                        try
                        {
                            var personUrl = BangumiApiClient.BuildPersonDetailUrl(actorIds[0]);
                            var personOpts = new HttpRequestOptions
                            {
                                Url = personUrl,
                                AcceptHeader = "application/json",
                                UserAgent = "MediaInfoKeeper/1.0 (Bangumi)",
                                TimeoutMs = 5000,
                                LogRequest = true, LogRequestAsDebug = true
                            };
                            using var pResp = await Task.Run(() => Plugin.SharedHttpClient.SendAsync(personOpts, "GET"));
                            using var pReader = new StreamReader(pResp.Content);
                            var pJson = await pReader.ReadToEndAsync();

                            var aliases = BangumiApiClient.ParsePersonAliases(pJson);
                            foreach (var alias in aliases)
                            {
                                if (!byActor.ContainsKey(alias))
                                    byActor[alias] = chs;
                            }
                            actorDetailCount++;
                        }
                        catch (Exception ex)
                        {
                            logger.Debug("Bangumi 角色增强: 获取声优详情失败 id={0}: {1}", actorIds[0], ex.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.Debug("Bangumi 角色增强: 获取角色详情失败 id={0}: {1}", id, ex.Message);
                }
            }

            if (isChinese)
                logger.Debug("Bangumi 角色增强: 共获取 {0} 个角色详情({1}个声优详情), 共 {2} 个角色, by_actor={3}条",
                    detailCount, actorDetailCount, charList.Count, byActor.Count);
            else
                logger.Debug("Bangumi 角色增强: 共获取 {0} 个角色详情（共 {1} 个角色）, by_actor={2}条",
                    detailCount, charList.Count, byActor.Count);
            return (byEn, byActor);
        }

        private static int MatchPeople(List<PersonInfo> people,
            Dictionary<string, string> byEn, Dictionary<string, string> byActor,
            Dictionary<string, string> charToJapActor)
        {
            if (people == null || people.Count == 0) return 0;
            var matchCount = 0;
            var skipChinese = Plugin.Instance?.Options?.MetaData?.BangumiSkipExistingChinese == true;

            foreach (var person in people.Where(p => p.Type == PersonType.Actor))
            {
                var role = (person.Role ?? "").Trim();
                var roleClean = role.Replace(" (voice)", "").Replace("(voice)", "").Trim();

                if (skipChinese && ContainsChinese(roleClean))
                    continue;

                if (!string.IsNullOrWhiteSpace(roleClean) && byEn.TryGetValue(roleClean, out var chsName))
                {
                    if (person.Role != chsName) { person.Role = chsName; matchCount++; }
                    continue;
                }

                if (charToJapActor != null && !string.IsNullOrWhiteSpace(roleClean) &&
                    charToJapActor.TryGetValue(roleClean, out var japActor) &&
                    !string.IsNullOrWhiteSpace(japActor) &&
                    byActor.TryGetValue(japActor, out chsName))
                {
                    if (person.Role != chsName) { person.Role = chsName; matchCount++; }
                }
            }

            return matchCount;
        }

        private async Task<Dictionary<string, string>> FetchTmdbActorsAsync(BaseItem item, string language)
        {
            try
            {
                var tmdbId = item.GetProviderId(MetadataProviders.Tmdb);
                logger.Debug("Bangumi 角色增强: TMDB 声优查询, lang={0}, tmdbId={1}", language ?? "(null)", tmdbId ?? "(null)");
                if (string.IsNullOrWhiteSpace(tmdbId)) { logger.Debug("Bangumi 角色增强: TMDB 声优跳过, 无 TMDB ID"); return null; }
                if (string.IsNullOrWhiteSpace(language)) { logger.Debug("Bangumi 角色增强: TMDB 声优跳过, 语言为空"); return null; }

                var (apiKey, baseUrl) = GetTmdbConfig();
                var mediaType = item is Series ? "tv" : "movie";
                var url = $"{baseUrl}/3/{mediaType}/{Uri.EscapeDataString(tmdbId)}?api_key={apiKey}&language={Uri.EscapeDataString(language)}&append_to_response=credits";
                logger.Debug("Bangumi 角色增强: TMDB 声优请求 {0}", url.Replace(apiKey, "***"));

                var opts = new HttpRequestOptions
                {
                    Url = url,
                    AcceptHeader = "application/json",
                    UserAgent = "MediaInfoKeeper/1.0 (Bangumi)",
                    TimeoutMs = 10000,
                    LogRequest = true, LogRequestAsDebug = true
                };
                using var resp = await Task.Run(() => Plugin.SharedHttpClient.SendAsync(opts, "GET"));
                using var reader = new StreamReader(resp.Content);
                var json = await reader.ReadToEndAsync();

                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("credits", out var credits)) { logger.Debug("Bangumi 角色增强: TMDB 声优 返回无 credits 字段"); return result; }
                if (!credits.TryGetProperty("cast", out var cast) || cast.ValueKind != JsonValueKind.Array) { logger.Debug("Bangumi 角色增强: TMDB 声优 返回无 cast 数组"); return result; }

                foreach (var member in cast.EnumerateArray())
                {
                    var character = member.TryGetProperty("character", out var ch) && ch.ValueKind == JsonValueKind.String
                        ? ch.GetString()?.Replace(" (voice)", "").Replace("(voice)", "").Trim() : null;
                    var name = member.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
                        ? nm.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(character) && !string.IsNullOrWhiteSpace(name) &&
                        !result.ContainsKey(character))
                        result[character] = name;
                }

                logger.Debug("Bangumi 角色增强: TMDB 声优数据获取成功, lang={0}, {1} 条", language, result.Count);
                return result;
            }
            catch (Exception ex)
            {
                logger.Debug("Bangumi 角色增强: TMDB 声优数据获取失败, lang={0}: {1}", language ?? "null", ex.Message);
                return null;
            }
        }

        private bool ShouldFetch(BaseItem item, List<PersonInfo> people, out string skipReason)
        {
            skipReason = null;

            if (item == null) { skipReason = "条目为空"; return false; }
            if (people == null || people.Count == 0) { skipReason = "人物列表为空"; return false; }
            if (Plugin.Instance?.Options?.MainPage?.PlugginEnabled != true) { skipReason = "插件未启用"; return false; }
            if (Plugin.Instance?.Options?.MetaData?.EnableBangumiCharacters != true) { skipReason = "Bangumi 角色增强未开启"; return false; }
            if (!(item is Series || item is Movie)) { skipReason = "不支持的类型: " + item.GetType().Name; return false; }

            var libraryOptions = Plugin.LibraryManager?.GetLibraryOptions(item);
            if (libraryOptions == null) { skipReason = "无法获取媒体库配置"; return false; }
            if (!item.IsMetadataFetcherEnabled(libraryOptions, ProviderName)) { skipReason = "当前媒体库未启用 BangumiCharacter 获取器"; return false; }

            return true;
        }

        private static bool ContainsChinese(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (var c in text)
            {
                if (c >= 0x4E00 && c <= 0x9FFF) return true;
                if (c >= 0x3400 && c <= 0x4DBF) return true;
                if (c >= 0xF900 && c <= 0xFAFF) return true;
            }
            return false;
        }
    }
}
