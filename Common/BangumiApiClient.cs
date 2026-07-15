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

        public static string GetApiBase()
        {
            return BaseUrl;
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

        public static int? ParseSearchResult(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data");
                if (data.GetArrayLength() == 0) return null;
                return data[0].GetProperty("id").GetInt32();
            }
            catch { return null; }
        }

        private static string S(JsonElement e, string p)
            => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
