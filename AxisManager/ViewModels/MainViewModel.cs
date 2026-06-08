using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using AxisManager.Models;
using AxisManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AxisManager.ViewModels;

public partial class MainViewModel : ObservableObject
{
    // ── Camera list ────────────────────────────────────────────────────────

    [ObservableProperty] private ObservableCollection<CameraDevice> _cameras = [];
    [ObservableProperty] private CameraDevice? _selectedCamera;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _statusText = "Ready  —  scan or add a camera manually";
    [ObservableProperty] private string _manualIp = "";

    // ── Connection ─────────────────────────────────────────────────────────

    [ObservableProperty] private string _username = "root";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string _connectionStatus = "●";
    [ObservableProperty] private string _connectionStatusClass = "dim";

    // ── Auth failure state ─────────────────────────────────────────────────

    [ObservableProperty] private bool _showManualAuth;
    [ObservableProperty] private string _authErrorText = "";

    // ── Default credentials (Settings panel) ──────────────────────────────

    [ObservableProperty] private string _defaultUsername = "root";
    [ObservableProperty] private string _defaultPassword = "";
    [ObservableProperty] private bool _autoConnect = true;
    [ObservableProperty] private bool _showSettings;

    // ── Theme ──────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isLightMode;

    // ☀ when in light mode (click → go dark), 🌙 when in dark mode (click → go light)
    public string ThemeToggleGlyph => IsLightMode ? "☀" : "🌙";

    // ── Info tab ───────────────────────────────────────────────────────────

    [ObservableProperty] private string _infoModel = "—";
    [ObservableProperty] private string _infoSerial = "—";
    [ObservableProperty] private string _infoFirmware = "—";
    [ObservableProperty] private string _infoSoc = "—";
    [ObservableProperty] private string _infoMac = "—";
    [ObservableProperty] private string _infoHostname = "—";
    [ObservableProperty] private string _infoStreams = "—";
    [ObservableProperty] private string _infoStorage = "—";

    // ── Network tab ────────────────────────────────────────────────────────

    [ObservableProperty] private string _curIp = "—";
    [ObservableProperty] private string _curSubnet = "—";
    [ObservableProperty] private string _curGw = "—";
    [ObservableProperty] private string _curMode = "—";

    [ObservableProperty] private string _newIp = "";
    [ObservableProperty] private string _newSubnet = "255.255.255.0";
    [ObservableProperty] private string _newGw = "";
    [ObservableProperty] private string _newHostname = "";

    // ── Streams tab ────────────────────────────────────────────────────────

    [ObservableProperty] private ObservableCollection<StreamInfo> _streams = [];

    // ── Params tab ─────────────────────────────────────────────────────────

    [ObservableProperty] private ObservableCollection<ParamEntry> _allParams = [];
    [ObservableProperty] private ObservableCollection<ParamEntry> _filteredParams = [];
    [ObservableProperty] private string _paramFilter = "";

    partial void OnParamFilterChanged(string value) => ApplyParamFilter();

    private VapixService? _vapix;
    private AppSettings _settings;

    // ── Constructor ────────────────────────────────────────────────────────

    public MainViewModel()
    {
        _settings = SettingsService.Load();
        DefaultUsername = _settings.DefaultUsername;
        DefaultPassword = _settings.DefaultPassword;
        AutoConnect = _settings.AutoConnect;

        Username = DefaultUsername;
        Password = DefaultPassword;

        IsLightMode = string.Equals(_settings.Theme, "Light",
            StringComparison.OrdinalIgnoreCase);
        ApplyTheme();
    }

    // ── Selection changed — attempt auto-connect ───────────────────────────

    partial void OnSelectedCameraChanged(CameraDevice? value)
    {
        if (value is null) return;

        ShowManualAuth = false;
        AuthErrorText = "";
        Username = DefaultUsername;
        Password = DefaultPassword;

        if (AutoConnect)
            _ = TryAutoConnectAsync(value);
    }

    private async Task TryAutoConnectAsync(CameraDevice camera)
    {
        IsConnecting = true;
        IsConnected = false;
        ConnectionStatus = "●";
        ConnectionStatusClass = "warning";
        StatusText = $"Connecting to {camera.Ip} with default credentials…";

        _vapix?.Dispose();
        _vapix = new VapixService(camera.Ip, DefaultUsername, DefaultPassword);

        try
        {
            var p = await _vapix.GetParamsAsync();
            OnConnectSuccess(camera, p);
        }
        catch (Exception ex)
        {
            IsConnecting = false;
            IsConnected = false;
            ConnectionStatus = "●";
            ConnectionStatusClass = "danger";
            ShowManualAuth = true;
            AuthErrorText = IsAuthError(ex)
                ? "Default credentials failed  —  enter credentials below"
                : $"Connection error: {ex.Message}";
            StatusText = $"Auto-connect failed for {camera.Ip}";
        }
    }

