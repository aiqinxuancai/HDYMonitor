using Aliyun.Base.Utils;
using HtmlAgilityPack;
using System.Text.Json;

namespace HDYMonitor.Services
{
    public class FetchConfigIdService
    {
        private const string DefaultUrlTemplate = "https://www.szhdy.com/cart?action=configureproduct&pid={id}";
        private const int DefaultStartId = 2018;
        private const int DefaultBackwardScanLimit = 200;
        private const int RequiredMarkerHits = 2;
        private static readonly string[] ContentMarkers =
        {
            "操作系统",
            "节点id",
            "CPU",
            "内存",
            "系统盘",
            "带宽",
            "IP数量",
            "硬盘模式",
            "网络类型",
            "系统版本"
        };

        private static bool _initialized;
        private static int? _lastKnownId;

        private static readonly string StoragePath = Environment.GetEnvironmentVariable("CONFIG_ID_STORAGE_PATH")
            ?? (Environment.OSVersion.Platform == PlatformID.Win32NT
                ? Path.Combine(Directory.GetCurrentDirectory(), "lastConfigId.json")
                : "/home/app/HDYMonitor/lastConfigId.json");

        private sealed record ConfigIdState(int LastId, DateTimeOffset UpdatedAt);

        public static async Task<FetchActivityResult> CheckAndNotifyAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (!IsEnabled())
                {
                    return new FetchActivityResult(true, 204, "Config ID monitor disabled.");
                }

                using var httpClient = CreateHttpClient();

                var initResult = await EnsureInitializedAsync(httpClient, cancellationToken);
                if (!initResult.Success)
                {
                    return initResult;
                }

                if (_lastKnownId is null)
                {
                    return new FetchActivityResult(false, 500, "Config ID monitor has no last ID.");
                }

                var nextId = _lastKnownId.Value + 1;
                var checkResult = await CheckIdHasContentAsync(httpClient, nextId, cancellationToken);
                if (checkResult.HasContent)
                {
                    var url = BuildUrl(nextId);
                    var title = $"新配置上线 ID={nextId}";
                    var message = $"{title}\n{url}";
                    await SendHelper.SendPushDeer(title, message);
                    await SaveLastIdAsync(nextId, cancellationToken);
                    _lastKnownId = nextId;
                    return new FetchActivityResult(true, 200, $"New config detected. ID={nextId}");
                }

                return new FetchActivityResult(true, 200, $"No new config. Last ID={_lastKnownId.Value}");
            }
            catch (Exception ex)
            {
                return new FetchActivityResult(false, 500, $"Config ID monitor error: {ex.Message}");
            }
        }

        private static bool IsEnabled()
        {
            var enabled = Environment.GetEnvironmentVariable("CONFIG_ID_MONITOR_ENABLED");
            if (string.IsNullOrWhiteSpace(enabled))
            {
                return true;
            }

            return !string.Equals(enabled, "0", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<FetchActivityResult> EnsureInitializedAsync(HttpClient httpClient, CancellationToken cancellationToken)
        {
            if (_initialized)
            {
                return new FetchActivityResult(true, 200, "Config ID monitor initialized.");
            }

            var configuredStartId = GetEnvInt("CONFIG_ID_START", DefaultStartId);
            var scanLimit = GetEnvInt("CONFIG_ID_BACKWARD_SCAN_LIMIT", DefaultBackwardScanLimit);

            var storedId = await LoadLastIdAsync(cancellationToken);
            var scanStartId = Math.Max(configuredStartId, storedId ?? 0);

            var latestId = await FindLatestAvailableIdAsync(httpClient, scanStartId, scanLimit, cancellationToken);
            if (latestId is null)
            {
                return new FetchActivityResult(false, 404, $"Unable to locate a valid config ID from {scanStartId} (scan limit {scanLimit}).");
            }

            _lastKnownId = latestId.Value;
            await SaveLastIdAsync(latestId.Value, cancellationToken);
            _initialized = true;
            return new FetchActivityResult(true, 200, $"Config ID initialized. Last ID={latestId.Value}");
        }

        private static async Task<int?> FindLatestAvailableIdAsync(HttpClient httpClient, int startId, int scanLimit, CancellationToken cancellationToken)
        {
            var currentId = startId;
            for (var i = 0; i <= scanLimit && currentId > 0; i++, currentId--)
            {
                var result = await CheckIdHasContentAsync(httpClient, currentId, cancellationToken);
                if (result.HasContent)
                {
                    return currentId;
                }
            }

            return null;
        }

        private static async Task<(bool HasContent, string? Title)> CheckIdHasContentAsync(HttpClient httpClient, int id, CancellationToken cancellationToken)
        {
            var url = BuildUrl(id);
            using var response = await httpClient.GetAsync(url, cancellationToken);
            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(html))
            {
                return (false, null);
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var text = doc.DocumentNode.InnerText ?? string.Empty;
            var hits = 0;
            foreach (var marker in ContentMarkers)
            {
                if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    hits++;
                    if (hits >= RequiredMarkerHits)
                    {
                        return (true, null);
                    }
                }
            }

            return (false, null);
        }

        private static string BuildUrl(int id)
        {
            var template = Environment.GetEnvironmentVariable("CONFIG_ID_URL_TEMPLATE") ?? DefaultUrlTemplate;
            if (template.Contains("{id}", StringComparison.OrdinalIgnoreCase))
            {
                return template.Replace("{id}", id.ToString(), StringComparison.OrdinalIgnoreCase);
            }

            return template.EndsWith("=", StringComparison.Ordinal) ? $"{template}{id}" : $"{template}{id}";
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = true,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };

            var httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36");
            httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            httpClient.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en-US;q=0.8,en;q=0.7");
            httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            httpClient.DefaultRequestHeaders.Add("DNT", "1");
            httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
            httpClient.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
            httpClient.DefaultRequestHeaders.Add("Pragma", "no-cache");
            httpClient.DefaultRequestHeaders.Add("Referer", "https://www.szhdy.com/");

            return httpClient;
        }

        private static async Task<int?> LoadLastIdAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(StoragePath))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(StoragePath, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                var state = JsonSerializer.Deserialize<ConfigIdState>(json);
                return state?.LastId;
            }
            catch (JsonException)
            {
                if (int.TryParse(json.Trim(), out var id))
                {
                    return id;
                }
            }

            return null;
        }

        private static async Task SaveLastIdAsync(int id, CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(StoragePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var state = new ConfigIdState(id, DateTimeOffset.Now);
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(StoragePath, json, cancellationToken);
        }

        private static int GetEnvInt(string key, int defaultValue)
        {
            var value = Environment.GetEnvironmentVariable(key);
            return int.TryParse(value, out var parsed) ? parsed : defaultValue;
        }
    }
}
