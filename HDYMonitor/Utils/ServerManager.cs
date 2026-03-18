using System.Text.Json;
using Aliyun.Base.Utils;

namespace HDYMonitor.Utils
{
    public static class ServerManager
    {
        private static readonly string StoragePath = Environment.GetEnvironmentVariable("STORAGE_PATH")
            ?? (Environment.OSVersion.Platform == PlatformID.Win32NT
                ? Path.Combine(Directory.GetCurrentDirectory(), "lastServers.json")
                : "/home/app/HDYMonitor/lastServers.json");

        public static async Task CheckAndNotifyAsync(List<ServerProductModel> currentServers)
        {
            try
            {
                var lastServers = new List<ServerProductModel>();

                var directory = Path.GetDirectoryName(StoragePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (File.Exists(StoragePath))
                {
                    var json = await File.ReadAllTextAsync(StoragePath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        lastServers = JsonSerializer.Deserialize<List<ServerProductModel>>(json) ?? new List<ServerProductModel>();
                    }
                }

                var hasChanges = false;

                foreach (var server in currentServers)
                {
                    var lastServer = lastServers.FirstOrDefault(s =>
                        s.ServerName == server.ServerName &&
                        s.Price == server.Price &&
                        s.Core == server.Core &&
                        s.Memory == server.Memory &&
                        s.SystemDisk == server.SystemDisk &&
                        s.Bandwidth == server.Bandwidth &&
                        s.Term == server.Term);

                    if (lastServer != null)
                    {
                        if (lastServer.IsPurchasable != server.IsPurchasable)
                        {
                            hasChanges = true;
                            if (server.IsPurchasable)
                            {
                                var title = $"\u670d\u52a1\u5668\u53ef\u8d2d\u4e70: {server.ServerName}";
                                var message = BuildServerMessage(server, includeStatus: true);
                                await SendHelper.PostMessageToMailAliyun(title, message);
                                await SendHelper.SendPushDeer(title, message);
                            }
                        }

                        continue;
                    }

                    hasChanges = true;
                    if (server.IsPurchasable)
                    {
                        var title = $"\u65b0\u670d\u52a1\u5668\u4e0a\u67b6: {server.ServerName}";
                        var message = BuildServerMessage(server, includeStatus: true);
                        await SendHelper.SendPushDeer(title, message);
                    }
                }

                if (lastServers.Count != currentServers.Count)
                {
                    hasChanges = true;
                }

                if (hasChanges)
                {
                    var newJson = JsonSerializer.Serialize(currentServers, new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(StoragePath, newJson);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in ServerManager: {ex.Message}");
            }
        }

        private static string BuildServerMessage(ServerProductModel server, bool includeStatus)
        {
            var lines = new List<string>
            {
                $"\u540d\u79f0: {server.ServerName}",
                BuildPriceLine(server),
                $"CPU: {ValueOrUnknown(server.Core)}",
                $"\u5185\u5b58: {ValueOrUnknown(server.Memory)}",
                $"\u5e26\u5bbd: {ValueOrUnknown(server.Bandwidth)}",
                $"\u5e74\u9650: {ValueOrUnknown(server.Term)}"
            };

            if (!string.IsNullOrWhiteSpace(server.SystemDisk))
            {
                lines.Add($"\u7cfb\u7edf\u76d8: {server.SystemDisk}");
            }

            if (includeStatus)
            {
                lines.Add($"\u72b6\u6001: {(server.IsPurchasable ? "\u53ef\u8d2d\u4e70" : "\u4e0d\u53ef\u8d2d\u4e70")} [{server.StatusMessage}]");
            }

            return string.Join("\n", lines);
        }

        private static string BuildPriceLine(ServerProductModel server)
        {
            var renewalInfo = string.IsNullOrWhiteSpace(server.RenewalInfo) ? string.Empty : $" {server.RenewalInfo.Trim()}";
            return $"\u4ef7\u683c: {ValueOrUnknown(server.Price)}{renewalInfo}";
        }

        private static string ValueOrUnknown(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? "\u672a\u77e5" : value;
        }
    }
}
