using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaInfoKeeper.Common;

namespace MediaInfoKeeper.Services {
    internal static class IntroDbService {
        private const string DefaultBaseUrl = "https://api.introdb.app";
        private const string MarkerCacheScope = "introdb-markers";

        private static readonly JsonSerializerOptions JsonOptions = new() {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromHours(6);

        public static async Task<MarkerLookupResult> GetMarkersAsync(Episode episode,
            CancellationToken cancellationToken) {
            if (!TryBuildIdentity(episode, out var identity, out var reason)) return NotFound(reason);

            var cachedResult = PluginDiskCache.GetJson<MarkerLookupResult>(
                MarkerCacheScope,
                identity.CacheKey,
                SuccessCacheDuration,
                JsonOptions);
            if (cachedResult != null) return NotFound("cache hit");

            var httpClient = Plugin.SharedHttpClient;
            if (httpClient == null) return NotFound("IHttpClient unavailable");

            var requestUrl = BuildApiUrl("segments") +
                             "?imdb_id=" + Uri.EscapeDataString(identity.ImdbId) +
                             "&season=" + identity.Season +
                             "&episode=" + identity.Episode;
            var detail = FormatItemForLog(episode);
            try {
                var requestOptions = new HttpRequestOptions {
                    Url = requestUrl,
                    CancellationToken = cancellationToken,
                    AcceptHeader = "application/json",
                    UserAgent = "MediaInfoKeeper",
                    EnableDefaultUserAgent = false,
                    TimeoutMs = 10000,
                    CacheMode = CacheMode.None,
                    ThrowOnErrorResponse = false
                };

                using var response = await httpClient.SendAsync(requestOptions, "GET").ConfigureAwait(false);
                var body = await ReadResponseBodyAsync(response).ConfigureAwait(false);
                var statusCode = (int)response.StatusCode;
                if (statusCode == 404) {
                    var notFoundResult = NotFound("404 Not Found");
                    PluginDiskCache.SetJson(MarkerCacheScope, identity.CacheKey, notFoundResult, JsonOptions);
                    return notFoundResult;
                }

                if (statusCode == 429) {
                    Plugin.SharedLogger?.Info("IntroDB 请求达到限制: {0}", detail);
                    return new MarkerLookupResult {
                        Found = false,
                        RateLimited = true,
                        Reason = "rate limited"
                    };
                }

                if (statusCode < 200 || statusCode >= 300) {
                    Plugin.SharedLogger?.Info("IntroDB 请求失败: status={0}, {1}, body={2}", statusCode, detail, body);
                    return NotFound("http " + statusCode);
                }

                var segments = JsonSerializer.Deserialize<SegmentsResponse>(body, JsonOptions);
                var intro = GetValidIntro(segments?.Intro, episode);
                var creditsStartTicks = GetValidCreditsStart(segments?.Outro, episode);
                if (intro == null && !creditsStartTicks.HasValue) {
                    var notFoundResult = NotFound("no usable segment");
                    PluginDiskCache.SetJson(MarkerCacheScope, identity.CacheKey, notFoundResult, JsonOptions);
                    return notFoundResult;
                }

                var result = new MarkerLookupResult {
                    Found = true,
                    IntroStartTicks = intro?.StartTicks,
                    IntroEndTicks = intro?.EndTicks,
                    CreditsStartTicks = creditsStartTicks
                };
                PluginDiskCache.SetJson(MarkerCacheScope, identity.CacheKey, result, JsonOptions);
                LogHit(detail, result);
                return result;
            }
            catch (OperationCanceledException) {
                throw;
            }
            catch (Exception ex) {
                Plugin.SharedLogger?.Info("IntroDB 查询异常: {0}, {1}", detail, ex.Message);
                Plugin.SharedLogger?.Debug(ex.StackTrace);
                return NotFound(ex.Message);
            }
        }

        public static async Task<MarkerSubmitResult> SubmitMarkersAsync(Episode episode,
            CancellationToken cancellationToken) {
            if (!TryBuildIdentity(episode, out var identity, out var reason)) return SubmitSkipped(reason);

            var apiKey = Plugin.Instance?.Options?.MetaData?.ScrapersEditor?.IntroDb?.ApiKey?.Trim();
            if (string.IsNullOrWhiteSpace(apiKey)) return SubmitSkipped("missing api key");

            var chapters = Plugin.IntroSkipChapterApi.GetChapters(episode);
            var introStartTicks = GetSubmitMarkerTicks(chapters, MarkerType.IntroStart);
            var introEndTicks = GetSubmitMarkerTicks(chapters, MarkerType.IntroEnd);
            var creditsStartTicks = GetSubmitMarkerTicks(chapters, MarkerType.CreditsStart);
            var submittedSegments = 0;
            MarkerSubmitResult lastFailure = null;

            if (introEndTicks.HasValue) {
                var startTicks = Math.Max(0, introStartTicks ?? 0);
                if (IsValidRange(episode, startTicks, introEndTicks.Value)) {
                    var introResult = await SubmitSegmentAsync(new SubmissionRequest {
                        SegmentType = "intro",
                        ImdbId = identity.ImdbId,
                        Season = identity.Season,
                        Episode = identity.Episode,
                        StartSeconds = TicksToSeconds(startTicks),
                        EndSeconds = TicksToSeconds(introEndTicks.Value),
                        TvdbId = identity.TvdbId,
                        TmdbId = identity.TmdbId
                    }, episode, apiKey, cancellationToken).ConfigureAwait(false);
                    if (introResult.Succeeded)
                        submittedSegments++;
                    else if (!introResult.Skipped)
                        lastFailure = introResult;
                }
            }

            if (creditsStartTicks.HasValue &&
                episode.RunTimeTicks.HasValue &&
                IsValidRange(episode, creditsStartTicks.Value, episode.RunTimeTicks.Value)) {
                var outroResult = await SubmitSegmentAsync(new SubmissionRequest {
                    SegmentType = "outro",
                    ImdbId = identity.ImdbId,
                    Season = identity.Season,
                    Episode = identity.Episode,
                    StartSeconds = TicksToSeconds(creditsStartTicks.Value),
                    EndSeconds = TicksToSeconds(episode.RunTimeTicks.Value),
                    TvdbId = identity.TvdbId,
                    TmdbId = identity.TmdbId
                }, episode, apiKey, cancellationToken).ConfigureAwait(false);
                if (outroResult.Succeeded)
                    submittedSegments++;
                else if (!outroResult.Skipped)
                    lastFailure = outroResult;
            }

            if (submittedSegments > 0) InvalidateMarkerCache(identity, episode);

            if (lastFailure != null) {
                lastFailure.SubmittedSegments = submittedSegments;
                return lastFailure;
            }

            return submittedSegments > 0
                ? new MarkerSubmitResult {
                    Succeeded = true,
                    SubmittedSegments = submittedSegments
                }
                : SubmitSkipped("no valid intro or credits markers");
        }

        internal static string FormatItemForLog(Episode episode) {
            if (episode == null) return "<unknown>";

            var seriesName = episode.FindSeriesName();
            return
                $"{(string.IsNullOrWhiteSpace(seriesName) ? "<unknown>" : seriesName.Trim())} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}";
        }

        private static async Task<MarkerSubmitResult> SubmitSegmentAsync(
            SubmissionRequest submission,
            Episode episode,
            string apiKey,
            CancellationToken cancellationToken) {
            var httpClient = Plugin.SharedHttpClient;
            if (httpClient == null) return SubmitSkipped("IHttpClient unavailable");

            var detail = FormatItemForLog(episode);
            var bodyJson = JsonSerializer.Serialize(submission, JsonOptions);
            try {
                var requestOptions = new HttpRequestOptions {
                    Url = BuildApiUrl("submit"),
                    CancellationToken = cancellationToken,
                    AcceptHeader = "application/json",
                    RequestContent = bodyJson.AsMemory(),
                    RequestContentType = "application/json",
                    UserAgent = "MediaInfoKeeper",
                    EnableDefaultUserAgent = false,
                    TimeoutMs = 10000,
                    ThrowOnErrorResponse = false
                };
                requestOptions.RequestHeaders["X-API-Key"] = apiKey;

                using var response = await httpClient.SendAsync(requestOptions, "POST").ConfigureAwait(false);
                var responseBody = await ReadResponseBodyAsync(response).ConfigureAwait(false);
                var statusCode = (int)response.StatusCode;
                if (statusCode >= 200 && statusCode < 300) {
                    Plugin.SharedLogger?.Info("IntroDB 上报成功: {0}, segment={1}", detail,
                        submission.SegmentType);
                    return new MarkerSubmitResult {
                        Succeeded = true,
                        SubmittedSegments = 1
                    };
                }

                if (statusCode == 429) {
                    Plugin.SharedLogger?.Info("IntroDB 上报达到限制: {0}, segment={1}, body={2}", detail,
                        submission.SegmentType, responseBody);
                    return new MarkerSubmitResult {
                        Succeeded = false,
                        RateLimited = true,
                        Reason = "rate limited"
                    };
                }

                Plugin.SharedLogger?.Info("IntroDB 上报失败: status={0}, {1}, segment={2}, body={3}", statusCode,
                    detail, submission.SegmentType, responseBody);
                return new MarkerSubmitResult {
                    Succeeded = false,
                    Reason = "http " + statusCode
                };
            }
            catch (OperationCanceledException) {
                throw;
            }
            catch (Exception ex) {
                Plugin.SharedLogger?.Info("IntroDB 上报异常: {0}, segment={1}, {2}", detail,
                    submission.SegmentType, ex.Message);
                Plugin.SharedLogger?.Debug(ex.StackTrace);
                return new MarkerSubmitResult {
                    Succeeded = false,
                    Reason = ex.Message
                };
            }
        }

        private static async Task<string> ReadResponseBodyAsync(HttpResponseInfo response) {
            if (response?.Content == null) return null;

            using var reader = new StreamReader(response.Content);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        private static bool TryBuildIdentity(Episode episode, out EpisodeIdentity identity, out string reason) {
            identity = null;
            reason = null;
            if (episode == null) {
                reason = "empty episode";
                return false;
            }

            var imdbId = NormalizeImdbId(episode.Series?.GetProviderId(MetadataProviders.Imdb.ToString()));
            if (imdbId == null) {
                reason = "missing series imdb id";
                return false;
            }

            if (!episode.ParentIndexNumber.HasValue || episode.ParentIndexNumber.Value < 1 ||
                !episode.IndexNumber.HasValue || episode.IndexNumber.Value < 1) {
                reason = "missing or invalid season or episode number";
                return false;
            }

            identity = new EpisodeIdentity {
                ImdbId = imdbId,
                Season = episode.ParentIndexNumber.Value,
                Episode = episode.IndexNumber.Value,
                TvdbId = ParsePositiveInt(episode.Series?.GetProviderId(MetadataProviders.Tvdb.ToString())),
                TmdbId = ParsePositiveInt(episode.Series?.GetProviderId(MetadataProviders.Tmdb.ToString()))
            };
            return true;
        }

        private static long? GetSubmitMarkerTicks(IEnumerable<ChapterInfo> chapters, MarkerType markerType) {
            if (chapters == null) return null;

            foreach (var chapter in chapters) {
                if (chapter?.MarkerType != markerType || IntroDbMarkerSource.IsProviderMarker(chapter)) continue;

                return chapter.StartPositionTicks;
            }

            return null;
        }

        private static bool IsValidRange(BaseItem item, long startTicks, long endTicks) {
            return startTicks >= 0 &&
                   endTicks > startTicks &&
                   (!item.RunTimeTicks.HasValue || endTicks <= item.RunTimeTicks.Value);
        }

        private static IntroSegment GetValidIntro(SegmentTimestamp segment, BaseItem item) {
            if (segment == null || segment.StartMs < 0 || segment.EndMs <= segment.StartMs) return null;

            var startTicks = segment.StartMs * TimeSpan.TicksPerMillisecond;
            var endTicks = segment.EndMs * TimeSpan.TicksPerMillisecond;
            return IsValidRange(item, startTicks, endTicks)
                ? new IntroSegment {
                    StartTicks = startTicks,
                    EndTicks = endTicks
                }
                : null;
        }

        private static long? GetValidCreditsStart(SegmentTimestamp segment, BaseItem item) {
            if (segment == null || segment.StartMs <= 0 || segment.EndMs <= segment.StartMs) return null;

            var startTicks = segment.StartMs * TimeSpan.TicksPerMillisecond;
            return item?.RunTimeTicks.HasValue == true && startTicks >= item.RunTimeTicks.Value
                ? null
                : startTicks;
        }

        private static int? ParsePositiveInt(string value) {
            return int.TryParse(value, out var number) && number > 0 ? number : null;
        }

        private static string NormalizeImdbId(string imdbId) {
            imdbId = imdbId?.Trim();
            if (string.IsNullOrWhiteSpace(imdbId) ||
                imdbId.Length < 9 ||
                imdbId.Length > 10 ||
                !imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                return null;

            for (var i = 2; i < imdbId.Length; i++)
                if (!char.IsDigit(imdbId[i]))
                    return null;

            return imdbId.ToLowerInvariant();
        }

        private static double TicksToSeconds(long ticks) {
            return Math.Round(ticks / (double)TimeSpan.TicksPerSecond, 3);
        }

        private static string BuildApiUrl(string endpoint) {
            var configuredBaseUrl = Plugin.Instance?.Options?.MetaData?.ScrapersEditor?.IntroDb?.BaseUrl;
            return (string.IsNullOrWhiteSpace(configuredBaseUrl) ? DefaultBaseUrl : configuredBaseUrl.Trim())
                .TrimEnd('/') + "/" + endpoint;
        }

        private static void InvalidateMarkerCache(EpisodeIdentity identity, Episode episode) {
            PluginDiskCache.Remove(MarkerCacheScope, identity.CacheKey, ".json");
            Plugin.SharedLogger?.Debug("IntroDB 查询缓存已失效: {0}", FormatItemForLog(episode));
        }

        private static void LogHit(string detail, MarkerLookupResult result) {
            Plugin.SharedLogger?.Info(
                "IntroDB 命中: {0} intro={1}-{2}, creditsStart={3}",
                detail,
                FormatTicks(result.IntroStartTicks),
                FormatTicks(result.IntroEndTicks),
                FormatTicks(result.CreditsStartTicks));
        }

        private static string FormatTicks(long? ticks) {
            return ticks.HasValue ? new TimeSpan(ticks.Value).ToString(@"hh\:mm\:ss\.fff") : "<none>";
        }

        private static MarkerLookupResult NotFound(string reason) {
            return new MarkerLookupResult {
                Found = false,
                Reason = reason
            };
        }

        private static MarkerSubmitResult SubmitSkipped(string reason) {
            return new MarkerSubmitResult {
                Succeeded = false,
                Skipped = true,
                Reason = reason
            };
        }

        internal sealed class MarkerLookupResult {
            public bool Found { get; set; }

            public bool RateLimited { get; set; }

            public string Reason { get; set; }

            public long? IntroStartTicks { get; set; }

            public long? IntroEndTicks { get; set; }

            public long? CreditsStartTicks { get; set; }
        }

        internal sealed class MarkerSubmitResult {
            public bool Succeeded { get; set; }

            public bool Skipped { get; set; }

            public bool RateLimited { get; set; }

            public string Reason { get; set; }

            public int SubmittedSegments { get; set; }
        }

        private sealed class EpisodeIdentity {
            public string ImdbId { get; set; }

            public int Season { get; set; }

            public int Episode { get; set; }

            public int? TvdbId { get; set; }

            public int? TmdbId { get; set; }

            public string CacheKey => $"{ImdbId}&season={Season}&episode={Episode}";
        }

        private sealed class SegmentsResponse {
            public SegmentTimestamp Intro { get; set; }

            public SegmentTimestamp Recap { get; set; }

            public SegmentTimestamp Outro { get; set; }
        }

        private sealed class SegmentTimestamp {
            [JsonPropertyName("start_ms")] public long StartMs { get; set; }

            [JsonPropertyName("end_ms")] public long EndMs { get; set; }
        }

        private sealed class IntroSegment {
            public long StartTicks { get; set; }

            public long EndTicks { get; set; }
        }

        private sealed class SubmissionRequest {
            [JsonPropertyName("segment_type")] public string SegmentType { get; set; }

            [JsonPropertyName("imdb_id")] public string ImdbId { get; set; }

            [JsonPropertyName("season")] public int Season { get; set; }

            [JsonPropertyName("episode")] public int Episode { get; set; }

            [JsonPropertyName("start_sec")] public double StartSeconds { get; set; }

            [JsonPropertyName("end_sec")] public double EndSeconds { get; set; }

            [JsonPropertyName("tvdb_id")] public int? TvdbId { get; set; }

            [JsonPropertyName("tmdb_id")] public int? TmdbId { get; set; }
        }
    }
}
