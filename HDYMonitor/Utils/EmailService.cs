using System;
using System.Threading.Tasks;
using AlibabaCloud.SDK.Dm20151123;
using AlibabaCloud.SDK.Dm20151123.Models;
using Tea;
using AlibabaCloud.OpenApiClient.Models;

namespace Aliyun.Base.Utils
{
    public class EmailService
    {
        private readonly Client _client;
        private readonly string _accountName; // 在类级别存储发信地址

        /// <summary>
        /// 构造函数，用于初始化邮件服务客户端
        /// </summary>
        /// <param name="accessKeyId">您的 AccessKey ID</param>
        /// <param name="accessKeySecret">您的 AccessKey Secret</param>
        /// <param name="endpoint">阿里云服务接入点, e.g., "dm.aliyuncs.com"</param>
        /// <param name="accountName">您在控制台配置的发信地址</param>
        public EmailService(string accessKeyId, string accessKeySecret, string endpoint, string accountName)
        {
            if (string.IsNullOrEmpty(accessKeyId) || string.IsNullOrEmpty(accessKeySecret) || string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(accountName))
            {
                throw new ArgumentException("所有构造函数参数都不能为空。");
            }

            Config config = new Config
            {
                AccessKeyId = accessKeyId,
                AccessKeySecret = accessKeySecret,
                Endpoint = endpoint
            };
            _client = new Client(config);
            _accountName = accountName;
        }

        /// <summary>
        /// 异步发送单封邮件
        /// </summary>
        /// <param name="toAddress">收件人地址</param>
        /// <param name="subject">邮件主题</param>
        /// <param name="htmlBody">HTML 格式的邮件正文</param>
        /// <param name="textBody">纯文本格式的邮件正文 (可选)</param>
        /// <returns>返回一个布尔值，表示邮件是否成功提交发送。true表示成功，false表示失败。</returns>
        public async Task<bool> SendEmailAsync(string toAddress, string subject, string htmlBody, string textBody = null)
        {
            if (string.IsNullOrEmpty(toAddress) || string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(htmlBody))
            {
                Console.WriteLine("错误：收件人地址、主题和HTML内容不能为空。");
                return false;
            }

            SingleSendMailRequest request = new SingleSendMailRequest
            {
                AccountName = _accountName,
                ToAddress = toAddress,
                Subject = subject,
                HtmlBody = htmlBody,
                TextBody = textBody, // 可以为 null
                AddressType = 1,
                ReplyToAddress = true, // 通常建议设置为 true
                TagName = "DefaultTag" // 可选，用于统计
            };

            try
            {
                Console.WriteLine($"准备向 {toAddress} 发送邮件...");
                SingleSendMailResponse response = await _client.SingleSendMailAsync(request);
                Console.WriteLine($"邮件已成功提交发送！ RequestId: {response.Body.RequestId}");
                return true;
            }
            catch (TeaException ex)
            {
                Console.WriteLine("邮件发送失败 - API 异常:");
                Console.WriteLine($"错误码 (Code): {ex.Code}");
                Console.WriteLine($"错误信息 (Message): {ex.Message}");
                Console.WriteLine($"请求ID (RequestId): {ex.Data["RequestId"]}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine("邮件发送失败 - 系统异常:");
                Console.WriteLine(ex.ToString());
                return false;
            }
        }
    }
}
