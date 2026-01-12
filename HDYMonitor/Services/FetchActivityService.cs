using HDYMonitor.Utils;
using System.Text.Json;

namespace HDYMonitor.Services
{
    public sealed record FetchActivityResult(bool Success, int StatusCode, string Message, string[]? Solutions = null);

    public class FetchActivityService
    {
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

                    var url = Environment.GetEnvironmentVariable("TARGET_URL") ?? "https://www.szhdy.com/activities/default.html?method=activity&id=11";
                    var response = await httpClient.GetAsync(url, cancellationToken);

                    var htmlContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    Console.WriteLine($"Status: {response.StatusCode}");

                    // 检测是否是Cloudflare挑战页面
                    if (htmlContent.Contains("Just a moment") || htmlContent.Contains("challenge-platform") || htmlContent.Contains("__cf_chl_opt"))
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

                    if (response.IsSuccessStatusCode)
                    {
                        List<ServerProductModel> servers = HtmlAnalyzer.ParseHtml(htmlContent);

                        Console.WriteLine($"共解析到 {servers.Count} 个产品：{JsonSerializer.Serialize(servers)}");

                        await ServerManager.CheckAndNotifyAsync(servers);

                        return new FetchActivityResult(true, 200, "执行成功");
                    }
                    else
                    {
                        return new FetchActivityResult(false, (int)response.StatusCode, $"Error fetching data: {response.ReasonPhrase}");
                    }
                }
            }
            catch (Exception ex)
            {
                return new FetchActivityResult(false, 500, $"Internal server error: {ex.Message}");
            }
        }
    }
}
