using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AxisManager.Models;

namespace AxisManager.Services;

public enum ProbeResult
{
    Connected,       // Auth succeeded, camera is ready
    NeedsSetup,      // Factory default — no password, setup wizard active
    AuthFailed,      // Camera responded but credentials were wrong
    Unreachable      // Could not connect at all
}

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
            Credentials      = new NetworkCredential(username, password),
            PreAuthenticate  = false,
            AllowAutoRedirect = true,
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    // ── Setup Detection ────────────────────────────────────────────────────

    /// <summary>
    /// Probes the camera to determine its setup state without throwing.
    /// Returns a ProbeResult so the caller can decide what UI to show.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync()
    {
        try
        {
            // Step 1 — Try to reach the root page to detect setup wizard
            var rootResp = await TryGetAsync($"http://{_ip}/");
            if (rootResp is null)
            {
                // HTTP failed, try HTTPS
                rootResp = await TryGetAsync($"https://{_ip}/");
            }

            if (rootResp is null)
                return ProbeResult.Unreachable;

            var body = await rootResp.Content.ReadAsStringAsync();

            // Step 2 — Detect setup wizard indicators
            if (IsSetupWizard(rootResp, body))
                return ProbeResult.NeedsSetup;

            // Step 3 — Try param.cgi with no auth (factory default has no password)
            var noAuthResp = await TryGetAsync(
                $"http://{_ip}/axis-cgi/param.cgi?action=list&group=Brand");

            if (noAuthResp?.IsSuccessStatusCode == true)
            {
                var paramBody = await noAuthResp.Content.ReadAsStringAsync();
                if (paramBody.Contains("root.Brand"))
                    return ProbeResult.Connected; // No password needed
            }

            // Step 4 — Try with provided credentials
            try
            {
                var text = await GetWithDigestAsync(
                    $"http://{_ip}/axis-cgi/param.cgi?action=list&group=Brand");
                if (text.Contains("root.Brand"))
                    return ProbeResult.Connected;
            }
            catch (HttpRequestException ex) when (
                ex.StatusCode == HttpStatusCode.Unauthorized ||
                ex.StatusCode == HttpStatusCode.Forbidden)
            {
                return ProbeResult.AuthFailed;
            }
            catch (Exception ex) when (IsAuthException(ex))
            {
                return ProbeResult.AuthFailed;
            }

            return ProbeResult.AuthFailed;
        }
        catch
        {
            return ProbeResult.Unreachable;
        }
    }

    private static bool IsSetupWizard(HttpResponseMessage resp, string body)
    {
        // Axis setup wizard indicators
        var lower = body.ToLowerInvariant();
        if (lower.Contains("setup wizard") ||
            lower.Contains("create account") ||
            lower.Contains("set password") ||
            lower.Contains("initial configuration") ||
            lower.Contains("\"/setup\"") ||
            lower.Contains("welcome to axis"))
            return true;

        // Some firmware redirects to /setup or /config on first boot
        var url = resp.RequestMessage?.RequestUri?.AbsolutePath ?? "";
        if (url.StartsWith("/setup", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("/config", StringComparison.OrdinalIgnoreCase))
            return true;

        // Axis OS 10+ uses a React-based wizard that checks for specific meta tags
        if (lower.Contains("axis-setup") || lower.Contains("axissetup"))
            return true;

        return false;
    }

    private async Task<HttpResponseMessage?> TryGetAsync(string url)
    {
        try
        {
            var req  = new HttpRequestMessage(HttpMethod.Get, url);
            var resp = await _http.SendAsync(req);
            return resp;
        }
        catch
        {
            return null;
        }
    }

    // ── Setup Wizard — Set Initial Password ───────────────────────────────

    /// <summary>
    /// Calls the Axis first-boot API to set the root password on a factory-default camera.
    /// Works on AXIS OS 7.x through 11.x.
    /// </summary>
    public async Task SetInitialPasswordAsync(string newPassword)
    {
        // Modern AXIS OS (9+): JSON API
        try
        {
            await SetInitialPasswordJsonAsync(newPassword);
            return;
        }
        catch { }

        // Legacy AXIS OS (7-8): CGI form post
        await SetInitialPasswordLegacyAsync(newPassword);
    }

    private async Task SetInitialPasswordJsonAsync(string newPassword)
    {
        var json = $$$"""
        {
            "apiVersion": "1.0",
            "method": "setPrimaryCredentials",
            "params": {
                "credentials": [{
                    "role": "root",
                    "password": "{{{newPassword}}}"
                }]
            }
        }
        """;

        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync(
            $"http://{_ip}/axis-cgi/usermanagement.cgi", content);
        resp.EnsureSuccessStatusCode();
    }

    private async Task SetInitialPasswordLegacyAsync(string newPassword)
    {
        // Older firmware: POST to /operator/basic.shtml or param.cgi
        var encoded = Uri.EscapeDataString(newPassword);
        var url = $"http://{_ip}/axis-cgi/pwdgrp.cgi" +
                  $"?action=add&grp=root&sgrp=root:admin:operator:viewer:ptz" +
                  $"&pwd={encoded}&rpwd={encoded}&user=root&comment=";

        var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
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
            ["Network.BootProto"]     = "none",
            ["Network.IPAddress"]     = ip,
            ["Network.SubnetMask"]    = subnet,
            ["Network.DefaultRouter"] = gateway,
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
            await GetWithDigestAsync($"http://{_ip}/axis-cgi/restart.cgi");
        }
        catch { /* camera drops connection on restart — expected */ }
    }

    public string GetSnapshotUrl() => $"http://{_ip}/axis-cgi/jpg/image.cgi";
    public string GetWebUrl()      => $"http://{_ip}";

    // ── Digest Auth ────────────────────────────────────────────────────────

    private async Task<string> GetWithDigestAsync(string url)
    {
        var req1 = new HttpRequestMessage(HttpMethod.Get, url);
        HttpResponseMessage resp1;
        try
        {
            resp1 = await _http.SendAsync(req1);
        }
        catch
        {
            url  = url.Replace("http://", "https://");
            req1 = new HttpRequestMessage(HttpMethod.Get, url);
            resp1 = await _http.SendAsync(req1);
        }

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

        if (resp1.IsSuccessStatusCode)
            return await resp1.Content.ReadAsStringAsync();

        // Basic auth fallback
        var req3 = new HttpRequestMessage(HttpMethod.Get, url);
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
            var response = Md5Hex($"{ha1}:{nonce}:{nc}:{cnonce}:auth:{ha2}");
            return $"Digest username=\"{_user}\", realm=\"{realm}\", " +
                   $"nonce=\"{nonce}\", uri=\"{uri}\", qop=auth, nc={nc}, " +
                   $"cnonce=\"{cnonce}\", response=\"{response}\"";
        }
        else
        {
            var response = Md5Hex($"{ha1}:{nonce}:{ha2}");
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

    private static bool IsAuthException(Exception ex)
    {
        var msg = ex.Message.ToLowerInvariant();
        return msg.Contains("401") || msg.Contains("unauthorized") ||
               msg.Contains("403") || msg.Contains("forbidden");
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

    public void Dispose() => _http.Dispose();
}
