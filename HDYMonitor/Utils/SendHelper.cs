
using AlibabaCloud.SDK.Dm20151123.Models;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace Aliyun.Base.Utils
{
    public class SendHelper
    {
        public static async Task SendPushDeer(string title, string msg = "")
        {
            Console.WriteLine($"SendPushDeer {msg}");

            string? pushDeerKeysValue = Environment.GetEnvironmentVariable("PUSHDEER_KEY");

            if (string.IsNullOrWhiteSpace(pushDeerKeysValue))
            {
                Console.WriteLine("主程序：未配置PushDeer的Key，跳过发送PushDeer。");
                return;
            }

            var pushDeerKeys = pushDeerKeysValue
                .Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (pushDeerKeys.Count == 0)
            {
                Console.WriteLine("主程序：PUSHDEER_KEY配置为空，跳过发送PushDeer。");
                return;
            }

            HttpClient client = new HttpClient();

            var url = "https://api2.pushdeer.com/message/push";
            for (var i = 0; i < pushDeerKeys.Count; i++)
            {
                var pushDeerKey = pushDeerKeys[i];
                var maskedPushDeerKey = MaskPushDeerKey(pushDeerKey);
                try
                {
                    var postData = new FormUrlEncodedContent(new Dictionary<string, string>()
                    {
                        {"pushkey" , pushDeerKey },
                        {"text" , title },
                        {"desp" , msg },
                    });

                    var ret = await client.PostAsync(url, postData);

                    ret.EnsureSuccessStatusCode();
                    Console.WriteLine($"PushDeer推送成功，第{i + 1}/{pushDeerKeys.Count}个Key，Key: {maskedPushDeerKey}");
                    Console.WriteLine(await ret.Content.ReadAsStringAsync());
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"PushDeer推送失败，第{i + 1}/{pushDeerKeys.Count}个Key，Key: {maskedPushDeerKey}");
                    Console.WriteLine(ex.ToString());
                }
            }

        }

        private static string MaskPushDeerKey(string pushDeerKey)
        {
            if (string.IsNullOrWhiteSpace(pushDeerKey))
            {
                return "***";
            }

            var key = pushDeerKey.Trim();
            const int visibleChars = 3;

            if (key.Length <= visibleChars * 2)
            {
                if (key.Length == 1)
                {
                    return "***";
                }

                return $"{key[0]}***{key[^1]}";
            }

            return $"{key[..visibleChars]}***{key[^visibleChars..]}";
        }

        public static async Task PostMessageToMailAliyun(string title, string content)
        {
            string? accessKeyId = Environment.GetEnvironmentVariable("ALIYUNAPI_KEY");
            string? accessKeySecret = Environment.GetEnvironmentVariable("ALIYUNAPI_SECRET");

            if (string.IsNullOrEmpty(accessKeyId) || string.IsNullOrEmpty(accessKeySecret))
            {
                Console.WriteLine("主程序：未配置阿里云API的Key和Secret，跳过发送邮件。");
                return;
            }


            string endpoint = "dm.aliyuncs.com";
            string fromAddress = "msg@mail.moehex.com"; // 您配置的发信地址

            // --- 初始化服务 ---
            var emailService = new EmailService(accessKeyId, accessKeySecret, endpoint, fromAddress);

            // --- 调用发送函数 ---
            string recipientEmail = "aiqinxuancai@163.com"; // 收件人
            string emailSubject = title;
            string emailHtmlBody = content;


            //处理content，让其拥有基础的HTML格式，比如将换行换成br，添加p段落等
            emailHtmlBody = "<p>" + emailHtmlBody.Replace("\n", "<br/>") + "</p>";

            // 发送邮件并获取结果
            bool isSuccess = await emailService.SendEmailAsync(recipientEmail, emailSubject, emailHtmlBody);

            if (isSuccess)
            {
                Console.WriteLine("主程序：邮件发送任务成功完成。");
            }
            else
            {
                Console.WriteLine("主程序：邮件发送任务失败。");
            }

            Console.WriteLine($"发送邮件完毕");
        }


    }
}
