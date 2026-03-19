using Aliyun.Base.Utils;
using HDYMonitor.Utils;
using HtmlAgilityPack;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace HDYMonitor.Services
{
    public class FetchConfigIdService
    {
        private const string DefaultUrlTemplate = "https://www.szhdy.com/cart?action=configureproduct&pid={id}";
        private const int DefaultStartId = 2018;
        private const int DefaultBackwardScanLimit = 200;
        private static readonly string LabelOs = "\u64cd\u4f5c\u7cfb\u7edf";
        private static readonly string[] ConfigFieldLabels =
        {
            "\u8282\u70b9id",
            "CPU",
            "\u5185\u5b58",
            "\u7cfb\u7edf\u76d8",
            "\u5e26\u5bbd",
            "\u5e74\u9650",
            "\u5e93\u5b58",
            "\u6570\u636e\u76d8"
        };
        private static readonly string[] TermLabelCandidates =
        {
            "\u5e74\u9650",
            "\u65f6\u957f",
            "\u5468\u671f",
            "\u8d2d\u4e70\u65f6\u957f"
        };
        private static readonly Dictionary<string, string[]> FieldLabelCandidates = new(StringComparer.OrdinalIgnoreCase)
        {
            ["\u5e26\u5bbd"] = new[] { "\u5e26\u5bbd", "\u5cf0\u503c\u5e26\u5bbd" }
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
        private static readonly string[] PriceLabelCandidates =
        {
            "\u4ef7\u683c",
            "\u603b\u8ba1",
            "\u5408\u8ba1",
            "\u91d1\u989d",
            "\u5c0f\u8ba1"
        };
        private static readonly string[] PriceClassHints =
        {
            "price",
            "total",
            "amount",
            "pay",
            "subtotal"
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
        private sealed record ConfigDetails(string? Name, string? Price, IReadOnlyDictionary<string, string> Fields);
        private sealed record OrderSummaryInfo(string? Price, string? Stock);

        public static async Task<FetchActivityResult> CheckAndNotifyAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (!IsEnabled())
                {
                    return new FetchActivityResult(true, 204, "Config ID monitor disabled.");
                }

                var initResult = await EnsureInitializedAsync(cancellationToken);
                if (!initResult.Success)
                {
                    return initResult;
                }

                if (_lastKnownId is null)
                {
                    return new FetchActivityResult(false, 500, "Config ID monitor has no last ID.");
                }

                var detectedIds = new List<int>();
                var lastDetectedId = _lastKnownId.Value;
                var probeId = _lastKnownId.Value;
                var consecutiveMisses = 0;

                while (consecutiveMisses < 3)
                {
                    var nextId = probeId + 1;
                    var checkResult = await CheckIdHasContentAsync(nextId, cancellationToken);
                    probeId = nextId;

                    if (!checkResult.HasContent)
                    {
                        consecutiveMisses++;
                        continue;
                    }

                    var url = BuildUrl(nextId);
                    var name = checkResult.Details?.Name;
                    var title = string.IsNullOrWhiteSpace(name)
                        ? $"\u65b0\u914d\u7f6e\u4e0a\u7ebf ID={nextId}"
                        : $"\u65b0\u914d\u7f6e\u4e0a\u7ebf ID={nextId} {name}";
                    var message = BuildConfigMessage(nextId, url, checkResult.Details);
                    await SendHelper.SendPushDeer(title, message);

                    detectedIds.Add(nextId);
                    lastDetectedId = nextId;
                    consecutiveMisses = 0;
                }

                if (detectedIds.Count > 0)
                {
                    await SaveLastIdAsync(lastDetectedId, cancellationToken);
                    _lastKnownId = lastDetectedId;
                    return new FetchActivityResult(
                        true,
                        200,
                        $"New config detected. IDs={string.Join(",", detectedIds)}");
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

        private static async Task<FetchActivityResult> EnsureInitializedAsync(CancellationToken cancellationToken)
        {
            if (_initialized)
            {
                return new FetchActivityResult(true, 200, "Config ID monitor initialized.");
            }

            var configuredStartId = GetEnvInt("CONFIG_ID_START", DefaultStartId);
            var scanLimit = GetEnvInt("CONFIG_ID_BACKWARD_SCAN_LIMIT", DefaultBackwardScanLimit);

            var storedId = await LoadLastIdAsync(cancellationToken);
            var scanStartId = Math.Max(configuredStartId, storedId ?? 0);

            var latestId = await FindLatestAvailableIdAsync(scanStartId, scanLimit, cancellationToken);
            if (latestId is null)
            {
                return new FetchActivityResult(false, 404, $"Unable to locate a valid config ID from {scanStartId} (scan limit {scanLimit}).");
            }

            _lastKnownId = latestId.Value;
            await SaveLastIdAsync(latestId.Value, cancellationToken);
            _initialized = true;
            return new FetchActivityResult(true, 200, $"Config ID initialized. Last ID={latestId.Value}");
        }

        private static async Task<int?> FindLatestAvailableIdAsync(int startId, int scanLimit, CancellationToken cancellationToken)
        {
            var currentId = startId;
            for (var i = 0; i <= scanLimit && currentId > 0; i++, currentId--)
            {
                var result = await CheckIdHasContentAsync(currentId, cancellationToken);
                if (result.HasContent)
                {
                    return currentId;
                }
            }

            return null;
        }

        private static async Task<(bool HasContent, ConfigDetails? Details)> CheckIdHasContentAsync(int id, CancellationToken cancellationToken)
        {
            var url = BuildUrl(id);
            Console.WriteLine($"[HTTP] GET {url}");
            using var httpClient = CreateHttpClient();
            using var response = await httpClient.GetAsync(url, cancellationToken);
            var html = await HttpContentReader.ReadAsStringSafeAsync(response.Content, cancellationToken);

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

            var details = await ExtractConfigDetailsAsync(httpClient, url, doc, html, cancellationToken);
            return (true, details);
        }

        private static bool HasConfigContent(HtmlDocument doc)
        {
            var osNode = FindLabelNode(doc, LabelOs);
            var cpuNode = FindLabelNode(doc, "CPU");
            return osNode != null && cpuNode != null;
        }

        private static async Task<ConfigDetails> ExtractConfigDetailsAsync(HttpClient httpClient, string pageUrl, HtmlDocument doc, string html, CancellationToken cancellationToken)
        {
            var anchorNode = FindLabelNode(doc, LabelOs);
            var name = ExtractProductName(doc, anchorNode);
            var price = ExtractPrice(doc, html);
            var orderSummaryInfo = await ExtractOrderSummaryInfoAsync(httpClient, pageUrl, doc, cancellationToken);
            if (string.IsNullOrWhiteSpace(price))
            {
                price = orderSummaryInfo.Price;
            }
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var label in ConfigFieldLabels)
            {
                if (string.Equals(label, "\u5e74\u9650", StringComparison.Ordinal))
                {
                    continue;
                }

                var labelNode = FindFieldLabelNode(doc, label);
                if (labelNode is null)
                {
                    continue;
                }

                var value = NormalizeFieldValue(label, FindNextMeaningfulText(labelNode));
                if (!string.IsNullOrWhiteSpace(value))
                {
                    fields[label] = value;
                }
            }

            var term = ExtractTerm(doc, html);
            if (!string.IsNullOrWhiteSpace(term))
            {
                fields["\u5e74\u9650"] = term;
            }

            if (!string.IsNullOrWhiteSpace(orderSummaryInfo.Stock))
            {
                fields["\u5e93\u5b58"] = orderSummaryInfo.Stock;
            }

            return new ConfigDetails(name, price, fields);
        }

        private static string BuildConfigMessage(int id, string url, ConfigDetails? details)
        {
            var lines = new List<string>
            {
                $"ID: {id}"
            };

            if (details != null)
            {
                if (!string.IsNullOrWhiteSpace(details.Price))
                {
                    lines.Add($"\u4ef7\u683c: {details.Price}");
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

        private static string? ExtractPrice(HtmlDocument doc, string html)
        {
            var priceNode = FindNodeByHints(doc, PriceClassHints);
            if (priceNode != null)
            {
                var price = ExtractPriceFromNode(priceNode, allowBareNumber: true);
                if (!string.IsNullOrWhiteSpace(price))
                {
                    return price;
                }
            }

            foreach (var label in PriceLabelCandidates)
            {
                var labelNode = FindLabelNode(doc, label);
                if (labelNode == null)
                {
                    continue;
                }

                var value = FindNextMeaningfulText(labelNode);
                var price = ExtractPriceFromText(value, allowBareNumber: true);
                if (!string.IsNullOrWhiteSpace(price))
                {
                    return price;
                }
            }

            var match = Regex.Match(html, "data-current\\s*=\\s*[\"']?(?<value>[0-9]+(?:\\.[0-9]+)?)[\"']?", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups["value"].Value;
            }

            match = Regex.Match(html, "\"(?:price|total|amount)\"\\s*:\\s*\"?(?<value>[0-9]+(?:\\.[0-9]+)?)\"?", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups["value"].Value;
            }

            return null;
        }

        private static async Task<OrderSummaryInfo> ExtractOrderSummaryInfoAsync(HttpClient httpClient, string pageUrl, HtmlDocument doc, CancellationToken cancellationToken)
        {
            var formNode = doc.DocumentNode.SelectSingleNode("//form[contains(concat(' ', normalize-space(@class), ' '), ' configoption_form ')]");
            if (formNode == null)
            {
                return new OrderSummaryInfo(null, null);
            }

            var formValues = BuildOrderSummaryFormValues(formNode);
            if (formValues.Count == 0)
            {
                return new OrderSummaryInfo(null, null);
            }

            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri))
            {
                return new OrderSummaryInfo(null, null);
            }

            var builder = new UriBuilder(pageUri)
            {
                Query = $"action=ordersummary&order_frm_tpl=&tpl_type=&date={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, builder.Uri)
            {
                Content = new FormUrlEncodedContent(formValues)
            };
            request.Headers.Referrer = pageUri;

            using var response = await httpClient.SendAsync(request, cancellationToken);
            var summaryHtml = await HttpContentReader.ReadAsStringSafeAsync(response.Content, cancellationToken);
            if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(summaryHtml))
            {
                return new OrderSummaryInfo(null, null);
            }

            var summaryDoc = new HtmlDocument();
            summaryDoc.LoadHtml(summaryHtml);

            var stock = ExtractStockFromOrderSummary(summaryHtml);
            var displayedPrice = ExtractOrderSummaryDisplayedPrice(summaryDoc);
            if (!string.IsNullOrWhiteSpace(displayedPrice))
            {
                return new OrderSummaryInfo(displayedPrice, stock);
            }

            var price = ExtractPriceFromText(summaryHtml, allowBareNumber: false);
            if (!string.IsNullOrWhiteSpace(price))
            {
                return new OrderSummaryInfo(price, stock);
            }

            var priceNode = FindNodeByHints(summaryDoc, PriceClassHints);
            return new OrderSummaryInfo(
                priceNode == null ? null : ExtractPriceFromNode(priceNode, allowBareNumber: true),
                stock);
        }

        private static string? ExtractStockFromOrderSummary(string summaryHtml)
        {
            if (string.IsNullOrWhiteSpace(summaryHtml))
            {
                return null;
            }

            var match = Regex.Match(
                summaryHtml,
                @"var\s+products\s*=\s*(?<json>\{.*?\});",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(match.Groups["json"].Value);
                var root = document.RootElement;

                var hasStockControl = TryGetInt32(root, "stock_control", out var stockControl);
                var hasQty = TryGetInt32(root, "qty", out var qty);

                if (hasStockControl && stockControl == 1)
                {
                    if (hasQty)
                    {
                        return qty <= 0 ? "\u65e0\u5e93\u5b58" : qty.ToString(CultureInfo.InvariantCulture);
                    }

                    return "\u5e93\u5b58\u63a7\u5236";
                }

                if (hasQty)
                {
                    return qty.ToString(CultureInfo.InvariantCulture);
                }

                return hasStockControl ? "\u4e0d\u9650\u91cf" : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool TryGetInt32(JsonElement root, string propertyName, out int value)
        {
            value = 0;
            if (!root.TryGetProperty(propertyName, out var property))
            {
                return false;
            }

            if (property.ValueKind == JsonValueKind.Number)
            {
                return property.TryGetInt32(out value);
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                return int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            }

            return false;
        }

        private static List<KeyValuePair<string, string>> BuildOrderSummaryFormValues(HtmlNode formNode)
        {
            var values = new List<KeyValuePair<string, string>>();

            var inputNodes = formNode.SelectNodes(".//input[@name]");
            if (inputNodes != null)
            {
                foreach (var inputNode in inputNodes)
                {
                    var name = inputNode.GetAttributeValue("name", string.Empty);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var type = inputNode.GetAttributeValue("type", string.Empty);
                    if ((type.Equals("radio", StringComparison.OrdinalIgnoreCase) || type.Equals("checkbox", StringComparison.OrdinalIgnoreCase))
                        && inputNode.GetAttributeValue("checked", null) == null)
                    {
                        continue;
                    }

                    var value = inputNode.GetAttributeValue("value", string.Empty);
                    if (string.IsNullOrWhiteSpace(value)
                        && type.Equals("hidden", StringComparison.OrdinalIgnoreCase)
                        && inputNode.GetAttributeValue("data-type", string.Empty).Equals("skyos", StringComparison.OrdinalIgnoreCase))
                    {
                        value = ResolveDefaultSkyOsValue(formNode, inputNode);
                    }

                    values.Add(new KeyValuePair<string, string>(
                        name,
                        value));
                }
            }

            var selectNodes = formNode.SelectNodes(".//select[@name]");
            if (selectNodes != null)
            {
                foreach (var selectNode in selectNodes)
                {
                    var name = selectNode.GetAttributeValue("name", string.Empty);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var optionNode = selectNode.SelectSingleNode(".//option[@selected]") ?? selectNode.SelectSingleNode(".//option");
                    if (optionNode == null)
                    {
                        continue;
                    }

                    values.Add(new KeyValuePair<string, string>(
                        name,
                        optionNode.GetAttributeValue("value", string.Empty)));
                }
            }

            var textareaNodes = formNode.SelectNodes(".//textarea[@name]");
            if (textareaNodes != null)
            {
                foreach (var textareaNode in textareaNodes)
                {
                    var name = textareaNode.GetAttributeValue("name", string.Empty);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    values.Add(new KeyValuePair<string, string>(
                        name,
                        NormalizeText(textareaNode.InnerText)));
                }
            }

            return values;
        }

        private static string ResolveDefaultSkyOsValue(HtmlNode formNode, HtmlNode inputNode)
        {
            var inputId = inputNode.GetAttributeValue("id", string.Empty);
            if (string.IsNullOrWhiteSpace(inputId))
            {
                return string.Empty;
            }

            var selectedNode = formNode.SelectSingleNode($"(.//li[@data-id='{inputId}' and contains(concat(' ', normalize-space(@class), ' '), ' active ')])[1]");
            if (selectedNode != null)
            {
                return selectedNode.GetAttributeValue("data-osid", string.Empty);
            }

            var firstNode = formNode.SelectSingleNode($"(.//li[@data-id='{inputId}'])[1]");
            return firstNode?.GetAttributeValue("data-osid", string.Empty) ?? string.Empty;
        }

        private static HtmlNode? FindNodeByHints(HtmlDocument doc, string[] hints)
        {
            var predicates = string.Join(" or ", hints.Select(h =>
                $"contains(translate(@class,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'{h}') or contains(translate(@id,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'{h}')"));
            if (string.IsNullOrWhiteSpace(predicates))
            {
                return null;
            }

            return doc.DocumentNode.SelectSingleNode($"//*[{predicates}]");
        }

        private static string? NormalizeFieldValue(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return label switch
            {
                "\u7cfb\u7edf\u76d8" => ExtractDefaultSizedValue(value),
                "\u6570\u636e\u76d8" => ExtractDefaultSizedValue(value),
                "\u5e26\u5bbd" => ExtractDefaultBandwidthValue(value),
                _ => value
            };
        }

        private static string ExtractDefaultSizedValue(string value)
        {
            var normalized = NormalizeText(value);
            var matches = Regex.Matches(
                normalized,
                @"\d+(?:\.\d+)?\s*(?:Kbps|Mbps|Gbps|Tbps|MB|GB|TB|GiB|MiB)",
                RegexOptions.IgnoreCase);

            if (matches.Count > 0)
            {
                return matches[0].Value.Replace(" ", string.Empty);
            }

            return normalized;
        }

        private static string ExtractDefaultBandwidthValue(string value)
        {
            var normalized = NormalizeText(value);
            var matches = Regex.Matches(
                normalized,
                @"\d+(?:\.\d+)?\s*(?:Kbps|Mbps|Gbps|Tbps)",
                RegexOptions.IgnoreCase);

            if (matches.Count > 0)
            {
                return matches[0].Value.Replace(" ", string.Empty);
            }

            var numberMatch = Regex.Match(normalized, @"\d+(?:\.\d+)?");
            if (numberMatch.Success)
            {
                return $"{numberMatch.Value}Mbps";
            }

            return normalized;
        }

        private static string? ExtractOrderSummaryDisplayedPrice(HtmlDocument summaryDoc)
        {
            var priceNode = summaryDoc.DocumentNode.SelectSingleNode(
                "//*[contains(concat(' ', normalize-space(@class), ' '), ' ordersummarybottom-price ')]");
            if (priceNode == null)
            {
                return null;
            }

            var priceText = NormalizeText(priceNode.InnerText).Replace(",", string.Empty);
            var priceMatch = Regex.Match(priceText, @"[0-9]+(?:\.[0-9]+)?");
            if (!priceMatch.Success)
            {
                return null;
            }

            var prefixNode = summaryDoc.DocumentNode.SelectSingleNode(
                "//*[contains(concat(' ', normalize-space(@class), ' '), ' ordersummarybottom-prefix ')]");
            var prefix = NormalizeText(prefixNode?.InnerText);
            return string.IsNullOrWhiteSpace(prefix) ? priceMatch.Value : $"{prefix}{priceMatch.Value}";
        }

        private static string? ExtractPriceFromNode(HtmlNode node, bool allowBareNumber)
        {
            foreach (var attr in node.Attributes)
            {
                if (attr == null)
                {
                    continue;
                }

                var name = attr.Name ?? string.Empty;
                if (name.Contains("price", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("amount", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("total", StringComparison.OrdinalIgnoreCase))
                {
                    var price = ExtractPriceFromText(attr.Value, allowBareNumber);
                    if (!string.IsNullOrWhiteSpace(price))
                    {
                        return price;
                    }
                }
            }

            return ExtractPriceFromText(node.InnerText, allowBareNumber);
        }

        private static string? ExtractPriceFromText(string? text, bool allowBareNumber)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var normalized = NormalizeText(text);
            var matches = Regex.Matches(normalized, "(?:\\u00a5|\\uffe5)\\s*(?<value>[0-9]+(?:\\.[0-9]+)?)|(?<value2>[0-9]+(?:\\.[0-9]+)?)\\s*\\u5143");
            foreach (Match match in matches)
            {
                if (!match.Success)
                {
                    continue;
                }

                return match.Value.Replace(",", string.Empty);
            }

            if (!allowBareNumber)
            {
                return null;
            }

            matches = Regex.Matches(normalized, "[0-9]+(?:\\.[0-9]+)?");
            foreach (Match match in matches)
            {
                if (!match.Success)
                {
                    continue;
                }

                return match.Value;
            }

            return null;
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

        private static HtmlNode? FindFieldLabelNode(HtmlDocument doc, string label)
        {
            if (FieldLabelCandidates.TryGetValue(label, out var candidates))
            {
                foreach (var candidate in candidates)
                {
                    var node = FindLabelNode(doc, candidate);
                    if (node != null)
                    {
                        return node;
                    }
                }

                return null;
            }

            return FindLabelNode(doc, label);
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

        private static string? ExtractTerm(HtmlDocument doc, string html)
        {
            foreach (var label in TermLabelCandidates)
            {
                var labelNode = FindLabelNode(doc, label);
                if (labelNode == null)
                {
                    continue;
                }

                var value = FindNextMeaningfulText(labelNode);
                var normalizedValue = NormalizeTerm(value);
                if (!string.IsNullOrWhiteSpace(normalizedValue))
                {
                    return normalizedValue;
                }
            }

            var text = NormalizeText(doc.DocumentNode.InnerText);
            var textMatch = Regex.Match(text, @"(?<term>一次性|(?:\d+|[一二三四五六七八九十百两个]+)(?:个?月|年|天))");
            if (textMatch.Success)
            {
                return textMatch.Groups["term"].Value;
            }

            var htmlMatch = Regex.Match(html, @"(?<term>一次性|(?:\d+|[一二三四五六七八九十百两个]+)(?:个?月|年|天))");
            return htmlMatch.Success ? htmlMatch.Groups["term"].Value : null;
        }

        private static string? NormalizeTerm(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = NormalizeText(value);
            var match = Regex.Match(normalized, @"(?<term>一次性|(?:\d+|[一二三四五六七八九十百两个]+)(?:个?月|年|天))");
            return match.Success ? match.Groups["term"].Value : null;
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
