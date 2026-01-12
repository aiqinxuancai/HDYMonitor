using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Aliyun.Base.Utils;
using Flurl.Http;

namespace HDYMonitor.Utils
{
    public static class ServerManager
    {
        // Use environment variable for path, default to the requested path (handling Windows for dev convenience)
        private static readonly string StoragePath = Environment.GetEnvironmentVariable("STORAGE_PATH") 
            ?? (Environment.OSVersion.Platform == PlatformID.Win32NT 
                ? Path.Combine(Directory.GetCurrentDirectory(), "lastServers.json") 
                : "/home/app/HDYMonitor/lastServers.json");


        public static async Task CheckAndNotifyAsync(List<ServerProductModel> currentServers)
        {
            try 
            {
                List<ServerProductModel> lastServers = new List<ServerProductModel>();
                
                // Ensure directory exists
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

                bool hasChanges = false;

                // Compare
                foreach (var server in currentServers)
                {
                    var lastServer = lastServers.FirstOrDefault(s => 
                        s.ServerName == server.ServerName &&
                        s.Price == server.Price &&
                        s.Core == server.Core &&
                        s.Memory == server.Memory);
                    
                    if (lastServer != null)
                    {
                        // Check if status changed
                        if (lastServer.IsPurchasable != server.IsPurchasable)
                        {
                            hasChanges = true;
                            // Notify if it became purchasable
                            if (server.IsPurchasable)
                            {
                                await SendHelper.PostMessageToMailAliyun($"服务器 '{server.ServerName}' 可购买! Price: {server.Price}. {server.RenewalInfo}", $"服务器 '{server.ServerName}' 可购买! Price: {server.Price}. {server.RenewalInfo}");
                                await SendHelper.SendPushDeer($"服务器 '{server.ServerName}' 可购买! Price: {server.Price}. {server.RenewalInfo}");
                            }
                        }
                    }
                    else
                    {
                        // New server found
                        hasChanges = true;
                        if (server.IsPurchasable)
                        {
                             await SendHelper.SendPushDeer($"新服务器 '{server.ServerName}' ! Price: {server.Price}. {server.RenewalInfo}");
                        }
                    }
                }

                // If lists lengths differ, definitely changed (server added or removed)
                if (lastServers.Count != currentServers.Count) hasChanges = true;

                // Save new state if there were any changes
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



        

        
    }
}