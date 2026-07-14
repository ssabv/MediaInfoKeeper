namespace MediaInfoKeeper.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Net;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Emby.Web.GenericEdit.Elements;
    using MediaInfoKeeper.Options;

    internal static class NetworkProbe
    {
        private static readonly TimeSpan ProxyProbeTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan TmdbProbeTimeout = TimeSpan.FromSeconds(5);
        private const string ProxyProbeUrl1 = "http://www.gstatic.com/generate_204";
        private const string ProxyProbeUrl2 = "http://www.google.com/generate_204";
        private const string DefaultTmdbApiBaseUrl = "https://api.themoviedb.org";
        private const string DefaultTmdbImageBaseUrl = "https://image.tmdb.org";
        private const string TmdbImageProbePath = "/t/p/w92/wwemzKWzjKYJFfCeiB57q3r4Bcm.png";

        internal sealed class Result
        {
            public ItemStatus Status { get; set; }

            public string Caption { get; set; }

            public string StatusText { get; set; }
        }

        public static Task<Result> RunProxyLatencyAsync(NetWorkOptions options)
        {
            if (options == null)
            {
                return Task.FromResult(Build(ItemStatus.Unavailable, "不可用", "N/A"));
            }

            if (!options.EnableProxyServer)
            {
                return Task.FromResult(Build(ItemStatus.Unavailable, "不可用", "N/A"));
            }

            if (!TryParseProxyEndpoint(options.ProxyServerUrl, out var scheme, out var host, out var port, out var username, out var password))
            {
                return Task.FromResult(Build(ItemStatus.Unavailable, "不可用", "N/A"));
            }

            return ProbeProxyCoreAsync(scheme, host, port, username, password, options.IgnoreCertificateValidation);
        }

        public static async Task<Result> RunTmdbAltAsync(NetWorkOptions options)
        {
            if (options == null)
            {
                return Build(ItemStatus.Unavailable, "不可用", "N/A");
            }

            var hasApiOverride = !string.IsNullOrWhiteSpace(options.AlternativeTmdbApiUrl) ||
                                 !string.IsNullOrWhiteSpace(options.AlternativeTmdbApiKey);
            var hasImageOverride = !string.IsNullOrWhiteSpace(options.AlternativeTmdbImageUrl);
            if (!hasApiOverride && !hasImageOverride)
            {
                return Build(ItemStatus.Unavailable, "未启用", "N/A");
            }

            var statusLines = new List<string>(2);
            var hasFailure = false;
            var hasSuccess = false;

            if (hasApiOverride)
            {
                var apiBaseUrl = NormalizeBaseUrl(options.AlternativeTmdbApiUrl, DefaultTmdbApiBaseUrl);
                var apiResult = await ProbeTmdbApiAsync(apiBaseUrl, options.AlternativeTmdbApiKey).ConfigureAwait(false);
                hasSuccess |= apiResult.Succeeded;
                hasFailure |= !apiResult.Succeeded;
                statusLines.Add("API: " + apiResult.StatusText);
            }

            if (hasImageOverride)
            {
                var imageBaseUrl = NormalizeBaseUrl(options.AlternativeTmdbImageUrl, DefaultTmdbImageBaseUrl);
                var imageResult = await ProbeTmdbImageAsync(imageBaseUrl).ConfigureAwait(false);
                hasSuccess |= imageResult.Succeeded;
                hasFailure |= !imageResult.Succeeded;
                statusLines.Add("Image: " + imageResult.StatusText);
            }

            var statusText = string.Join("\n", statusLines);
            if (hasFailure && hasSuccess)
            {
                return Build(ItemStatus.Warning, "部分可用", statusText);
            }

            if (hasSuccess)
            {
                return Build(ItemStatus.Succeeded, "可用", statusText);
            }

            return Build(ItemStatus.Unavailable, "不可用", string.IsNullOrEmpty(statusText) ? "N/A" : statusText);
        }

        private static async Task<Result> ProbeProxyCoreAsync(
            string scheme,
            string host,
            int port,
            string username,
            string password,
            bool ignoreCertificateValidation)
        {
            try
            {
                var proxyUrl = new UriBuilder(scheme, host, port).Uri;
                using var handler = new HttpClientHandler();
                handler.Proxy = new WebProxy(proxyUrl)
                {
                    Credentials = !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password)
                        ? new NetworkCredential(username, password)
                        : null
                };
                handler.UseProxy = true;
                if (ignoreCertificateValidation)
                {
                    handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
                }

                using var client = new HttpClient(handler)
                {
                    Timeout = ProxyProbeTimeout
                };

                var task1 = ProbeProxyUrlAsync(client, ProxyProbeUrl1);
                var task2 = ProbeProxyUrlAsync(client, ProxyProbeUrl2);

                var stopwatch = Stopwatch.StartNew();
                var first = await Task.WhenAny(task1, task2).ConfigureAwait(false);
                if (await first.ConfigureAwait(false) ||
                    await (first == task1 ? task2 : task1).ConfigureAwait(false))
                {
                    stopwatch.Stop();
                    return Build(
                        ItemStatus.Succeeded,
                        "可用",
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} ms",
                            stopwatch.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)));
                }
            }
            catch
            {
            }

            return Build(ItemStatus.Unavailable, "不可用", "N/A");
        }

        private static async Task<bool> ProbeProxyUrlAsync(HttpClient client, string url)
        {
            try
            {
                using var response = await client.GetAsync(url).ConfigureAwait(false);
                return response.IsSuccessStatusCode && response.StatusCode == HttpStatusCode.NoContent;
            }
            catch
            {
                return false;
            }
        }

        private static Task<(bool Succeeded, HttpStatusCode StatusCode, string StatusText)> ProbeTmdbApiAsync(string baseUrl, string apiKey)
        {
            var url = CombineUrl(baseUrl, "/3/configuration");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                url += "?api_key=" + Uri.EscapeDataString(apiKey.Trim());
            }

            return ProbeHttpAsync("GET", url, HttpStatusCode.Unauthorized);
        }

        private static async Task<(bool Succeeded, HttpStatusCode StatusCode, string StatusText)> ProbeTmdbImageAsync(string baseUrl)
        {
            var url = CombineUrl(baseUrl, TmdbImageProbePath);
            var result = await ProbeHttpAsync("HEAD", url).ConfigureAwait(false);
            if (!result.Succeeded && result.StatusCode == HttpStatusCode.MethodNotAllowed)
            {
                result = await ProbeHttpAsync("GET", url).ConfigureAwait(false);
            }

            return result;
        }

        private static async Task<(bool Succeeded, HttpStatusCode StatusCode, string StatusText)> ProbeHttpAsync(
            string method,
            string url,
            HttpStatusCode? acceptedFailureStatus = null)
        {
            try
            {
                var httpClient = Plugin.SharedHttpClient;
                if (httpClient == null)
                {
                    return (false, default, url + " IHttpClient 不可用");
                }

                var requestOptions = new MediaBrowser.Common.Net.HttpRequestOptions
                {
                    Url = url,
                    TimeoutMs = (int)TmdbProbeTimeout.TotalMilliseconds,
                    EnableHttpCompression = true,
                    EnableDefaultUserAgent = false,
                    UserAgent = "MediaInfoKeeper"
                };
                var stopwatch = Stopwatch.StartNew();
                using var response = await httpClient.SendAsync(requestOptions, method).ConfigureAwait(false);
                stopwatch.Stop();

                var statusCode = (HttpStatusCode)response.StatusCode;
                if (((int)response.StatusCode >= 200 && (int)response.StatusCode < 300) ||
                    statusCode == acceptedFailureStatus)
                {
                    return (
                        true,
                        statusCode,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} {1} ({2} ms)",
                            url,
                            statusCode == acceptedFailureStatus ? "连通" : "可用",
                            stopwatch.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)));
                }

                return (
                    false,
                    statusCode,
                    string.Format(CultureInfo.InvariantCulture, "{0} HTTP {1}", url, (int)response.StatusCode));
            }
            catch (Exception ex)
            {
                return (false, default, url + " " + ex.GetType().Name);
            }
        }

        private static bool TryParseProxyEndpoint(
            string raw,
            out string scheme,
            out string host,
            out int port,
            out string username,
            out string password)
        {
            scheme = null;
            host = null;
            port = 0;
            username = string.Empty;
            password = string.Empty;

            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) ||
                string.IsNullOrWhiteSpace(uri.Host) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return false;
            }

            scheme = uri.Scheme;
            host = uri.Host;
            port = uri.IsDefaultPort ? (uri.Scheme == Uri.UriSchemeHttp ? 80 : 443) : uri.Port;
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var userInfoParts = uri.UserInfo.Split(new[] { ':' }, 2);
                username = userInfoParts[0];
                password = userInfoParts.Length > 1 ? userInfoParts[1] : string.Empty;
            }

            return port > 0;
        }

        private static string NormalizeBaseUrl(string raw, string fallback)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            var value = raw.Trim();
            if (!value.Contains("://"))
            {
                value = "https://" + value;
            }

            return value.TrimEnd('/');
        }

        private static string CombineUrl(string baseUrl, string path)
        {
            return baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
        }

        private static Result Build(ItemStatus status, string caption, string statusText)
        {
            return new Result
            {
                Status = status,
                Caption = caption,
                StatusText = statusText
            };
        }
    }
}