    // ── Commands ───────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        StatusText = "Scanning network for Axis devices…";

        try
        {
            var found = await DiscoveryService.ScanAsync(4);
            var existingIps = Cameras.Select(c => c.Ip).ToHashSet();

            var added = 0;
            foreach (var cam in found)
                if (existingIps.Add(cam.Ip))
                {
                    Cameras.Add(cam);
                    added++;
                }

            StatusText = $"Scan complete — {found.Count} device{(found.Count != 1 ? "s" : "")} " +
                         $"found, {added} new";
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            // Always clear the flag so the Scan button re-enables, even on error.
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void AddManual()
    {
        var ip = ManualIp.Trim();
        if (string.IsNullOrEmpty(ip)) return;
        if (Cameras.Any(c => c.Ip == ip)) return;

        Cameras.Add(new CameraDevice { Ip = ip, Status = "Manual" });
        ManualIp = "";
        StatusText = $"Added {ip}";
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (SelectedCamera is null) return;

        IsConnecting = true;
        IsConnected = false;
        ConnectionStatus = "●";
        ConnectionStatusClass = "warning";
        ShowManualAuth = false;
        AuthErrorText = "";
        StatusText = $"Connecting to {SelectedCamera.Ip}…";

        _vapix?.Dispose();
        _vapix = new VapixService(SelectedCamera.Ip, Username, Password);

        var camera = SelectedCamera;

        try
        {
            var p = await _vapix.GetParamsAsync();
            OnConnectSuccess(camera, p);
        }
        catch (Exception ex)
        {
            IsConnecting = false;
            ConnectionStatus = "●";
            ConnectionStatusClass = "danger";
            ShowManualAuth = true;
            AuthErrorText = IsAuthError(ex)
                ? "Incorrect credentials  —  try again"
                : $"Connection error: {ex.Message}";
            StatusText = $"Connection failed: {ex.Message}";
        }
    }

    private void OnConnectSuccess(
        CameraDevice camera,
        System.Collections.Generic.Dictionary<string, string> p)
    {
        PopulateFromParams(p, camera.Ip);

        camera.Connected = true;
        camera.Status = "Connected";
        camera.Name = p.GetValueOrDefault("root.Brand.ProdShortName", "Axis Device");

        var idx = Cameras.IndexOf(camera);
        if (idx >= 0)
        {
            Cameras.RemoveAt(idx);
            Cameras.Insert(idx, camera);
            SelectedCamera = camera;
        }

        IsConnected = true;
        IsConnecting = false;
        ShowManualAuth = false;
        AuthErrorText = "";
        ConnectionStatus = "●";
        ConnectionStatusClass = "success";
        StatusText = $"Connected  —  {InfoModel} @ {camera.Ip}";
    }

    // ── Settings commands ──────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleSettings() => ShowSettings = !ShowSettings;

    // ── Theme ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleTheme() => IsLightMode = !IsLightMode;

    partial void OnIsLightModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ThemeToggleGlyph));
        ApplyTheme();

        _settings.Theme = value ? "Light" : "Dark";
        SettingsService.Save(_settings);
    }

    private void ApplyTheme()
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant =
                IsLightMode ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    [RelayCommand]
    private void SaveSettings()
    {
        _settings.DefaultUsername = DefaultUsername;
        _settings.DefaultPassword = DefaultPassword;
        _settings.AutoConnect = AutoConnect;
        SettingsService.Save(_settings);

        Username = DefaultUsername;
        Password = DefaultPassword;
        ShowSettings = false;
        StatusText = "Default credentials saved";
    }

    [RelayCommand]
    private void CancelSettings()
    {
        DefaultUsername = _settings.DefaultUsername;
        DefaultPassword = _settings.DefaultPassword;
        AutoConnect = _settings.AutoConnect;
        ShowSettings = false;
    }

    // ── Other commands ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ApplyStaticIpAsync()
    {
        if (_vapix is null || !IsConnected) return;
        if (string.IsNullOrWhiteSpace(NewIp) ||
            string.IsNullOrWhiteSpace(NewSubnet) ||
            string.IsNullOrWhiteSpace(NewGw))
        {
            StatusText = "IP, Subnet, and Gateway are required";
            return;
        }
        StatusText = "Applying static IP…";
        try
        {
            await _vapix.SetStaticIpAsync(NewIp, NewSubnet, NewGw,
                string.IsNullOrWhiteSpace(NewHostname) ? null : NewHostname);
            StatusText = $"Static IP set to {NewIp}  —  update device IP to reconnect";
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task ApplyDhcpAsync()
    {
        if (_vapix is null || !IsConnected) return;
        StatusText = "Switching to DHCP…";
        try
        {
            await _vapix.SetDhcpAsync();
            StatusText = "DHCP enabled  —  camera will request a new IP";
            CurMode = "DHCP";
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task RestartCameraAsync()
    {
        if (_vapix is null || !IsConnected) return;
        StatusText = "Restart command sent…";
        await _vapix.RestartAsync();
        StatusText = "Camera restarting  —  wait ~30 seconds";
    }

    [RelayCommand]
    private void OpenSnapshot()
    {
        if (SelectedCamera is null) return;
        OpenUrl($"http://{SelectedCamera.Ip}/axis-cgi/jpg/image.cgi");
    }

    [RelayCommand]
    private void OpenWebInterface()
    {
        if (SelectedCamera is null) return;
        OpenUrl($"http://{SelectedCamera.Ip}");
    }

    [RelayCommand]
    private async Task RefreshParamsAsync()
    {
        if (_vapix is null || !IsConnected) return;
        StatusText = "Loading parameters…";
        try
        {
            var p = await _vapix.GetParamsAsync();
            LoadParams(p);
            StatusText = $"Loaded {p.Count} parameters";
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    [ObservableProperty] private string _clipboardText = "";

    private static bool IsAuthError(Exception ex)
    {
        var msg = ex.Message.ToLowerInvariant();
        return msg.Contains("401") || msg.Contains("unauthorized") ||
               msg.Contains("403") || msg.Contains("forbidden");
    }

    private static string G(System.Collections.Generic.Dictionary<string, string> p,
                             string key, string def = "—")
        => p.GetValueOrDefault(key, def);

    private void PopulateFromParams(
        System.Collections.Generic.Dictionary<string, string> p, string ip)
    {
        InfoModel = G(p, "root.Brand.ProdFullName");
        InfoSerial = G(p, "root.Properties.System.SerialNumber");
        InfoFirmware = G(p, "root.Properties.Firmware.Version");
        InfoSoc = G(p, "root.Properties.System.Soc");
        InfoMac = G(p, "root.Network.eth0.MACAddress");
        InfoHostname = G(p, "root.Network.HostName");
        var nv = G(p, "root.Properties.Image.NbrOfViews", "0");
        InfoStreams = $"{nv} views";
        InfoStorage = G(p, "root.Storage.S0.DiskID", "None");

        CurIp = G(p, "root.Network.IPAddress");
        CurSubnet = G(p, "root.Network.SubnetMask");
        CurGw = G(p, "root.Network.DefaultRouter");
        CurMode = G(p, "root.Network.BootProto", "dhcp") == "dhcp" ? "DHCP" : "STATIC";

        NewIp = G(p, "root.Network.IPAddress", "");
        NewSubnet = G(p, "root.Network.SubnetMask", "255.255.255.0");
        NewGw = G(p, "root.Network.DefaultRouter", "");
        NewHostname = G(p, "root.Network.HostName", "");

        Streams.Clear();
        int nbViews = int.TryParse(
            G(p, "root.Image.NbrOfConfigs", "12"), out var n) ? n : 12;
        for (int i = 0; i < nbViews; i++)
        {
            var name = G(p, $"root.Image.I{i}.Name", $"Stream {i}");
            Streams.Add(new StreamInfo
            {
                Index = i,
                Name = name,
                RtspUrl = $"rtsp://{ip}/axis-media/media.amp?camera={i + 1}",
                HttpUrl = $"http://{ip}/axis-cgi/mjpg/video.cgi?camera={i + 1}",
            });
        }

        LoadParams(p);
    }

    private void LoadParams(System.Collections.Generic.Dictionary<string, string> p)
    {
        AllParams.Clear();
        foreach (var kv in p.OrderBy(x => x.Key))
            AllParams.Add(new ParamEntry { Key = kv.Key, Value = kv.Value });
        ApplyParamFilter();
    }

    private void ApplyParamFilter()
    {
        FilteredParams.Clear();
        var q = ParamFilter.ToLowerInvariant();
        foreach (var e in AllParams)
            if (string.IsNullOrEmpty(q) ||
                e.Key.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.Value.Contains(q, StringComparison.OrdinalIgnoreCase))
                FilteredParams.Add(e);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsLinux())
                System.Diagnostics.Process.Start("xdg-open", url);
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", url);
        }
        catch { }
    }
}