using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace OverWatchELD.VtcBot.Bot
{
    public sealed class EldSignedClient
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly string _secret;

        public EldSignedClient(HttpClient http, string baseUrl, string secret)
        {
            _http = http;
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _secret = (secret ?? "").Trim();
        }

        public async Task<JsonDocument?> GetAsync(string path)
        {
            var url = _baseUrl + path;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            var txt = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(txt) ? "{}" : txt); } catch { return null; }
        }

        public async Task<JsonDocument?> PostJsonAsync(string path, object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            var url = _baseUrl + path;

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            Sign(req, "POST", path, json);

            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            var txt = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(txt) ? "{}" : txt); } catch { return null; }
        }

        private void Sign(HttpRequestMessage req, string method, string path, string body)
        {
            if (string.IsNullOrWhiteSpace(_secret)) return; // auth disabled

            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var nonce = Guid.NewGuid().ToString("N");
            var bodyHash = Sha256Hex(body ?? "");

            var msg = $"{ts}.{nonce}.{method.ToUpperInvariant()}.{path.TrimEnd('/').ToLowerInvariant()}.{bodyHash}";
            var sig = HmacB64(_secret, msg);

            req.Headers.Remove("X-OW-Timestamp");
            req.Headers.Remove("X-OW-Nonce");
            req.Headers.Remove("X-OW-Signature");

            req.Headers.Add("X-OW-Timestamp", ts);
            req.Headers.Add("X-OW-Nonce", nonce);
            req.Headers.Add("X-OW-Signature", sig);
        }

        private static string Sha256Hex(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static string HmacB64(string secret, string msg)
        {
            var key = Encoding.UTF8.GetBytes(secret);
            var data = Encoding.UTF8.GetBytes(msg);
            var sig = HMACSHA256.HashData(key, data);
            return Convert.ToBase64String(sig);
        }
    }
}