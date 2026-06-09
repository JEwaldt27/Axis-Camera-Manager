using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using AxisManager.Models;

namespace AxisManager.Services;

public enum ProbeResult
{
    Connected,    // Ready — either no password needed or credentials worked
    NeedsSetup,   // Factory default — no password set (Axis-Setup: vapix header)
    AuthFailed,   // Has a password, credentials wrong
    Unreachable   // Could not connect at all
}

public class VapixService : IDisposable
{
    private readonly HttpClient _http;     // authenticated requests
    private readonly HttpClient _rawHttp;  // credential-free probe requests
    private readonly string     _ip;
    private readonly string     _user;
    private readonly string     _pass;
    private bool                _noAuth;   // true when camera has no password

    public VapixService(string ip, string username = "root", string password = "")
    {
        _ip   = ip;
        _user = username;
        _pass = password;

        // ── Main client: used for all authenticated VAPIX calls ────────────
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            Credentials       = new NetworkCredential(username, password),
            PreAuthenticate   = false,
            AllowAutoRedirect = true,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        // ── Raw probe client: NO credentials, NO auto-redirect ─────────────
        // The main HttpClientHandler with Credentials set will automatically
        // retry a 401 with those credentials before returning the response —
        // meaning we would never see the 401 or the Axis-Setup header.
        // This separate client has no credentials so we get the raw 401 back.
        var rawHandler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            Credentials           = null,
            UseDefaultCredentials = false,
            PreAuthenticate       = false,
            AllowAutoRedirect     = false,
        };
        _rawHttp = new HttpClient(rawHandler) { Timeout = TimeSpan.FromSeconds(8) };
    }

    // ── Setup Detection ────────────────────────────────────────────────────

    /// <summary>
    /// Probes the camera to determine its setup state.
    ///
    /// Official Axis detection method: a factory-default camera returns 401
    /// with the response header  Axis-Setup: vapix  on any VAPIX call.
    /// We use a credential-free HttpClient so the 401 is never auto-retried.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync()
    {
        try
        {
            var url  = $"http://{_ip}/axis-cgi/param.cgi?action=list&group=Brand";
            var resp = await TryRawGetAsync(url);

            if (resp is null)
                resp = await TryRawGetAsync(url.Replace("http://", "https://"));

            if (resp is null)
                return ProbeResult.Unreachable;

            // ── Official detection: Axis-Setup: vapix header ───────────────
            // Present on 401 when no password has been set on the device.
            if (HasAxisSetupHeader(resp))
                return ProbeResult.NeedsSetup;

            // ── 200 with no auth — very old firmware with blank password ────
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                if (body.Contains("root.Brand"))
                {
                    _noAuth = true;
                    return ProbeResult.Connected;
                }
            }

            // ── 401 without Axis-Setup — camera has a password, try creds ──
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                try
                {
                    var text = await FetchWithAuth(url);
                    if (text.Contains("root.Brand"))
                        return ProbeResult.Connected;
                }
                catch (HttpRequestException ex)
                    when (ex.StatusCode is HttpStatusCode.Unauthorized
                                       or HttpStatusCode.Forbidden)
                {
                    return ProbeResult.AuthFailed;
                }
                catch (Exception ex) when (IsAuthMessage(ex.Message))
                {
                    return ProbeResult.AuthFailed;
                }
            }

            return ProbeResult.AuthFailed;
        }
        catch
        {
            return ProbeResult.Unreachable;
        }
    }

    private static bool HasAxisSetupHeader(HttpResponseMessage resp)
    {
        if (resp.Headers.TryGetValues("Axis-Setup", out var vals))
            foreach (var v in vals)
                if (v.Contains("vapix", StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    // ── First-Time Setup — Set Initial Password ────────────────────────────

    public async Task SetInitialPasswordAsync(string newPassword)
    {
        // AXIS OS 11.6+: root user doesn't exist, create a new user
        try { await CreateInitialUserModernAsync("root", newPassword); return; }
        catch { }

        // AXIS OS 9–11.5: set root password via JSON API
        try { await SetRootPasswordJsonAsync(newPassword); return; }
        catch { }

        // AXIS OS 7–8: legacy CGI
        await SetRootPasswordLegacyAsync(newPassword);
    }

    private async Task CreateInitialUserModernAsync(string username, string password)
    {
        var json = $$"""
        {
            "apiVersion": "1.0",
            "method": "createUser",
            "params": {
                "username": "{{username}}",
                "password": "{{password}}",
                "privileges": ["administrator", "operator", "viewer"]
            }
        }
        """;
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        // Use _rawHttp — no password exists yet so no auth needed
        var resp = await _rawHttp.PostAsync(
            $"http://{_ip}/axis-cgi/basicdeviceinfo.cgi", content);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        if (body.Contains("\"error\""))
            throw new InvalidOperationException($"CreateUser failed: {body}");
    }

    private async Task SetRootPasswordJsonAsync(string password)
    {
        var json = $$"""
        {
            "apiVersion": "1.0",
            "method": "setPrimaryCredentials",
            "params": {
                "credentials": [{"role": "root", "password": "{{password}}"}]
            }
        }
        """;
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        // Use _rawHttp — no password exists yet
        var resp = await _rawHttp.PostAsync(
            $"http://{_ip}/axis-cgi/usermanagement.cgi", content);
        resp.EnsureSuccessStatusCode();
    }

    private async Task SetRootPasswordLegacyAsync(string password)
    {
        var enc = Uri.EscapeDataString(password);
        var url = $"http://{_ip}/axis-cgi/pwdgrp.cgi" +
                  $"?action=add&user=root&grp=root" +
                  $"&sgrp=admin:operator:viewer:ptz" +
                  $"&pwd={enc}&rpwd={enc}";
        // Use _rawHttp — no auth needed on factory default
        var resp = await _rawHttp.GetAsync(url);
        resp.EnsureSuccessStatusCode();
    }

    // ── Core param.cgi ─────────────────────────────────────────────────────

    public async Task<Dictionary<string, string>> GetParamsAsync(string? group = null)
    {
        var url = BuildParamUrl("list", group is null
            ? null
            : new Dictionary<string, string> { ["group"] = group });

        var text = _noAuth
            ? await FetchNoAuth(url)
            : await FetchWithAuth(url);

        return ParseParams(text);
    }

    public async Task<string> SetParamsAsync(Dictionary<string, string> values)
        => await FetchWithAuth(BuildParamUrl("update", values));

    public async Task SetStaticIpAsync(string ip, string subnet, string gateway,
                                        string? hostname = null)
    {
        var p = new Dictionary<string, string>
        {
            ["Network.BootProto"]     = "none",
            ["Network.IPAddress"]     = ip,
            ["Network.SubnetMask"]    = subnet,
            ["Network.DefaultRouter"] = gateway,
        };
        if (hostname != null) p["Network.HostName"] = hostname;
        await SetParamsAsync(p);
    }

    public async Task SetDhcpAsync()
        => await SetParamsAsync(new Dictionary<string, string>
            { ["Network.BootProto"] = "dhcp" });

    public async Task RestartAsync()
    {
        try { await FetchWithAuth($"http://{_ip}/axis-cgi/restart.cgi"); }
        catch { }
    }

    public string GetSnapshotUrl() => $"http://{_ip}/axis-cgi/jpg/image.cgi";
    public string GetWebUrl()      => $"http://{_ip}";

    // ── HTTP Fetching ──────────────────────────────────────────────────────

    private async Task<string> FetchNoAuth(string url)
    {
        var resp = await _rawHttp.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    private async Task<string> FetchWithAuth(string url)
    {
        var req1 = new HttpRequestMessage(HttpMethod.Get, url);
        HttpResponseMessage resp1;
        try
        {
            resp1 = await _http.SendAsync(req1);
        }
        catch
        {
            url   = url.Replace("http://", "https://");
            req1  = new HttpRequestMessage(HttpMethod.Get, url);
            resp1 = await _http.SendAsync(req1);
        }

        if (resp1.IsSuccessStatusCode)
            return await resp1.Content.ReadAsStringAsync();

        if (resp1.StatusCode == HttpStatusCode.Unauthorized)
        {
            var authHeader   = resp1.Headers.WwwAuthenticate.ToString();
            var digestHeader = BuildDigestHeader(authHeader, url, "GET");

            var req2 = new HttpRequestMessage(HttpMethod.Get, url);
            req2.Headers.Authorization = AuthenticationHeaderValue.Parse(digestHeader);
            var resp2 = await _http.SendAsync(req2);

            if (resp2.StatusCode == HttpStatusCode.Unauthorized)
                throw new HttpRequestException("401 Unauthorized", null,
                    HttpStatusCode.Unauthorized);

            resp2.EnsureSuccessStatusCode();
            return await resp2.Content.ReadAsStringAsync();
        }

        // Basic auth fallback
        var req3  = new HttpRequestMessage(HttpMethod.Get, url);
        var creds = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_user}:{_pass}"));
        req3.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);
        var resp3 = await _http.SendAsync(req3);

        if (resp3.StatusCode == HttpStatusCode.Unauthorized)
            throw new HttpRequestException("401 Unauthorized", null,
                HttpStatusCode.Unauthorized);

        resp3.EnsureSuccessStatusCode();
        return await resp3.Content.ReadAsStringAsync();
    }

    // Non-throwing GET using the raw (no-credential) client
    private async Task<HttpResponseMessage?> TryRawGetAsync(string url)
    {
        try { return await _rawHttp.GetAsync(url); }
        catch { return null; }
    }

    // ── Digest ─────────────────────────────────────────────────────────────

    private string BuildDigestHeader(string wwwAuth, string url, string method)
    {
        var realm  = ExtractQuoted(wwwAuth, "realm")  ?? "AXIS_WEB_AUTH";
        var nonce  = ExtractQuoted(wwwAuth, "nonce")  ?? Guid.NewGuid().ToString("N");
        var qop    = ExtractQuoted(wwwAuth, "qop");
        var uri    = new Uri(url).PathAndQuery;
        var ha1    = Md5Hex($"{_user}:{realm}:{_pass}");
        var ha2    = Md5Hex($"{method}:{uri}");
        var nc     = "00000001";
        var cnonce = Guid.NewGuid().ToString("N")[..8];

        if (qop?.Contains("auth") == true)
        {
            var r = Md5Hex($"{ha1}:{nonce}:{nc}:{cnonce}:auth:{ha2}");
            return $"Digest username=\"{_user}\", realm=\"{realm}\", " +
                   $"nonce=\"{nonce}\", uri=\"{uri}\", qop=auth, nc={nc}, " +
                   $"cnonce=\"{cnonce}\", response=\"{r}\"";
        }
        else
        {
            var r = Md5Hex($"{ha1}:{nonce}:{ha2}");
            return $"Digest username=\"{_user}\", realm=\"{realm}\", " +
                   $"nonce=\"{nonce}\", uri=\"{uri}\", response=\"{r}\"";
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

    private static bool IsAuthMessage(string msg)
    {
        var l = msg.ToLowerInvariant();
        return l.Contains("401") || l.Contains("unauthorized") ||
               l.Contains("403") || l.Contains("forbidden");
    }

    private string BuildParamUrl(string action, Dictionary<string, string>? extra = null)
    {
        var sb = new StringBuilder(
            $"http://{_ip}/axis-cgi/param.cgi?action={action}");
        if (extra != null)
            foreach (var kv in extra)
                sb.Append($"&{Uri.EscapeDataString(kv.Key)}" +
                           $"={Uri.EscapeDataString(kv.Value)}");
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

    public void Dispose()
    {
        _http.Dispose();
        _rawHttp.Dispose();
    }
}
