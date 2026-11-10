using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BetterRawAccel.Core
{
    public static class ProfileSharing
    {
        const string Prefix = "RA1:";

        public static string ExportCode(Profile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            var dto = Engine.ToDto(profile);
            string json = dto.ToString(Formatting.None);
            byte[] raw = Encoding.UTF8.GetBytes(json);

            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(output, CompressionMode.Compress, true))
                {
                    deflate.Write(raw, 0, raw.Length);
                }
                byte[] compressed = output.ToArray();
                string b64 = Convert.ToBase64String(compressed)
                    .Replace('+', '-')
                    .Replace('/', '_')
                    .TrimEnd('=');
                return Prefix + b64;
            }
        }

        public static JObject ImportCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Share code cannot be empty");
            code = code.Trim();
            if (code.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                code = code.Substring(Prefix.Length);

            // Restore base64 padding
            string b64 = code.Replace('-', '+').Replace('_', '/');
            int mod = b64.Length % 4;
            if (mod == 2) b64 += "==";
            else if (mod == 3) b64 += "=";

            byte[] compressed;
            try
            {
                compressed = Convert.FromBase64String(b64);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException("Invalid base64 encoding in share code: " + e.Message);
            }

            using (var input = new MemoryStream(compressed))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var reader = new StreamReader(deflate, Encoding.UTF8))
            {
                string json = reader.ReadToEnd();
                return JObject.Parse(json);
            }
        }
    }
}
