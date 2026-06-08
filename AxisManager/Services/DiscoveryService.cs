using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AxisManager.Models;

namespace AxisManager.Services;

public static class DiscoveryService
{
    private const string MdnsAddr = "224.0.0.251";
    private const int    MdnsPort = 5353;

    private static readonly string[] Services =
    {
        "_vapix-https._tcp.local",
        "_vapix-http._tcp.local",
        "_axis-video._tcp.local",
    };

    // Runs the (blocking) socket work on a background thread so the UI thread
    // never stalls during the receive window. Throws on hard failures (e.g. the
    // port cannot be bound) so the caller can surface the error and recover.
    public static Task<List<CameraDevice>> ScanAsync(int timeoutSeconds = 4)
        => Task.Run(() => Scan(timeoutSeconds));

    private static List<CameraDevice> Scan(int timeoutSeconds)
    {
        var found = new Dictionary<string, CameraDevice>();

        using var sock = new Socket(AddressFamily.InterNetwork,
                                    SocketType.Dgram,
                                    ProtocolType.Udp);
        // ReuseAddress + non-exclusive use let us re-bind 5353 on repeated scans
        // (and coexist with other mDNS responders like Bonjour) instead of
        // throwing "address already in use" on the second scan.
        sock.SetSocketOption(SocketOptionLevel.Socket,
                             SocketOptionName.ReuseAddress, true);
        sock.ExclusiveAddressUse = false;

        // Join multicast group
        var mcastOpt = new MulticastOption(
            IPAddress.Parse(MdnsAddr), IPAddress.Any);
        sock.SetSocketOption(SocketOptionLevel.IP,
                             SocketOptionName.AddMembership, mcastOpt);
        sock.SetSocketOption(SocketOptionLevel.IP,
                             SocketOptionName.MulticastTimeToLive, 255);

        sock.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
        sock.ReceiveTimeout = 500;

        var dest = new IPEndPoint(IPAddress.Parse(MdnsAddr), MdnsPort);

        // Send queries
        foreach (var svc in Services)
        {
            var pkt = BuildQuery(svc);
            sock.SendTo(pkt, SocketFlags.None, dest);
        }

        // Collect responses
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var buf = new byte[4096];

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int len = sock.ReceiveFrom(buf, ref remote);
                var ip = ((IPEndPoint)remote).Address.ToString();

                // Skip link-local
                if (ip.StartsWith("169.254")) continue;

                if (!found.ContainsKey(ip))
                {
                    var name = ExtractDeviceName(buf, len) ?? "Axis Device";
                    var mac  = ExtractMacFromName(name);
                    found[ip] = new CameraDevice
                    {
                        Ip     = ip,
                        Name   = name,
                        Mac    = mac,
                        Status = "Discovered"
                    };
                }
            }
            catch (SocketException) { /* receive timeout — keep polling until deadline */ }
        }

        return new List<CameraDevice>(found.Values);
    }

    // ── Packet Building ────────────────────────────────────────────────────

    private static byte[] BuildQuery(string service)
    {
        var pkt = new List<byte>();
        // Header
        pkt.AddRange(new byte[] { 0x00, 0x00 }); // ID
        pkt.AddRange(new byte[] { 0x00, 0x00 }); // Flags
        pkt.AddRange(new byte[] { 0x00, 0x01 }); // Questions: 1
        pkt.AddRange(new byte[] { 0x00, 0x00 }); // Answers
        pkt.AddRange(new byte[] { 0x00, 0x00 }); // Authority
        pkt.AddRange(new byte[] { 0x00, 0x00 }); // Additional

        // Name
        foreach (var part in service.Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(part);
            pkt.Add((byte)bytes.Length);
            pkt.AddRange(bytes);
        }
        pkt.Add(0x00); // end of name

        pkt.AddRange(new byte[] { 0x00, 0x0c }); // Type PTR
        pkt.AddRange(new byte[] { 0x00, 0x01 }); // Class IN

        return pkt.ToArray();
    }

    // ── Response Parsing ───────────────────────────────────────────────────

    private static string? ExtractDeviceName(byte[] data, int length)
    {
        try
        {
            var text = Encoding.UTF8.GetString(data, 0, length);
            var match = Regex.Match(text, @"AXIS [A-Z0-9\-]+");
            if (match.Success) return match.Value;
        }
        catch { }
        return null;
    }

    private static string ExtractMacFromName(string name)
    {
        var clean = name.Replace(":", "").Replace("-", "");
        var match = Regex.Match(clean, @"([0-9a-fA-F]{12})$");
        if (!match.Success) return "Unknown";
        var mac = match.Value.ToUpper();
        return string.Join(":", Enumerable.Range(0, 6)
            .Select(i => mac.Substring(i * 2, 2)));
    }
}
