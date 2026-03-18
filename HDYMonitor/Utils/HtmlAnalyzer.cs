using HtmlAgilityPack;
using System.Text;
using System.Text.RegularExpressions;

namespace HDYMonitor.Utils
{
    public class ServerProductModel
    {
        public string ServerName { get; set; } = string.Empty;
        public string Price { get; set; } = string.Empty;
        public string RenewalInfo { get; set; } = string.Empty;
        public bool IsPurchasable { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
        public string Core { get; set; } = string.Empty;
        public string Memory { get; set; } = string.Empty;
        public string SystemDisk { get; set; } = string.Empty;
        public string Bandwidth { get; set; } = string.Empty;
        public string Term { get; set; } = string.Empty;

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"名称: {ServerName}");
            sb.AppendLine($"价格: {Price} ({RenewalInfo})");
            sb.AppendLine($"配置: {Core} | {Memory} | {SystemDisk} | {Bandwidth} | {Term}");
            sb.AppendLine($"状态: {(IsPurchasable ? "可购买" : "不可购买")} [{StatusMessage}]");
            return sb.ToString();
        }
    }

    public class HtmlAnalyzer
    {
        public static List<ServerProductModel> ParseHtml(string htmlContent)
        {
            var results = new List<ServerProductModel>();
            var doc = new HtmlDocument();
            doc.LoadHtml(htmlContent);

            var cardNodes = doc.DocumentNode.SelectNodes("//div[contains(@class, '-promotion-card')]");
            if (cardNodes == null)
            {
                return results;
            }

            foreach (var card in cardNodes)
            {
                var model = new ServerProductModel();

                var nameNode = card.SelectSingleNode(".//h1");
                model.ServerName = nameNode?.InnerText.Trim() ?? "未知名称";

                var priceNode = card.SelectSingleNode(".//span[contains(@class, 'main-price-current')]");
                if (priceNode != null)
                {
                    model.Price = priceNode.GetAttributeValue("data-current", priceNode.InnerText.Trim());
                }

                var unitNode = card.SelectSingleNode(".//span[contains(@class, 'price-current-unit')]");
                model.RenewalInfo = unitNode?.InnerText.Trim() ?? string.Empty;

                var configRows = card.SelectNodes(".//div[contains(@class, 'form-container')]");
                if (configRows != null)
                {
                    foreach (var row in configRows)
                    {
                        var titleNode = row.SelectSingleNode(".//div[@class='form-title']/h5");
                        if (titleNode == null)
                        {
                            continue;
                        }

                        var title = titleNode.InnerText.Trim().Replace("：", string.Empty).Replace(":", string.Empty);
                        var value = ExtractConfigValue(row);
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            continue;
                        }

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
                            case "时长":
                            case "年限":
                            case "周期":
                                model.Term = value;
                                break;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(model.Term))
                {
                    model.Term = TryExtractTermFromRenewalInfo(model.RenewalInfo);
                }

                var btnNode = card.SelectSingleNode(".//a[contains(@class, 'form-footer-butt')]");
                if (btnNode != null)
                {
                    var btnText = btnNode.InnerText.Trim();
                    var btnClass = btnNode.GetAttributeValue("class", string.Empty);
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

        private static string ExtractConfigValue(HtmlNode row)
        {
            var textNode = row.SelectSingleNode(".//div[contains(@class, 'form-content-data')]//p[contains(@class, 'form-text')]");
            if (textNode != null)
            {
                return HtmlEntity.DeEntitize(textNode.InnerText).Trim();
            }

            var optionNodes = row.SelectNodes(".//div[contains(@class, 'act-dropdown-option')]");
            if (optionNodes != null)
            {
                var values = optionNodes
                    .Select(node => HtmlEntity.DeEntitize(node.InnerText).Trim())
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (values.Count > 0)
                {
                    return string.Join(", ", values);
                }
            }

            var selectedNode = row.SelectSingleNode(".//div[contains(@class, 'act-dropdown-selected')]");
            return selectedNode == null
                ? string.Empty
                : HtmlEntity.DeEntitize(selectedNode.InnerText).Trim();
        }

        private static string TryExtractTermFromRenewalInfo(string renewalInfo)
        {
            if (string.IsNullOrWhiteSpace(renewalInfo))
            {
                return string.Empty;
            }

            var match = Regex.Match(renewalInfo, @"\d+\s*(?:年|月|天)");
            return match.Success ? match.Value.Trim() : string.Empty;
        }
    }
}
