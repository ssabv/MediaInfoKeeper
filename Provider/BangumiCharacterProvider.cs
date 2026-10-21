using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using HttpRequestOptions = MediaBrowser.Common.Net.HttpRequestOptions;
using System.Text;
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

        /// <summary>
        ///     单季(或单条目)的角色索引。
        /// </summary>
        private sealed class SubjectIndex
        {
            /// <summary>角色英文名(别名) → 中文显示名</summary>
            public Dictionary<string, string> ByEn;

            /// <summary>角色名(Bangumi 原名/简体中文名) → 中文显示名。TMDB role 精确匹配的主路径。</summary>
            public Dictionary<string, string> ByRoleName;

            /// <summary>声优名(含别名) → 角色中文显示名有序列表。
            /// 顺序 = Bangumi characters 返回顺序(主角在前)。声优配多角时取前两个。</summary>
            public Dictionary<string, List<string>> ByActorList;

            /// <summary>角色名 → 日文声优名(TMDB credits 反查表)，按需填充</summary>
            public Dictionary<string, string> ActorMap;

            public string OriginalLanguage;
        }

        /// <summary>
        ///     一部作品的系列索引：TMDB 季号 → 该季的角色索引；
        ///     Fallback = 全系列合并索引（该季无独立条目时的兜底）。
        /// </summary>
        private sealed class FranchiseIndex
        {
            public string OriginalLanguage;
            public Dictionary<int, SubjectIndex> Seasons = new Dictionary<int, SubjectIndex>();
            public SubjectIndex Fallback;
        }

        private static readonly ConcurrentDictionary<string, FranchiseIndex> FranchiseCache
            = new ConcurrentDictionary<string, FranchiseIndex>();

        /// <summary>
        ///     索引构建中的 Task 缓存(tmdbId -> 构建任务)。
        ///     单飞保护: 同一 tmdbId 并发刷新(如剧集级联刷新 12 集)时,
        ///     只让第一个请求真正构建索引, 其余请求 await 同一个 Task。
        /// </summary>
        private static readonly ConcurrentDictionary<string, Task<FranchiseIndex>> FranchiseBuildTasks
            = new ConcurrentDictionary<string, Task<FranchiseIndex>>();


        /// <summary>
        ///     幕后人员职务词 → 中文（TMDB job 原词硬编码映射，不查 Bangumi）。
        ///     只替换 PersonInfo.Role 的显示词，Type 保持 Emby 原值，导演/编剧栏分组不受影响。
        /// </summary>
        private static readonly Dictionary<string, string> CrewJobZh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Directing
            ["Director"] = "导演",
            ["Series Director"] = "系列导演",
            ["Assistant Director"] = "副导演",
            ["Episode Director"] = "分集导演",
            ["Action Director"] = "动作导演",
            ["Animation Director"] = "作画导演",
            ["Art Director"] = "美术导演",
            ["Sound Director"] = "音响导演",
            ["Chief Animation Director"] = "总作画导演",
            ["Storyboard Artist"] = "分镜",
            // Writing
            ["Writer"] = "编剧",
            ["Screenplay"] = "编剧",
            ["Screenstory"] = "剧本",
            ["Story"] = "故事",
            ["Original Story"] = "原案",
            ["Novel"] = "原著",
            ["Comic"] = "原作漫画",
            ["Manga"] = "原作漫画",
            ["Original Series"] = "原作",
            ["Original Film"] = "原作",
            ["Series Composition"] = "系列构成",
            ["Script Editor"] = "剧本编辑",
            ["Script Coordinator"] = "剧本协调",
            ["Co-Writer"] = "联合编剧",
            // Production
            ["Producer"] = "制片人",
            ["Executive Producer"] = "执行制片人",
            ["Co-Producer"] = "联合制片人",
            ["Associate Producer"] = "副制片人",
            ["Supervising Producer"] = "监制",
            ["Co-Executive Producer"] = "联合执行制片人",
            ["Development Producer"] = "制片开发",
            ["Consulting Producer"] = "顾问制片人",
            ["Line Producer"] = "制片主任",
            ["Production Supervisor"] = "制片主管",
            ["Production Manager"] = "制片经理",
            ["Production Coordinator"] = "制片协调",
            ["Production Assistant"] = "制片助理",
            ["Production Director"] = "制片导演",
            ["Casting Director"] = "选角导演",
            ["Casting"] = "选角",
            ["Casting Consultant"] = "选角顾问",
            ["Publicist"] = "宣传",
        };

        /// <summary>系列链安全上限，防止异常数据无限爬取</summary>
        private const int MaxFranchiseEntries = 40;

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

        private async Task<(string EnglishTitle, string OriginalLanguage, int? Year, int? EpisodeCount)> FetchItemInfoAsync(string tmdbId, string mediaType)
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

                int? year = null;
                var dateProperty = mediaType == "tv" ? "first_air_date" : "release_date";
                if (doc.RootElement.TryGetProperty(dateProperty, out var dateEl) && dateEl.ValueKind == JsonValueKind.String)
                {
                    var date = dateEl.GetString();
                    if (!string.IsNullOrWhiteSpace(date) && date.Length >= 4 &&
                        int.TryParse(date.Substring(0, 4), out var parsedYear))
                    {
                        year = parsedYear;
                    }
                }

                int? episodeCount = null;
                if (mediaType == "tv" &&
                    doc.RootElement.TryGetProperty("number_of_episodes", out var epsEl) &&
                    epsEl.ValueKind == JsonValueKind.Number)
                {
                    episodeCount = epsEl.GetInt32();
                }

                logger.Debug(
                    "Bangumi 角色增强: 条目信息 enTitle={0}, origLang={1}, year={2}, eps={3}",
                    englishTitle ?? "(null)",
                    originalLanguage ?? "(null)",
                    year?.ToString() ?? "(null)",
                    episodeCount?.ToString() ?? "(null)");
                return (englishTitle, originalLanguage, year, episodeCount);
            }
            catch (Exception ex)
            {
                logger.Debug("Bangumi 角色增强: 获取条目信息失败: {0}", ex.Message);
                return (null, null, null, null);
            }
        }

        /// <summary>
        ///     获取 TMDB 剧集各季的首播日期，用于 Bangumi 条目的分季归属。
        /// </summary>
        private async Task<List<(int SeasonNumber, DateTime? AirDate)>> FetchTmdbSeasonsAsync(string tmdbId)
        {
            var result = new List<(int, DateTime?)>();
            try
            {
                var (apiKey, baseUrl) = GetTmdbConfig();
                var url = $"{baseUrl}/3/tv/{Uri.EscapeDataString(tmdbId)}?api_key={apiKey}&language=en";
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
                if (doc.RootElement.TryGetProperty("seasons", out var seasons) && seasons.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in seasons.EnumerateArray())
                    {
                        if (!s.TryGetProperty("season_number", out var numEl) || numEl.ValueKind != JsonValueKind.Number)
                            continue;
                        DateTime? air = null;
                        if (s.TryGetProperty("air_date", out var airEl) && airEl.ValueKind == JsonValueKind.String)
                        {
                            var ds = airEl.GetString();
                            if (!string.IsNullOrWhiteSpace(ds) && ds.Length >= 10 &&
                                DateTime.TryParseExact(ds.Substring(0, 10), "yyyy-MM-dd",
                                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                            {
                                air = dt;
                            }
                        }
                        result.Add((numEl.GetInt32(), air));
                    }
                }
                logger.Debug("Bangumi 角色增强: TMDB 季信息 {0} 季", result.Count);
            }
            catch (Exception ex)
            {
                logger.Debug("Bangumi 角色增强: TMDB 季信息获取失败: {0}", ex.Message);
            }
            return result;
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

            var franchise = await GetOrCreateFranchiseIndexAsync(series, tmdbId, "tv", ct);
            if (franchise == null) return ItemUpdateType.None;

            // 按集所属季取专属索引; 该季在 Bangumi 无独立条目归属时用全系列合并索引兜底
            var seasonNumber = episode.ParentIndexNumber ?? 0;
            if (!franchise.Seasons.TryGetValue(seasonNumber, out var indexes) || indexes == null)
            {
                logger.Debug("Bangumi 角色增强(剧集): 季 {0} 无专属索引, 使用全系列兜底", seasonNumber);
                indexes = franchise.Fallback;
            }
            if (indexes == null) return ItemUpdateType.None;

            var charToActor = indexes.ActorMap;
            if (charToActor == null || charToActor.Count == 0)
            {
                var lang = indexes.OriginalLanguage ?? franchise.OriginalLanguage ?? "ja";
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
            foreach (var p in people.Where(p => p.Type == PersonType.Actor || p.Type == PersonType.GuestStar))
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

            var actorCount = people.Count(p => p.Type == PersonType.Actor || p.Type == PersonType.GuestStar);

            // 角色粒度兜底: 每个角色先查自己季的索引, 单个没查到就用全系列合并索引再查
            // (TMDB 畸形条目如 Re:Zero 把 S2~S4 塞进 S1, 季桶错位时按季查不到的角色由兜底命中)
            var (matchCount, toTranslate) = MatchPeople(people, indexes, franchise.Fallback, mergedActorMap);

            // 幕后人员职务词中文化: Director→导演、Producer→制片人 等(静态表, 不查任何 API)
            matchCount += ApplyCrewJobChinese(people);

            // Bangumi 只有日文名的角色 → DeepL 机翻成中文; 失败回退日文
            matchCount += await TranslateKanaNamesAsync(toTranslate);

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

                var franchise = await GetOrCreateFranchiseIndexAsync(item, tmdbId, mediaType, ct);
                if (franchise == null) return ItemUpdateType.None;

                // Series/Movie 级人物表是整体的 → 用全系列合并索引
                var idx = franchise.Fallback;
                if (idx == null) return ItemUpdateType.None;

                var originalLanguage = franchise.OriginalLanguage;
                var charToActor = await FetchTmdbActorsAsync(item, originalLanguage);
                if (charToActor == null || charToActor.Count == 0)
                {
                    logger.Info("Bangumi 角色增强: 原始语言声优数据为空, 降级使用 ja-JP");
                    charToActor = await FetchTmdbActorsAsync(item, "ja-JP");
                }

                var actorCount = people.Count(p => p.Type == PersonType.Actor || p.Type == PersonType.GuestStar);
                var (matchCount, toTranslate) = MatchPeople(people, idx, null, charToActor);

                // 幕后人员职务词中文化: Director→导演、Producer→制片人 等(静态表, 不查任何 API)
                matchCount += ApplyCrewJobChinese(people);

                // Bangumi 只有日文名的角色 → DeepL 机翻成中文; 失败回退日文
                matchCount += await TranslateKanaNamesAsync(toTranslate);

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

        /// <summary>
        ///     构建整部作品的系列索引：
        ///     1. 搜索锚点条目(逻辑与旧版一致)
        ///     2. 沿 Bangumi 续集/前传关系爬完整链(剧场版/衍生/不同世界观不爬)
        ///     3. 按播出日期把链上条目归属到 TMDB 季(匹配不上时就近兜底)
        ///     4. 每季合并角色索引 + 全系列合并兜底索引
        /// </summary>
        private async Task<FranchiseIndex> BuildFranchiseCoreAsync(BaseItem item, string tmdbId, string mediaType)
        {
            var (englishTitle, originalLanguage, tmdbYear, _) = await FetchItemInfoAsync(tmdbId, mediaType);
            if (string.IsNullOrWhiteSpace(originalLanguage))
                originalLanguage = "ja";

            int? subjectId = null;

            if (originalLanguage == "zh")
            {
                var cnTitle = item.Name;
                if (!string.IsNullOrWhiteSpace(cnTitle))
                {
                    logger.Debug("Bangumi 角色增强: 国漫中文标题搜索, keyword={0}", cnTitle);
                    subjectId = await SearchBangumiAsync(cnTitle, tmdbYear, mediaType);
                }
            }
            else if (originalLanguage == "ja")
            {
                var japTitle = GetJapaneseTitle(item);
                if (!string.IsNullOrWhiteSpace(japTitle))
                {
                    logger.Debug("Bangumi 角色增强: 日漫日文标题搜索, keyword={0}", japTitle);
                    subjectId = await SearchBangumiAsync(japTitle, tmdbYear, mediaType);
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(englishTitle))
                {
                    logger.Debug("Bangumi 角色增强: 英文标题搜索, keyword={0}", englishTitle);
                    subjectId = await SearchBangumiAsync(englishTitle, tmdbYear, mediaType);
                }
            }

            if (!subjectId.HasValue && !string.IsNullOrWhiteSpace(englishTitle))
            {
                logger.Info("Bangumi 角色增强: 源语言搜索无结果, 降级使用英文标题={0}", englishTitle);
                subjectId = await SearchBangumiAsync(englishTitle, tmdbYear, mediaType);
            }

            if (!subjectId.HasValue)
            {
                logger.Info("Bangumi 角色增强: 搜索无结果");
                return null;
            }
            logger.Debug("Bangumi 角色增强: 匹配科目 id={0}", subjectId.Value);

            var franchise = new FranchiseIndex { OriginalLanguage = originalLanguage };

            if (mediaType == "movie")
            {
                // 电影: TMDB 每部一个条目, 不爬系列链, 单条目索引即兜底
                var charList = await FetchCharacterListAsync(subjectId.Value);
                if (charList.Count == 0)
                {
                    logger.Info("Bangumi 角色增强: 角色列表为空, subject_id={0}", subjectId.Value);
                    return null;
                }
                franchise.Fallback = await BuildIndexesAsync(charList, originalLanguage);
                return franchise;
            }

            // ---- TV: 爬系列链 + 按季分桶 ----
            var chain = await FetchFranchiseChainAsync(subjectId.Value);
            logger.Info("Bangumi 角色增强: 系列链 {0} 个条目: {1}",
                chain.Count,
                string.Join(", ", chain.Select(c =>
                    c.Id.ToString() + (c.Date.HasValue ? "(" + c.Date.Value.ToString("yyyy-MM-dd") + ")" : "(无日期)"))));

            // 每个条目只拉一次角色列表, 分季索引与兜底索引共用
            var charLists = new Dictionary<int, List<(int Id, string NameJp, int Type, List<string> Actors, List<int> ActorIds)>>();
            foreach (var entry in chain)
            {
                var cl = await FetchCharacterListAsync(entry.Id);
                if (cl.Count > 0) charLists[entry.Id] = cl;
            }

            var seasonsMeta = await FetchTmdbSeasonsAsync(tmdbId);
            var buckets = AssignToSeasons(chain, seasonsMeta);
            foreach (var kv in buckets)
            {
                var merged = new List<(int Id, string NameJp, int Type, List<string> Actors, List<int> ActorIds)>();
                foreach (var sid in kv.Value)
                    if (charLists.TryGetValue(sid, out var cl))
                        merged.AddRange(cl);
                if (merged.Count == 0) continue;

                var idx = await BuildIndexesAsync(merged, originalLanguage);
                if (idx != null) franchise.Seasons[kv.Key] = idx;
                logger.Info("Bangumi 角色增强: 季 {0} ← Bangumi 条目 [{1}], 角色 {2} 个",
                    kv.Key, string.Join(", ", kv.Value), merged.Count);
            }

            // 全系列合并兜底索引
            var allChars = new List<(int Id, string NameJp, int Type, List<string> Actors, List<int> ActorIds)>();
            foreach (var entry in chain)
                if (charLists.TryGetValue(entry.Id, out var cl))
                    allChars.AddRange(cl);
            if (allChars.Count > 0)
                franchise.Fallback = await BuildIndexesAsync(allChars, originalLanguage);

            if (franchise.Fallback == null && franchise.Seasons.Count == 0)
            {
                logger.Info("Bangumi 角色增强: 角色列表为空, subject_id={0}", subjectId.Value);
                return null;
            }
            return franchise;
        }

        /// <summary>
        ///     沿续集/前传关系爬整部作品的条目链。
        ///     只认 type=2(动画) 的「续集」「前传」; 衍生/不同世界观/剧场版等一律不爬。
        /// </summary>
        private async Task<List<(int Id, DateTime? Date)>> FetchFranchiseChainAsync(int anchorId)
        {
            var visited = new HashSet<int> { anchorId };
            var order = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(anchorId);
            while (queue.Count > 0 && order.Count < MaxFranchiseEntries)
            {
                var id = queue.Dequeue();
                order.Add(id);
                var rels = await FetchRelationsAsync(id);
                foreach (var r in rels)
                {
                    if (r.Type != 2) continue;
                    if (r.Relation != "续集" && r.Relation != "前传") continue;
                    if (visited.Add(r.Id) && order.Count + queue.Count < MaxFranchiseEntries)
                        queue.Enqueue(r.Id);
                }
            }

            var list = new List<(int Id, DateTime? Date)>();
            foreach (var id in order)
            {
                var date = await FetchSubjectDateAsync(id);
                list.Add((id, date));
            }
            // 按开播日期升序(无日期的排最后), 分季时沿时间轴就近归属
            return list.OrderBy(e => e.Date ?? DateTime.MaxValue).ToList();
        }

        private async Task<List<(int Id, string Relation, int Type)>> FetchRelationsAsync(int subjectId)
        {
            try
            {
                var json = await GetBangumiJsonAsync(BangumiApiClient.BuildRelationsUrl(subjectId));
                return BangumiApiClient.ParseRelations(json);
            }
            catch (Exception ex)
            {
                logger.Debug("Bangumi 角色增强: 获取关联条目失败 id={0}: {1}", subjectId, ex.Message);
                return new List<(int, string, int)>();
            }
        }

        private async Task<DateTime?> FetchSubjectDateAsync(int subjectId)
        {
            try
            {
                var json = await GetBangumiJsonAsync(BangumiApiClient.BuildSubjectDetailUrl(subjectId));
                return BangumiApiClient.ParseSubjectDate(json);
            }
            catch (Exception ex)
            {
                logger.Debug("Bangumi 角色增强: 获取条目日期失败 id={0}: {1}", subjectId, ex.Message);
                return null;
            }
        }

        private async Task<string> GetBangumiJsonAsync(string url, int timeoutMs = 10000)
        {
            var opts = new HttpRequestOptions
            {
                Url = url,
                AcceptHeader = "application/json",
                UserAgent = "MediaInfoKeeper/1.0 (Bangumi)",
                TimeoutMs = timeoutMs,
                LogRequest = true, LogRequestAsDebug = true
            };
            using var resp = await Task.Run(() => Plugin.SharedHttpClient.SendAsync(opts, "GET"));
            using var reader = new StreamReader(resp.Content);
            return await reader.ReadToEndAsync();
        }

        /// <summary>
        ///     把系列链上的条目归属到 TMDB 季：
        ///     播出日期优先就近匹配(落在某季播出窗口内自然最近);
        ///     无日期的沿链条跟随上一个条目; 匹配不上也兜底(永远有归属)。
        ///     TMDB 季信息缺失时返回空 → 全走 Fallback 索引。
        /// </summary>
        private static Dictionary<int, List<int>> AssignToSeasons(
            List<(int Id, DateTime? Date)> chain,
            List<(int SeasonNumber, DateTime? AirDate)> seasons)
        {
            var result = new Dictionary<int, List<int>>();
            var datedSeasons = seasons
                .Where(s => s.AirDate.HasValue)
                .OrderBy(s => s.AirDate.Value)
                .ToList();
            if (chain.Count == 0 || datedSeasons.Count == 0) return result;

            var prevTarget = datedSeasons[0].SeasonNumber;
            foreach (var (id, date) in chain)
            {
                int target;
                if (date.HasValue)
                {
                    target = datedSeasons
                        .OrderBy(s => Math.Abs((s.AirDate.Value - date.Value).Ticks))
                        .First().SeasonNumber;
                }
                else
                {
                    target = prevTarget;
                }

                if (!result.TryGetValue(target, out var list))
                {
                    list = new List<int>();
                    result[target] = list;
                }
                list.Add(id);
                prevTarget = target;
            }
            return result;
        }

        /// <summary>
        ///     获取条目索引, 带缓存 + 单飞保护。
        ///     命中 FranchiseCache 直接返回; 否则查/建构建 Task,
        ///     并发下同一 tmdbId 只构建一次。
        /// </summary>
        private async Task<FranchiseIndex> GetOrCreateFranchiseIndexAsync(BaseItem item, string tmdbId, string mediaType, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(tmdbId)) return null;

            if (FranchiseCache.TryGetValue(tmdbId, out var cached))
                return cached;

            var buildTask = FranchiseBuildTasks.GetOrAdd(tmdbId,
                _ => BuildFranchiseCoreAsync(item, tmdbId, mediaType));

            try
            {
                var result = await buildTask.ConfigureAwait(false);
                if (result != null)
                    FranchiseCache.TryAdd(tmdbId, result);
                return result;
            }
            finally
            {
                FranchiseBuildTasks.TryRemove(tmdbId, out _);
            }
        }

        private async Task<int?> SearchBangumiAsync(string keyword, int? targetYear = null, string mediaType = null)
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
                var candidates = BangumiApiClient.ParseSearchResults(json);
                if (candidates.Count == 0) return null;

                if (!targetYear.HasValue)
                {
                    logger.Debug("Bangumi 搜索: 无年份参考，使用第一条 id={0}", candidates[0].Id);
                    return candidates[0].Id;
                }

                var best = candidates
                    .Select(c => new
                    {
                        Candidate = c,
                        Score = ScoreBangumiCandidate(c, keyword, targetYear, mediaType)
                    })
                    .OrderByDescending(x => x.Score)
                    .First();

                logger.Debug(
                    "Bangumi 搜索: keyword={0}, 命中 id={1}, name={2}, year={3}, platform={4}, score={5}",
                    keyword,
                    best.Candidate.Id,
                    best.Candidate.Name ?? best.Candidate.NameCn ?? "(null)",
                    best.Candidate.Year?.ToString() ?? "(null)",
                    best.Candidate.Platform ?? "(null)",
                    best.Score);

                return best.Candidate.Id;
            }
            catch (Exception ex)
            {
                logger.Error("Bangumi 搜索异常: {0}", ex.Message);
                return null;
            }
        }

        private static int ScoreBangumiCandidate(
            BangumiApiClient.BangumiSearchCandidate candidate,
            string keyword,
            int? targetYear,
            string mediaType)
        {
            var score = 0;

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                if (string.Equals(candidate.Name, keyword, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(candidate.NameCn, keyword, StringComparison.OrdinalIgnoreCase))
                    score += 200;
            }

            if (targetYear.HasValue && candidate.Year.HasValue)
            {
                var diff = Math.Abs(candidate.Year.Value - targetYear.Value);
                if (diff == 0)
                    score += 50;
                else if (diff == 1)
                    score += 30;
                else
                    score -= diff * 10;
            }

            if (!string.IsNullOrWhiteSpace(mediaType) && !string.IsNullOrWhiteSpace(candidate.Platform))
            {
                var isMovie = string.Equals(mediaType, "movie", StringComparison.OrdinalIgnoreCase);
                var candidatePlatform = candidate.Platform;
                if (isMovie && string.Equals(candidatePlatform, "Movie", StringComparison.OrdinalIgnoreCase))
                    score += 50;
                else if (!isMovie && string.Equals(candidatePlatform, "TV", StringComparison.OrdinalIgnoreCase))
                    score += 50;
                else if (!isMovie && string.Equals(candidatePlatform, "Movie", StringComparison.OrdinalIgnoreCase))
                    score -= 80;
                else if (isMovie && string.Equals(candidatePlatform, "TV", StringComparison.OrdinalIgnoreCase))
                    score -= 80;
            }

            return score;
        }

        private async Task<List<(int Id, string NameJp, int Type, List<string> Actors, List<int> ActorIds)>> FetchCharacterListAsync(int subjectId)
        {
            try
            {
                var json = await GetBangumiJsonAsync(BangumiApiClient.BuildCharactersUrl(subjectId));
                return BangumiApiClient.ParseCharacterList(json);
            }
            catch (Exception ex)
            {
                logger.Error("Bangumi 获取角色列表异常: {0}", ex.Message);
                return new List<(int, string, int, List<string>, List<int>)>();
            }
        }

        /// <summary>
        ///     构建角色索引。结果按 charList 顺序合并(Bangumi 返回顺序 = 主角在前)，
        ///     保证同一声优对应多个角色时 ByActorList 顺序确定且主角优先。
        /// </summary>
        private async Task<SubjectIndex> BuildIndexesAsync(
            List<(int Id, string NameJp, int Type, List<string> Actors, List<int> ActorIds)> charList, string originalLanguage)
        {
            const int maxConcurrent = 8; // 限流并发: 同时最多 8 个角色详情/声优详情请求
            var idx = new SubjectIndex
            {
                ByEn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ByRoleName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ByActorList = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase),
                OriginalLanguage = originalLanguage
            };
            if (charList.Count == 0) return idx;

            // 主角(type=1)排前、配角次之; 同类型保持 Bangumi 原顺序(稳定排序)
            var ordered = charList
                .Select((c, i) => (c, i))
                .OrderBy(x => x.c.Type == 0 ? 1 : x.c.Type)
                .ThenBy(x => x.i)
                .Select(x => x.c)
                .ToList();

            var detailCount = 0;
            var actorDetailCount = 0;
            var results = new (string Chs, string En, string JpName, List<string> ActorNames)[ordered.Count];

            using var semaphore = new SemaphoreSlim(maxConcurrent);
            var tasks = ordered.Select(async (ch, position) =>
            {
                await semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    var (id, chName, _, actors, actorIds) = ch;

                    try
                    {
                        var dJson = await GetBangumiJsonAsync(BangumiApiClient.BuildCharacterDetailUrl(id), 5000);
                        var (chs, en) = BangumiApiClient.ParseCharacterDetail(dJson);
                        if (string.IsNullOrWhiteSpace(chs) && string.IsNullOrWhiteSpace(chName))
                            return;

                        Interlocked.Increment(ref detailCount);

                        var actorNames = new List<string>();
                        foreach (var a in actors)
                            if (!string.IsNullOrWhiteSpace(a) && !actorNames.Contains(a))
                                actorNames.Add(a);

                        for (var ai = 0; ai < actorIds.Count && ai < actors.Count; ai++)
                        {
                            try
                            {
                                var pJson = await GetBangumiJsonAsync(BangumiApiClient.BuildPersonDetailUrl(actorIds[ai]), 5000);
                                var aliases = BangumiApiClient.ParsePersonAliases(pJson);
                                foreach (var alias in aliases)
                                    if (!string.IsNullOrWhiteSpace(alias) && !actorNames.Contains(alias))
                                        actorNames.Add(alias);
                                Interlocked.Increment(ref actorDetailCount);
                            }
                            catch (Exception ex)
                            {
                                logger.Debug("Bangumi 角色增强: 获取声优详情失败 id={0}: {1}", actorIds[ai], ex.Message);
                            }
                        }

                        results[position] = (chs, en, chName, actorNames);
                    }
                    catch (Exception ex)
                    {
                        logger.Debug("Bangumi 角色增强: 获取角色详情失败 id={0}: {1}", id, ex.Message);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks).ConfigureAwait(false);

            // 顺序合并: 列表追加顺序 = Bangumi 角色顺序, 同一声优多角色时主角排前
            for (var position = 0; position < ordered.Count; position++)
            {
                var (chs, en, jpName, actorNames) = results[position];
                if (actorNames == null) continue;
                var displayName = !string.IsNullOrWhiteSpace(chs) ? chs
                    : !string.IsNullOrWhiteSpace(jpName) ? jpName : null;
                if (displayName == null) continue;

                if (!string.IsNullOrWhiteSpace(en) && !idx.ByEn.ContainsKey(en))
                    idx.ByEn[en] = displayName;

                // 角色名索引: TMDB role 直接精确命中, 治「声优反查抢错角色」(罗小黑战记 case)
                if (!string.IsNullOrWhiteSpace(jpName) && !idx.ByRoleName.ContainsKey(jpName))
                    idx.ByRoleName[jpName] = displayName;
                if (!string.IsNullOrWhiteSpace(chs) && !idx.ByRoleName.ContainsKey(chs))
                    idx.ByRoleName[chs] = displayName;

                foreach (var actor in actorNames)
                {
                    if (!idx.ByActorList.TryGetValue(actor, out var list))
                    {
                        list = new List<string>();
                        idx.ByActorList[actor] = list;
                    }
                    if (!list.Contains(displayName)) list.Add(displayName);
                }
            }

            logger.Debug("Bangumi 角色增强: 共获取 {0} 个角色详情({1}个声优详情), 共 {2} 个角色, by_actor={3}条, by_role={4}条",
                detailCount, actorDetailCount, ordered.Count, idx.ByActorList.Count, idx.ByRoleName.Count);
            return idx;
        }

        /// <summary>
        ///     匹配优先级(角色粒度兜底):
        ///     对每个角色, 先查 primary(本季索引), 单个没查到再用 fallback(全系列合并索引)查:
        ///     1. 角色英文名(别名) → 中文名
        ///     2. 角色名精确匹配(Bangumi 原名/简体中文名) → 中文名   ← 罗小黑战记这类国漫的主路径
        ///     3. TMDB 角色 → 声优反查: 该声优一个角色 → 直接用;
        ///        多个角色 → 按 Bangumi 顺序(主角在前)取前两个拼接, 例: 罗小黑/罗小白
        ///     命中值含假名(Bangumi 只有日文名)的交给调用方做 DeepL 翻译; 完全未命中的保留 TMDB 原文。
        /// </summary>
        private (int MatchCount, List<(PersonInfo Person, string JaName)> ToTranslate) MatchPeople(List<PersonInfo> people,
            SubjectIndex primary, SubjectIndex fallback,
            Dictionary<string, string> charToJapActor)
        {
            var toTranslate = new List<(PersonInfo, string)>();
            if (people == null || people.Count == 0) return (0, toTranslate);
            var matchCount = 0;
            var skipChinese = Plugin.Instance?.Options?.MetaData?.BangumiSkipExistingChinese == true;
            var useFallback = fallback != null && !ReferenceEquals(primary, fallback);

            foreach (var person in people.Where(p => p.Type == PersonType.Actor || p.Type == PersonType.GuestStar))
            {
                var role = (person.Role ?? "").Trim();
                var roleClean = role.Replace(" (voice)", "").Replace("(voice)", "").Trim();

                if (skipChinese && ContainsChinese(roleClean))
                    continue;

                string chsName = null;
                if (!string.IsNullOrWhiteSpace(roleClean))
                {
                    chsName = ResolveRoleName(roleClean, primary)
                              ?? (useFallback ? ResolveRoleName(roleClean, fallback) : null);
                }

                if (chsName == null &&
                    charToJapActor != null && !string.IsNullOrWhiteSpace(roleClean) &&
                    charToJapActor.TryGetValue(roleClean, out var japActor) &&
                    !string.IsNullOrWhiteSpace(japActor))
                {
                    var roleList = LookupActorRoles(japActor, primary)
                                   ?? (useFallback ? LookupActorRoles(japActor, fallback) : null);
                    if (roleList != null && roleList.Count > 0)
                    {
                        chsName = roleList.Count >= 2 ? roleList[0] + "/" + roleList[1] : roleList[0];
                    }
                }

                if (!string.IsNullOrWhiteSpace(chsName))
                {
                    if (ContainsKana(chsName))
                    {
                        // Bangumi 只有日文名 → 记下来走 DeepL 翻中文, 失败回退日文
                        toTranslate.Add((person, chsName));
                    }
                    else
                    {
                        if (person.Role != chsName) person.Role = chsName;
                        matchCount++;
                    }
                }
            }

            return (matchCount, toTranslate);
        }

        /// <summary>在单个索引里解析角色名 → 中文显示名(英文名别名 → 角色名精确匹配)。</summary>
        private static string ResolveRoleName(string role, SubjectIndex idx)
        {
            if (idx == null) return null;
            if (idx.ByEn != null && idx.ByEn.TryGetValue(role, out var byEnHit)) return byEnHit;
            if (idx.ByRoleName != null && idx.ByRoleName.TryGetValue(role, out var byRoleHit)) return byRoleHit;
            return null;
        }

        /// <summary>在单个索引里按声优名查有序角色列表(主角在前)。</summary>
        private static List<string> LookupActorRoles(string actor, SubjectIndex idx)
        {
            if (idx?.ByActorList != null && idx.ByActorList.TryGetValue(actor, out var list) && list.Count > 0)
                return list;
            return null;
        }

        /// <summary>日→中机翻缓存(进程级)。空串表示翻译失败, 同一个词只请求一次。</summary>
        private static readonly ConcurrentDictionary<string, string> TranslationCache
            = new ConcurrentDictionary<string, string>();

        private const string DeeplOneshotUrl = "https://oneshot-free.www.deepl.com/v1/translate";
        private const string DeeplApiFreeUrl = "https://api-free.deepl.com/v2/translate";

        /// <summary>内置默认反代地址(自建 CF Worker, 服务端持有 key, 走官方 api-free 配额)。</summary>
        private const string BuiltInProxyUrl = "https://translate.942695.xyz";

        /// <summary>内置 DeepL key(XOR 0x37 + Base64 混淆存储, 非明文)。</summary>
        private const string BuiltInDeeplKeyObfuscated = "AA5RBAQDDgYaAQNSARoDBQ8DGlYEVFYaBgQGBAEGAVICUQMADVFP";

        private static string GetDeeplKey()
        {
            var userKey = Plugin.Instance?.Options?.MetaData?.DeepLApiKey?.Trim();
            if (!string.IsNullOrWhiteSpace(userKey)) return userKey;

            try
            {
                var raw = Convert.FromBase64String(BuiltInDeeplKeyObfuscated);
                var chars = raw.Select(b => (char)(b ^ 0x37)).ToArray();
                var key = new string(chars);
                return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        ///     DeepL 官方 API 直连(api-free.deepl.com, 需网络可达; 家宽直连可能被干扰)。
        /// </summary>
        private async Task<string> TryDeeplApiAsync(string text, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return null;
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var req = new HttpRequestMessage(HttpMethod.Post, DeeplApiFreeUrl);
                req.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + apiKey);
                req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["text"] = text,
                    ["target_lang"] = "ZH-HANS",
                    ["source_lang"] = "JA"
                });

                using var resp = await client.SendAsync(req);
                var json = await resp.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("translations", out var trs) &&
                    trs.ValueKind == JsonValueKind.Array && trs.GetArrayLength() > 0 &&
                    trs[0].TryGetProperty("text", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                {
                    var translated = tEl.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(translated))
                    {
                        logger.Info("Bangumi 角色增强: 角色名机翻(官方API) {0} → {1}", text, translated);
                        return translated;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Debug("Bangumi 角色增强: DeepL 官方API机翻失败 {0}: {1}", text, ex.Message);
            }
            return null;
        }
        private const string DeeplAppVersion = "26.52";
        private const string DeeplAppBuild = "5443737";
        private static readonly string DeeplInstanceId = Guid.NewGuid().ToString();
        private static readonly string DeeplSessionId = Guid.NewGuid().ToString();
        private static HttpClient deeplHttpClient;
        private static readonly object DeeplInitLock = new object();

        private static bool ContainsKana(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (var c in text)
            {
                if (c >= 0x3040 && c <= 0x30FF) return true; // 平假名 + 片假名
                if (c >= 0x31F0 && c <= 0x31FF) return true; // 片假名音标扩展
                if (c >= 0xFF66 && c <= 0xFF9D) return true; // 半角片假名
            }
            return false;
        }

        /// <summary>
        ///     Bangumi 只有日文名的角色 → DeepL 免费接口(oneshot-free, 无 key, 国内直连可用)翻成中文;
        ///     翻译失败回退日文名。只改角色名(person.Role), 绝不动声优名。
        /// </summary>
        private async Task<int> TranslateKanaNamesAsync(List<(PersonInfo Person, string JaName)> toTranslate)
        {
            if (toTranslate == null || toTranslate.Count == 0) return 0;
            var count = 0;
            foreach (var item in toTranslate)
            {
                var translated = await TranslateJaToZhAsync(item.JaName);
                var final = string.IsNullOrWhiteSpace(translated) ? item.JaName : translated;
                if (item.Person.Role != final)
                {
                    logger.Info("Bangumi 角色增强: 日文角色名 {0} → {1}",
                        item.JaName, string.IsNullOrWhiteSpace(translated) ? "(翻译失败, 保留日文)" : final);
                    item.Person.Role = final;
                }
                count++;
            }
            return count;
        }

        private HttpClient GetDeeplHttpClient()
        {
            if (deeplHttpClient != null) return deeplHttpClient;
            lock (DeeplInitLock)
            {
                if (deeplHttpClient == null)
                {
                    // 独立 client: 自带 CookieContainer(预热 www.deepl.com 的 cookie 对 oneshot 必需)
                    var handler = new HttpClientHandler
                    {
                        UseCookies = true,
                        CookieContainer = new CookieContainer(),
                        AutomaticDecompression = DecompressionMethods.All
                    };
                    deeplHttpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
                    deeplHttpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                        "User-Agent", "DeepL/" + DeeplAppVersion + " CFNetwork/3826.600.41 Darwin/25.0.0");
                    deeplClientWarm = false;
                }
            }
            return deeplHttpClient;
        }

        private static bool deeplClientWarm;

        /// <summary>
        ///     角色名日→中机翻: 全程 DeepL, 目标恒为 zh-Hans。
        ///     链路: 机翻接口地址(内置默认反代, CF 出口稳定) → 直连 DeepL 官方 API(key 可自填, 留空用内置 key)
        ///           → DeepL oneshot 匿名兜底。全部失败返回 null, 调用方回退日文名。
        ///     进程级缓存: 成功与失败都缓存, 同一个词只请求一次。
        /// </summary>
        private async Task<string> TranslateJaToZhAsync(string text)
        {
            if (TranslationCache.TryGetValue(text, out var cached))
                return string.IsNullOrEmpty(cached) ? null : cached;

            // 默认反代地址: 内置 worker(服务端持有 key, 走官方 api-free 配额)
            var apiBase = Plugin.Instance?.Options?.MetaData?.TranslateApiBaseUrl?.Trim();
            if (string.IsNullOrWhiteSpace(apiBase))
                apiBase = BuiltInProxyUrl;

            var translated = await TryProxyGtxAsync(text, apiBase)
                             ?? await TryDeeplApiAsync(text, GetDeeplKey())
                             ?? await TryDeeplOneshotAsync(text);

            TranslationCache[text] = translated ?? "";
            return string.IsNullOrWhiteSpace(translated) ? null : translated;
        }

        /// <summary>自建反代路径: gtx 格式 GET, 反代内部转 DeepL oneshot, 返回 gtx 形状 JSON。</summary>
        private async Task<string> TryProxyGtxAsync(string text, string apiBase)
        {
            try
            {
                if (!apiBase.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !apiBase.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    apiBase = "https://" + apiBase;

                var url = apiBase.TrimEnd('/') +
                          "/translate_a/single?client=gtx&sl=ja&tl=zh-CN&dt=t&q=" +
                          Uri.EscapeDataString(text);
                var opts = new HttpRequestOptions
                {
                    Url = url,
                    AcceptHeader = "application/json",
                    UserAgent = "MediaInfoKeeper/1.0 (Bangumi)",
                    TimeoutMs = 20000
                };
                using var resp = await Task.Run(() => Plugin.SharedHttpClient.SendAsync(opts, "GET"));
                using var reader = new StreamReader(resp.Content);
                var json = await reader.ReadToEndAsync();

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array &&
                    doc.RootElement.GetArrayLength() > 0 &&
                    doc.RootElement[0].ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var seg in doc.RootElement[0].EnumerateArray())
                    {
                        if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 &&
                            seg[0].ValueKind == JsonValueKind.String)
                            sb.Append(seg[0].GetString());
                    }
                    var translated = sb.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(translated))
                    {
                        logger.Info("Bangumi 角色增强: 角色名机翻(反代) {0} → {1}", text, translated);
                        return translated;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Debug("Bangumi 角色增强: 反代机翻失败 {0}: {1}", text, ex.Message);
            }
            return null;
        }

        /// <summary>
        ///     DeepL oneshot 免费接口直连(DeepL iOS 客户端同款请求, DeepLX 同款配方):
        ///     先 GET www.deepl.com/translator 种 cookie, 再 POST oneshot-free。
        ///     无 key、国内直连可用(高峰期可能 429); 失败返回 null。
        /// </summary>
        private async Task<string> TryDeeplOneshotAsync(string text)
        {
            try
            {
                var client = GetDeeplHttpClient();
                if (!deeplClientWarm)
                {
                    // 预热: 冷访问 www.deepl.com 拿 userCountry/verifiedBot cookie, 失败不阻塞
                    try
                    {
                        using var warm = new HttpRequestMessage(HttpMethod.Get, "https://www.deepl.com/translator");
                        await client.SendAsync(warm);
                    }
                    catch { }
                    deeplClientWarm = true;
                }

                var payload = new
                {
                    text = new[] { text },
                    target_lang = "zh-Hans",
                    source_lang = "ja",
                    usage_type = "translate",
                    app_information = new
                    {
                        os = "iOS",
                        os_version = "26.0",
                        app_version = DeeplAppVersion,
                        app_build = DeeplAppBuild,
                        instance_id = DeeplInstanceId
                    }
                };
                var body = JsonSerializer.Serialize(payload);

                using var req = new HttpRequestMessage(HttpMethod.Post, DeeplOneshotUrl);
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                // LoginNone: 匿名请求带字面量 "None", 走对的服务端分支
                req.Headers.TryAddWithoutValidation("Authorization", "None");
                req.Headers.TryAddWithoutValidation("x-app-os-version", "26.0");
                req.Headers.TryAddWithoutValidation("x-app-instance-id", DeeplInstanceId);
                req.Headers.TryAddWithoutValidation("x-app-session-id", DeeplSessionId);

                using var resp = await client.SendAsync(req);
                var json = await resp.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("translations", out var trs) &&
                    trs.ValueKind == JsonValueKind.Array && trs.GetArrayLength() > 0 &&
                    trs[0].TryGetProperty("text", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                {
                    var translated = tEl.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(translated))
                    {
                        logger.Info("Bangumi 角色增强: 角色名机翻(直连) {0} → {1}", text, translated);
                        return translated;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Debug("Bangumi 角色增强: DeepL 直连机翻失败 {0}: {1}", text, ex.Message);
            }
            return null;
        }

        /// <summary>
        ///     幕后人员(Director/Writer/Producer 等)职务词硬编码映射成中文。
        ///     例: Director → 导演、Producer → 制片人、Series Composition → 系列构成。
        ///     不查 Bangumi，纯静态表；只改 Role 显示词，Type 不动（导演/编剧栏分组不变）。
        /// </summary>
        private static int ApplyCrewJobChinese(List<PersonInfo> people)
        {
            if (people == null || people.Count == 0) return 0;
            var count = 0;
            foreach (var p in people)
            {
                if (p.Type == PersonType.Actor || p.Type == PersonType.GuestStar) continue;
                var role = (p.Role ?? "").Trim();
                if (string.IsNullOrWhiteSpace(role)) continue;
                if (CrewJobZh.TryGetValue(role, out var zh) && !string.Equals(role, zh, StringComparison.Ordinal))
                {
                    p.Role = zh;
                    count++;
                }
            }
            return count;
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
