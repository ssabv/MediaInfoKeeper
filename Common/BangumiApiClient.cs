using System;
using System.Collections.Generic;
using System.Text.Json;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Common
{
    internal static class BangumiApiClient
    {
        private const string BaseUrl = "https://api.bgm.tv";

        public static string BuildSearchBody(string keyword)
        {
            return JsonSerializer.Serialize(new { keyword, filter = new { type = new[] { 2 } } });
        }

        public sealed class BangumiSearchCandidate
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string NameCn { get; set; }
            public int? Year { get; set; }
            public int? EpisodeCount { get; set; }
        }

        public static string GetApiBase()
        {
            var apiBase = Plugin.Instance?.Options?.MetaData?.BangumiApiBaseUrl;
            if (string.IsNullOrWhiteSpace(apiBase)) apiBase = BaseUrl;
            return apiBase.TrimEnd('/');
        }

        public static string GetSearchUrl()
        {
            return GetApiBase() + "/v0/search/subjects";
        }

        public static string BuildCharactersUrl(int subjectId)
        {
            return GetApiBase() + $"/v0/subjects/{subjectId}/characters";
        }

        public static string BuildCharacterDetailUrl(int characterId)
        {
            return GetApiBase() + $"/v0/characters/{characterId}";
        }

        public static string BuildPersonDetailUrl(int personId)
        {
            return GetApiBase() + $"/v0/persons/{personId}";
        }

        public static List<(int Id, string NameJp, List<string> Actors, List<int> ActorIds)> ParseCharacterList(string json)
        {
            var result = new List<(int, string, List<string>, List<int>)>();
            if (string.IsNullOrWhiteSpace(json)) return result;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                JsonElement data;
                if (root.ValueKind == JsonValueKind.Array)
                    data = root;
                else if (!root.TryGetProperty("data", out data) || data.ValueKind != JsonValueKind.Array)
                    return result;

                foreach (var ch in data.EnumerateArray())
                {
                    var id = ch.GetProperty("id").GetInt32();
                    var name = S(ch, "name") ?? "";
                    var actors = new List<string>();
                    var actorIds = new List<int>();
                    if (ch.TryGetProperty("actors", out var acts) && acts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var a in acts.EnumerateArray())
                        {
                            if (a.ValueKind == JsonValueKind.String)
                            {
                                actors.Add(a.GetString());
                            }
                            else if (a.ValueKind == JsonValueKind.Object)
                            {
                                if (a.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                                    actors.Add(nm.GetString());
                                if (a.TryGetProperty("id", out var pid) && pid.ValueKind == JsonValueKind.Number)
                                    actorIds.Add(pid.GetInt32());
                            }
                        }
                    }
                    result.Add((id, name, actors, actorIds));
                }
            }
            catch { }
            return result;
        }

        public static (string Chs, string En) ParseCharacterDetail(string json)
        {
            string chs = null, en = null;
            if (string.IsNullOrWhiteSpace(json)) return (null, null);
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("infobox", out var ib) && ib.ValueKind == JsonValueKind.Array)
                {
                    foreach (var kv in ib.EnumerateArray())
                    {
                        var key = S(kv, "key");
                        if (key == "简体中文名")
                        {
                            chs = S(kv, "value");
                        }
                        if (key == "别名")
                        {
                            if (kv.TryGetProperty("value", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var alias in aliases.EnumerateArray())
                                {
                                    if (S(alias, "k") == "英文名")
                                        en = S(alias, "v");
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return (chs, en);
        }

        public static List<string> ParsePersonAliases(string json)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(json)) return result;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("infobox", out var ib) && ib.ValueKind == JsonValueKind.Array)
                {
                    foreach (var kv in ib.EnumerateArray())
                    {
                        var key = S(kv, "key");
                        if (key == "简体中文名")
                        {
                            var v = S(kv, "value");
                            if (!string.IsNullOrWhiteSpace(v)) result.Add(v);
                        }
                        else if (key == "别名")
                        {
                            if (kv.TryGetProperty("value", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var alias in aliases.EnumerateArray())
                                {
                                    if (alias.ValueKind == JsonValueKind.Object && !alias.TryGetProperty("k", out _))
                                    {
                                        var v = S(alias, "v");
                                        if (!string.IsNullOrWhiteSpace(v) && !result.Contains(v))
                                            result.Add(v);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        public static List<BangumiSearchCandidate> ParseSearchResults(string json)
        {
            var result = new List<BangumiSearchCandidate>();
            if (string.IsNullOrWhiteSpace(json)) return result;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                {
                    return result;
                }

                foreach (var item in data.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                        continue;

                    var candidate = new BangumiSearchCandidate
                    {
                        Id = idEl.GetInt32(),
                        Name = S(item, "name"),
                        NameCn = S(item, "name_cn")
                    };

                    if (item.TryGetProperty("date", out var dateEl) && dateEl.ValueKind == JsonValueKind.String)
                    {
                        var date = dateEl.GetString();
                        if (!string.IsNullOrWhiteSpace(date) && date.Length >= 4 &&
                            int.TryParse(date.Substring(0, 4), out var year))
                        {
                            candidate.Year = year;
                        }
                    }

                    if (item.TryGetProperty("eps", out var epsEl) && epsEl.ValueKind == JsonValueKind.Number)
                    {
                        candidate.EpisodeCount = epsEl.GetInt32();
                    }

                    result.Add(candidate);
                }
            }
            catch { }

            return result;
        }

        public static int? ParseSearchResult(string json)
        {
            var results = ParseSearchResults(json);
            return results.Count > 0 ? results[0].Id : (int?)null;
        }

        private static string S(JsonElement e, string p)
            => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
