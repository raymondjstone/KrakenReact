using System;
using System.Text.RegularExpressions;

namespace KrakenReact.Server.Utils
{
    public static class ClientOrderId
    {
        // Allowed characters: letters, digits, underscore, hyphen
        private static readonly Regex _allowed = new("[^A-Za-z0-9_-]+", RegexOptions.Compiled);
        private const int MaxLength = 32;

        public static string Normalize(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return Generate();
            var cleaned = _allowed.Replace(id, string.Empty);
            if (cleaned.Length > MaxLength) cleaned = cleaned.Substring(0, MaxLength);
            if (string.IsNullOrEmpty(cleaned)) return Generate();
            return cleaned;
        }

        public static string Generate()
        {
            // 32 hex chars from Guid without dashes
            return Guid.NewGuid().ToString("N").Substring(0, MaxLength);
        }

        public static string GenerateWithPrefix(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return Generate();
            // Build prefix + timestamp + random tail to fit MaxLength
            var ts = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var tailLen = Math.Max(0, MaxLength - prefix.Length - ts.Length);
            var tail = Guid.NewGuid().ToString("N");
            if (tail.Length > tailLen) tail = tail.Substring(0, tailLen);
            var combined = (prefix + ts + tail).Substring(0, MaxLength);
            // Ensure allowed characters
            combined = _allowed.Replace(combined, string.Empty);
            if (combined.Length > MaxLength) combined = combined.Substring(0, MaxLength);
            if (string.IsNullOrEmpty(combined)) return Generate();
            return combined;
        }

        public static string GenerateTimestampWithPrefix(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) prefix = "CI";
            var ts = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var combined = (prefix + ts);
            if (combined.Length > MaxLength) combined = combined.Substring(0, MaxLength);
            combined = _allowed.Replace(combined, string.Empty);
            if (string.IsNullOrEmpty(combined)) return Generate();
            return combined;
        }
    }
}
