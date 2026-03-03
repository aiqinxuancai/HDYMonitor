using HDYMonitor.Utils;
using HtmlAgilityPack;
using System.Linq;
using System.Text.Json;

namespace HDYMonitor.Services
{
    public sealed record FetchActivityResult(bool Success, int StatusCode, string Message, string[]? Solutions = null);

    public class FetchActivityService
    {
        private const string DefaultNewestActivityUrl = "https://www.szhdy.com/newestactivity.html";

        public static async Task<FetchActivityResult> FetchAndProcessActivityAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // 创建HttpClientHandler以支持自动重定向、cookie和压缩
                var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = true,
                    UseCookies = true,
                    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
                };

                using (var httpClient = new HttpClient(handler))
                {
                    // 设置超时时间
                    httpClient.Timeout = TimeSpan.FromSeconds(30);

                    // 添加完整的浏览器请求头,模拟真实浏览器行为
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36");
                    httpClient.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
                    httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
                    httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
                    httpClient.DefaultRequestHeaders.Add("DNT", "1");
                    httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
                    httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "empty");
                    httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "cors");
                    httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
                    httpClient.DefaultRequestHeaders.Add("Referer", "https://www.szhdy.com/");
                    httpClient.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
                    httpClient.DefaultRequestHeaders.Add("Pragma", "no-cache");

                    var newestActivityUrl = Environment.GetEnvironmentVariable("NEWEST_ACTIVITY_URL") ?? DefaultNewestActivityUrl;
                    Console.WriteLine($"[HTTP] GET {newestActivityUrl}");
                    using var newestActivityResponse = await httpClient.GetAsync(newestActivityUrl, cancellationToken);
                    var newestActivityHtml = await newestActivityResponse.Content.ReadAsStringAsync(cancellationToken);
                    Console.WriteLine($"Status ({newestActivityUrl}): {newestActivityResponse.StatusCode}");

                    if (IsCloudflareChallenge(newestActivityHtml))
                    {
                        return BuildCloudflareResult();
                    }

                    if (!newestActivityResponse.IsSuccessStatusCode)
                    {
                        return new FetchActivityResult(
                            false,
                            (int)newestActivityResponse.StatusCode,
                            $"Error fetching newest activity list: {newestActivityResponse.ReasonPhrase}");
                    }

                    var activityUrls = ExtractOngoingActivityUrls(newestActivityHtml, newestActivityUrl);
                    if (activityUrls.Count == 0)
                    {
                        return new FetchActivityResult(false, 404, "No ongoing activities found on newestactivity page.");
                    }

                    Console.WriteLine($"Found {activityUrls.Count} ongoing activity page(s): {string.Join(", ", activityUrls)}");

                    var allServers = new List<ServerProductModel>();
                    foreach (var activityUrl in activityUrls)
                    {
                        Console.WriteLine($"[HTTP] GET {activityUrl}");
                        using var activityResponse = await httpClient.GetAsync(activityUrl, cancellationToken);
                        var activityHtml = await activityResponse.Content.ReadAsStringAsync(cancellationToken);
                        Console.WriteLine($"Status ({activityUrl}): {activityResponse.StatusCode}");

                        if (IsCloudflareChallenge(activityHtml))
                        {
                            return BuildCloudflareResult();
                        }

                        if (!activityResponse.IsSuccessStatusCode)
                        {
                            return new FetchActivityResult(
                                false,
                                (int)activityResponse.StatusCode,
                                $"Error fetching activity page {activityUrl}: {activityResponse.ReasonPhrase}");
                        }

                        var servers = HtmlAnalyzer.ParseHtml(activityHtml);
                        Console.WriteLine($"活动页面解析到 {servers.Count} 个产品：{activityUrl}");
                        allServers.AddRange(servers);
                    }

                    var mergedServers = MergeServers(allServers);
                    Console.WriteLine($"共解析到 {mergedServers.Count} 个产品（原始 {allServers.Count} 个，去重后）：{JsonSerializer.Serialize(mergedServers)}");

                    await ServerManager.CheckAndNotifyAsync(mergedServers);
                    return new FetchActivityResult(true, 200, $"执行成功。活动页数量: {activityUrls.Count}, 产品数量: {mergedServers.Count}");
                }
            }
            catch (Exception ex)
            {
                return new FetchActivityResult(false, 500, $"Internal server error: {ex.Message}");
            }
        }

        private static bool IsCloudflareChallenge(string htmlContent)
        {
            return htmlContent.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
                || htmlContent.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase)
                || htmlContent.Contains("__cf_chl_opt", StringComparison.OrdinalIgnoreCase);
        }

        private static FetchActivityResult BuildCloudflareResult()
        {
            var solutions = new[]
            {
                "1. 使用Selenium/Playwright等浏览器自动化工具(可执行JavaScript解决挑战)",
                "2. 使用FlareSolverr等专门的Cloudflare绕过服务 (https://github.com/FlareSolverr/FlareSolverr)",
                "3. 使用Puppeteer-Sharp在C#中模拟真实浏览器",
                "4. 使用Truth Social官方API并提供访问令牌(如果可用)",
                "5. 使用代理服务或轮换IP地址",
                "6. 考虑使用云函数/无服务器函数来分散请求"
            };

            return new FetchActivityResult(false, 503, "Cloudflare Protection Detected", solutions);
        }

        private static List<string> ExtractOngoingActivityUrls(string htmlContent, string newestActivityUrl)
        {
            var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var doc = new HtmlDocument();
            doc.LoadHtml(htmlContent);

            var baseUri = new Uri(newestActivityUrl);

            var ongoingNodes = doc.DocumentNode.SelectNodes(
                "//a[contains(concat(' ', normalize-space(@class), ' '), ' activity-item ') and translate(normalize-space(@data-status), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')='ongoing']");

            AddActivityLinks(ongoingNodes, baseUri, urls);

            if (urls.Count == 0)
            {
                var statusNodes = doc.DocumentNode.SelectNodes(
                    "//p[contains(concat(' ', normalize-space(@class), ' '), ' status_text ') and contains(normalize-space(.), '进行中')]");

                if (statusNodes != null)
                {
                    foreach (var statusNode in statusNodes)
                    {
                        var anchorNode = statusNode.SelectSingleNode("ancestor::a[contains(concat(' ', normalize-space(@class), ' '), ' activity-item ')]");
                        AddActivityLink(anchorNode, baseUri, urls);
                    }
                }
            }

            return urls.OrderBy(url => url, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddActivityLinks(HtmlNodeCollection? nodes, Uri baseUri, HashSet<string> urls)
        {
            if (nodes == null)
            {
                return;
            }

            foreach (var node in nodes)
            {
                AddActivityLink(node, baseUri, urls);
            }
        }

        private static void AddActivityLink(HtmlNode? node, Uri baseUri, HashSet<string> urls)
        {
            var href = node?.GetAttributeValue("href", string.Empty);
            if (string.IsNullOrWhiteSpace(href))
            {
                return;
            }

            if (Uri.TryCreate(baseUri, href, out var absoluteUri))
            {
                urls.Add(absoluteUri.ToString());
            }
        }

        private static List<ServerProductModel> MergeServers(List<ServerProductModel> servers)
        {
            var merged = new Dictionary<string, ServerProductModel>(StringComparer.OrdinalIgnoreCase);

            foreach (var server in servers)
            {
                var key = string.Join("|",
                    server.ServerName ?? string.Empty,
                    server.Price ?? string.Empty,
                    server.Core ?? string.Empty,
                    server.Memory ?? string.Empty,
                    server.SystemDisk ?? string.Empty,
                    server.Bandwidth ?? string.Empty);

                if (!merged.TryGetValue(key, out var existing))
                {
                    merged[key] = server;
                    continue;
                }

                if (!existing.IsPurchasable && server.IsPurchasable)
                {
                    merged[key] = server;
                }
            }

            return merged.Values.ToList();
        }
    }
}

