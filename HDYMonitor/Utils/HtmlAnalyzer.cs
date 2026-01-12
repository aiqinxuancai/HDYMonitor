using HtmlAgilityPack;
using System.Text;

namespace HDYMonitor.Utils
{
    public class ServerProductModel
    {
        public string ServerName { get; set; }      // 服务器名称
        public string Price { get; set; }           // 价格
        public string RenewalInfo { get; set; }     // 续费说明
        public bool IsPurchasable { get; set; }     // 是否可购买
        public string StatusMessage { get; set; }   // 状态文字

        // --- 新增字段 ---
        public string Core { get; set; }            // 核心
        public string Memory { get; set; }          // 内存
        public string SystemDisk { get; set; }      // 系统盘
        public string Bandwidth { get; set; }       // 带宽

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"名称: {ServerName}");
            sb.AppendLine($"价格: {Price} ({RenewalInfo})");
            sb.AppendLine($"配置: {Core} | {Memory} | {SystemDisk} | {Bandwidth}");
            sb.AppendLine($"状态: {(IsPurchasable ? "可购买" : "不可购买")} [{StatusMessage}]");
            return sb.ToString();
        }
    }

    // 2. 更新分析类
    public class HtmlAnalyzer
    {
        public static List<ServerProductModel> ParseHtml(string htmlContent)
        {
            var results = new List<ServerProductModel>();
            var doc = new HtmlDocument();
            doc.LoadHtml(htmlContent);

            // 查找所有促销卡片
            var cardNodes = doc.DocumentNode.SelectNodes("//div[contains(@class, '-promotion-card')]");

            if (cardNodes == null) return results;

            foreach (var card in cardNodes)
            {
                var model = new ServerProductModel();

                // --- 1. 基础信息提取 ---
                var nameNode = card.SelectSingleNode(".//h1");
                model.ServerName = nameNode?.InnerText.Trim() ?? "未知名称";

                var priceNode = card.SelectSingleNode(".//span[contains(@class, 'main-price-current')]");
                if (priceNode != null)
                {
                    model.Price = priceNode.GetAttributeValue("data-current", priceNode.InnerText.Trim());
                }

                var unitNode = card.SelectSingleNode(".//span[contains(@class, 'price-current-unit')]");
                model.RenewalInfo = unitNode?.InnerText.Trim() ?? "";

                // --- 2. 详细配置提取 (新增逻辑) ---
                // 查找卡片内所有的 form-container 行
                var configRows = card.SelectNodes(".//div[contains(@class, 'form-container')]");
                if (configRows != null)
                {
                    foreach (var row in configRows)
                    {
                        // 提取标题 (例如 "核心：")
                        var titleNode = row.SelectSingleNode(".//div[@class='form-title']/h5");
                        // 提取值 (例如 "12H ...")
                        var valueNode = row.SelectSingleNode(".//div[contains(@class, 'form-content-data')]//p[contains(@class, 'form-text')]");

                        if (titleNode != null && valueNode != null)
                        {
                            // 清理标题中的冒号和空格
                            string title = titleNode.InnerText.Trim().Replace("：", "").Replace(":", "");
                            string value = valueNode.InnerText.Trim();

                            switch (title)
                            {
                                case "核心":
                                    model.Core = value;
                                    break;
                                case "内存":
                                    model.Memory = value;
                                    break;
                                case "系统盘":
                                    model.SystemDisk = value;
                                    break;
                                case "带宽":
                                    model.Bandwidth = value;
                                    break;
                            }
                        }
                    }
                }

                // --- 3. 购买状态判断 ---
                var btnNode = card.SelectSingleNode(".//a[contains(@class, 'form-footer-butt')]");
                if (btnNode != null)
                {
                    string btnText = btnNode.InnerText.Trim();
                    string btnClass = btnNode.GetAttributeValue("class", "");
                    model.StatusMessage = btnText;
                    model.IsPurchasable = !btnClass.Contains("disableButton") && !btnText.Contains("售罄");
                }
                else
                {
                    model.IsPurchasable = false;
                    model.StatusMessage = "未找到按钮";
                }

                results.Add(model);
            }

            return results;
        }
    }
}
