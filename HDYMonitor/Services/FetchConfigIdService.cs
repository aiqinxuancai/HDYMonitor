using Aliyun.Base.Utils;
using HtmlAgilityPack;
using System.Linq;
using System.Text.Json;

namespace HDYMonitor.Services
{
    public class FetchConfigIdService
    {
        private const string DefaultUrlTemplate = "https://www.szhdy.com/cart?action=configureproduct&pid={id}";
        private const int DefaultStartId = 2018;
        private const int DefaultBackwardScanLimit = 200;
        private static readonly string LabelOs = "\u64cd\u4f5c\u7cfb\u7edf";
        private static readonly string LabelName = "\u540d\u79f0";
        private static readonly string[] ConfigFieldLabels =
        {
            "\u8282\u70b9id",
            "CPU",
            "\u5185\u5b58",
            "\u7cfb\u7edf\u76d8",
            "\u5e26\u5bbd",
            "\u7f51\u7edc\u7c7b\u578b",
            "IP\u6570\u91cf",
            "\u6570\u636e\u76d8"
        };
        private static readonly string[] HeadingDenyList =
        {
            "\u4ea7\u54c1\u4e0e\u670d\u52a1",
            "\u652f\u6301\u4e0e\u670d\u52a1",
            "\u4e86\u89e3\u6211\u4eec",
            "\u533a\u57df",
            "\u8fd4\u56de",
            "\u6700\u65b0\u901a\u77e5",
            "\u70ed\u9500\u63a8\u8350"
        };
        private static readonly HashSet<string> ValueNoise = new(StringComparer.OrdinalIgnoreCase)
        {
            "-",
            "+",
            "Image",
            "\u9009\u62e9\u7248\u672c"
        };

        private static bool _initialized;
        private static int? _lastKnownId;

        private static readonly string StoragePath = Environment.GetEnvironmentVariable("CONFIG_ID_STORAGE_PATH")
            ?? (Environment.OSVersion.Platform == PlatformID.Win32NT
                ? Path.Combine(Directory.GetCurrentDirectory(), "lastConfigId.json")
                : "/home/app/HDYMonitor/lastConfigId.json");

        private sealed record ConfigIdState(int LastId, DateTimeOffset UpdatedAt);
        private sealed record ConfigDetails(string? Name, IReadOnlyDictionary<string, string> Fields);

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
                    var name = checkResult.Details?.Name;
                    var title = string.IsNullOrWhiteSpace(name)
                        ? $"\u65b0\u914d\u7f6e\u4e0a\u7ebf ID={nextId}"
                        : $"\u65b0\u914d\u7f6e\u4e0a\u7ebf ID={nextId} {name}";
                    var message = BuildConfigMessage(nextId, url, checkResult.Details);
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

        private static async Task<(bool HasContent, ConfigDetails? Details)> CheckIdHasContentAsync(HttpClient httpClient, int id, CancellationToken cancellationToken)
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

            if (!HasConfigContent(doc))
            {
                return (false, null);
            }

            var details = ExtractConfigDetails(doc);
            return (true, details);
        }

        private static bool HasConfigContent(HtmlDocument doc)
        {
            var osNode = FindLabelNode(doc, LabelOs);
            var cpuNode = FindLabelNode(doc, "CPU");
            return osNode != null && cpuNode != null;
        }

        private static ConfigDetails ExtractConfigDetails(HtmlDocument doc)
        {
            var anchorNode = FindLabelNode(doc, LabelOs);
            var name = ExtractProductName(doc, anchorNode);
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var label in ConfigFieldLabels)
            {
                var labelNode = FindLabelNode(doc, label);
                if (labelNode is null)
                {
                    continue;
                }

                var value = FindNextMeaningfulText(labelNode);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    fields[label] = value;
                }
            }

            return new ConfigDetails(name, fields);
        }

        private static string BuildConfigMessage(int id, string url, ConfigDetails? details)
        {
            var lines = new List<string>
            {
                $"ID: {id}"
            };

            if (details != null)
            {
                if (!string.IsNullOrWhiteSpace(details.Name))
                {
                    lines.Add($"{LabelName}: {details.Name}");
                }

                foreach (var label in ConfigFieldLabels)
                {
                    if (details.Fields.TryGetValue(label, out var value))
                    {
                        lines.Add($"{label}: {value}");
                    }
                }
            }

            lines.Add(url);
            return string.Join("\n", lines);
        }

        private static HtmlNode? FindLabelNode(HtmlDocument doc, string label)
        {
            var node = FindExactLabelNode(doc, label);
            if (node != null)
            {
                return node;
            }

            node = FindExactLabelNode(doc, $"{label}:");
            if (node != null)
            {
                return node;
            }

            return FindExactLabelNode(doc, $"{label}\uFF1A");
        }

        private static HtmlNode? FindExactLabelNode(HtmlDocument doc, string label)
        {
            var node = doc.DocumentNode.SelectSingleNode($"//*[not(*) and normalize-space(.)='{label}']");
            if (node != null)
            {
                return node;
            }

            return doc.DocumentNode.SelectSingleNode($"//*[normalize-space(.)='{label}']");
        }

        private static string? ExtractProductName(HtmlDocument doc, HtmlNode? anchorNode)
        {
            var candidate = FindNameByClass(doc);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }

            var headings = doc.DocumentNode.SelectNodes("//h1|//h2|//h3|//h4");
            if (headings == null || headings.Count == 0)
            {
                return null;
            }

            var anchorPos = anchorNode?.StreamPosition ?? int.MaxValue;
            foreach (var heading in headings.OrderByDescending(h => h.StreamPosition))
            {
                if (heading.StreamPosition > anchorPos)
                {
                    continue;
                }

                var text = NormalizeText(heading.InnerText);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (HeadingDenyList.Any(blocked => string.Equals(blocked, text, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                return text;
            }

            return null;
        }

        private static string? FindNameByClass(HtmlDocument doc)
        {
            var node = doc.DocumentNode.SelectSingleNode("//*[contains(translate(@class,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'product') and contains(translate(@class,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'name')]");
            if (node != null)
            {
                return NormalizeText(node.InnerText);
            }

            node = doc.DocumentNode.SelectSingleNode("//*[contains(translate(@id,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'product') and contains(translate(@id,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'name')]");
            return node == null ? null : NormalizeText(node.InnerText);
        }

        private static string? FindNextMeaningfulText(HtmlNode node)
        {
            for (var next = NextNode(node); next != null; next = NextNode(next))
            {
                if (next.NodeType != HtmlNodeType.Text && next.NodeType != HtmlNodeType.Element)
                {
                    continue;
                }

                var text = NormalizeText(next.InnerText);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (ValueNoise.Contains(text))
                {
                    continue;
                }

                if (string.Equals(text, NormalizeText(node.InnerText), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return text;
            }

            return null;
        }

        private static HtmlNode? NextNode(HtmlNode node)
        {
            if (node.FirstChild != null)
            {
                return node.FirstChild;
            }

            var current = node;
            while (current != null)
            {
                if (current.NextSibling != null)
                {
                    return current.NextSibling;
                }

                current = current.ParentNode;
            }

            return null;
        }

        private static string NormalizeText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
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
