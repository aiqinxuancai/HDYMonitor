using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace HDYMonitor.Utils
{
    internal static class HttpContentReader
    {
        static HttpContentReader()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public static async Task<string> ReadAsStringSafeAsync(HttpContent content, CancellationToken cancellationToken = default)
        {
            var bytes = await content.ReadAsByteArrayAsync(cancellationToken);
            var encoding = ResolveEncoding(content.Headers.ContentType, bytes);
            return encoding.GetString(bytes);
        }

        private static Encoding ResolveEncoding(MediaTypeHeaderValue? contentType, byte[] bytes)
        {
            var charset = contentType?.CharSet;
            if (!string.IsNullOrWhiteSpace(charset))
            {
                var normalized = charset.Trim().Trim('"', '\'');
                try
                {
                    return Encoding.GetEncoding(normalized);
                }
                catch (ArgumentException)
                {
                }
            }

            var sniffedCharset = TryDetectCharsetFromMeta(bytes);
            if (!string.IsNullOrWhiteSpace(sniffedCharset))
            {
                try
                {
                    return Encoding.GetEncoding(sniffedCharset);
                }
                catch (ArgumentException)
                {
                }
            }

            return Encoding.UTF8;
        }

        private static string? TryDetectCharsetFromMeta(byte[] bytes)
        {
            var probeLength = Math.Min(bytes.Length, 4096);
            if (probeLength == 0)
            {
                return null;
            }

            var probe = Encoding.ASCII.GetString(bytes, 0, probeLength);
            var match = Regex.Match(
                probe,
                "<meta[^>]+charset\\s*=\\s*[\"']?(?<charset>[A-Za-z0-9_\\-]+)",
                RegexOptions.IgnoreCase);

            return match.Success ? match.Groups["charset"].Value : null;
        }
    }
}
