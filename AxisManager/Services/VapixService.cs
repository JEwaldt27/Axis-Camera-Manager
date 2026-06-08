using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace AxisManager.Services;

public class VapixService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string     _ip;
    private readonly string     _user;
    private readonly string     _pass;

    public VapixService(string ip, string username = "root", string password = "")
    {
        _ip   = ip;
        _user = username;
        _pass = password;

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            Credentials = new NetworkCredential(username, password),
            PreAuthenticate = false,
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    // ── Core param.cgi ─────────────────────────────────────────────────────

    public async Task<Dictionary<string, string>> GetParamsAsync(string? group = null)
    {
        var url = BuildParamUrl("list", group is null
            ? null
            : new Dictionary<string, string> { ["group"] = group });

        var text = await GetWithDigestAsync(url);
        return ParseParams(text);
    }

    public async Task<string> SetParamsAsync(Dictionary<string, string> values)
    {
        var url = BuildParamUrl("update", values);
        return await GetWithDigestAsync(url);
    }

    public async Task SetStaticIpAsync(string ip, string subnet, string gateway,
                                        string? hostname = null)
    {
        var p = new Dictionary<string, string>
        {
            ["Network.BootProto"]      = "none",
            ["Network.IPAddress"]      = ip,
            ["Network.SubnetMask"]     = subnet,
            ["Network.DefaultRouter"]  = gateway,
        };
        if (hostname != null) p["Network.HostName"] = hostname;
        await SetParamsAsync(p);
    }

    public async Task SetDhcpAsync()
    {
        await SetParamsAsync(new Dictionary<string, string>
        {
            ["Network.BootProto"] = "dhcp"
        });
    }

    public async Task RestartAsync()
    {
        try
        {
            var url = $"http://{_ip}/axis-cgi/restart.cgi";
            await GetWithDigestAsync(url);
        }
        catch { /* camera drops connection on restart — expected */ }
    }

    public string GetSnapshotUrl()  => $"http://{_ip}/axis-cgi/jpg/image.cgi";
    public string GetWebUrl()       => $"http://{_ip}";

    // ── Digest Auth ────────────────────────────────────────────────────────

    private async Task<string> GetWithDigestAsync(string url)
    {
        // First try without auth
        var req1 = new HttpRequestMessage(HttpMethod.Get, url);
        HttpResponseMessage resp1;
        try
        {
            resp1 = await _http.SendAsync(req1);
        }
        catch
        {
            // Fallback to HTTPS
            url = url.Replace("http://", "https://");
            req1 = new HttpRequestMessage(HttpMethod.Get, url);
            resp1 = await _http.SendAsync(req1);
        }

        if (resp1.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Parse WWW-Authenticate for digest
            var authHeader = resp1.Headers.WwwAuthenticate.ToString();
            var digestHeader = BuildDigestHeader(authHeader, url, "GET");

            var req2 = new HttpRequestMessage(HttpMethod.Get, url);
            req2.Headers.Authorization = AuthenticationHeaderValue.Parse(digestHeader);
            var resp2 = await _http.SendAsync(req2);
            resp2.EnsureSuccessStatusCode();
            return await resp2.Content.ReadAsStringAsync();
        }

        if (resp1.IsSuccessStatusCode)
            return await resp1.Content.ReadAsStringAsync();

        // Try basic auth as fallback
        var req3 = new HttpRequestMessage(HttpMethod.Get, url);
        var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_user}:{_pass}"));
        req3.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);
        var resp3 = await _http.SendAsync(req3);
        resp3.EnsureSuccessStatusCode();
        return await resp3.Content.ReadAsStringAsync();
    }

    private string BuildDigestHeader(string wwwAuth, string url, string method)
    {
        // Extract realm and nonce from WWW-Authenticate: Digest realm="...", nonce="..."
        var realm = ExtractQuoted(wwwAuth, "realm") ?? "AXIS_WEB_AUTH";
        var nonce = ExtractQuoted(wwwAuth, "nonce") ?? Guid.NewGuid().ToString("N");
        var qop   = ExtractQuoted(wwwAuth, "qop");

        var uri = new Uri(url).PathAndQuery;

        var ha1 = Md5Hex($"{_user}:{realm}:{_pass}");
        var ha2 = Md5Hex($"{method}:{uri}");

        string response;
        string nc    = "00000001";
        string cnonce = Guid.NewGuid().ToString("N")[..8];

        if (qop != null && (qop.Contains("auth")))
        {
            response = Md5Hex($"{ha1}:{nonce}:{nc}:{cnonce}:auth:{ha2}");
            return $"Digest username=\"{_user}\", realm=\"{realm}\", " +
                   $"nonce=\"{nonce}\", uri=\"{uri}\", qop=auth, nc={nc}, " +
                   $"cnonce=\"{cnonce}\", response=\"{response}\"";
        }
        else
        {
            response = Md5Hex($"{ha1}:{nonce}:{ha2}");
            return $"Digest username=\"{_user}\", realm=\"{realm}\", " +
                   $"nonce=\"{nonce}\", uri=\"{uri}\", response=\"{response}\"";
        }
    }

    private static string? ExtractQuoted(string s, string key)
    {
        var idx = s.IndexOf($"{key}=\"", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var start = idx + key.Length + 2;
        var end   = s.IndexOf('"', start);
        return end < 0 ? null : s[start..end];
    }

    private static string Md5Hex(string input)
    {
        var bytes = System.Security.Cryptography.MD5
            .HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private string BuildParamUrl(string action, Dictionary<string, string>? extra = null)
    {
        var sb = new StringBuilder($"http://{_ip}/axis-cgi/param.cgi?action={action}");
        if (extra != null)
            foreach (var kv in extra)
                sb.Append($"&{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}");
        return sb.ToString();
    }

    private static Dictionary<string, string> ParseParams(string text)
    {
        var result = new Dictionary<string, string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = line.IndexOf('=');
            if (idx > 0)
                result[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }
        return result;
    }

    public void Dispose() => _http.Dispose();
}
