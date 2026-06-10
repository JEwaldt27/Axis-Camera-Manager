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
    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private bool   _isConnecting;
    [ObservableProperty] private string _connectionStatus      = "●";
    [ObservableProperty] private string _connectionStatusClass = "dim";

    // ── Auth / Setup UI state ──────────────────────────────────────────────

    [ObservableProperty] private bool   _showManualAuth;
    [ObservableProperty] private string _authErrorText = "";

    // Shown when camera is detected as factory-default (needs initial setup)
    [ObservableProperty] private bool   _showSetupPanel;
    [ObservableProperty] private string _newCamPassword  = "";
    [ObservableProperty] private string _newCamPassword2 = "";
    [ObservableProperty] private string _setupErrorText  = "";
    [ObservableProperty] private bool   _isSettingUp;

    // ── Initialize All panel ───────────────────────────────────────────────

    [ObservableProperty] private bool   _showInitAllPanel;
    [ObservableProperty] private string _initAllPassword  = "";
    [ObservableProperty] private string _initAllPassword2 = "";
    [ObservableProperty] private string _initAllErrorText = "";
    [ObservableProperty] private string _initAllProgress  = "";
    [ObservableProperty] private bool   _isInitializingAll;

    // ── Default credentials (Settings panel) ──────────────────────────────

    [ObservableProperty] private string _defaultUsername = "root";
    [ObservableProperty] private string _defaultPassword = "";
    [ObservableProperty] private bool   _autoConnect     = true;
    [ObservableProperty] private bool   _showSettings;

    // ── Theme ──────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isLightMode;
    public string ThemeToggleGlyph => IsLightMode ? "☀" : "🌙";

    // ── Info tab ───────────────────────────────────────────────────────────

    [ObservableProperty] private string _infoModel    = "—";
    [ObservableProperty] private string _infoSerial   = "—";
    [ObservableProperty] private string _infoFirmware = "—";
    [ObservableProperty] private string _infoSoc      = "—";
    [ObservableProperty] private string _infoMac      = "—";
    [ObservableProperty] private string _infoHostname = "—";
    [ObservableProperty] private string _infoStreams   = "—";
    [ObservableProperty] private string _infoStorage  = "—";

    // ── Network tab ────────────────────────────────────────────────────────

    [ObservableProperty] private string _curIp     = "—";
    [ObservableProperty] private string _curSubnet = "—";
    [ObservableProperty] private string _curGw     = "—";
    [ObservableProperty] private string _curMode   = "—";

    [ObservableProperty] private string _newIp       = "";
    [ObservableProperty] private string _newSubnet   = "255.255.255.0";
    [ObservableProperty] private string _newGw       = "";
    [ObservableProperty] private string _newHostname = "";

    // ── Streams / Params tabs ──────────────────────────────────────────────

    [ObservableProperty] private ObservableCollection<StreamInfo>  _streams       = [];
    [ObservableProperty] private ObservableCollection<ParamEntry>  _allParams     = [];
    [ObservableProperty] private ObservableCollection<ParamEntry>  _filteredParams= [];
    [ObservableProperty] private string _paramFilter = "";

    partial void OnParamFilterChanged(string value) => ApplyParamFilter();

    private VapixService?            _vapix;
    private AppSettings              _settings;
    private bool                     _suppressSelectionChanged;
    private CancellationTokenSource  _probeCts = new();

    // ── Constructor ────────────────────────────────────────────────────────

    public MainViewModel()
    {
        _settings = SettingsService.Load();
        DefaultUsername = _settings.DefaultUsername;
        DefaultPassword = _settings.DefaultPassword;
        AutoConnect     = _settings.AutoConnect;

        Username = DefaultUsername;
        Password = DefaultPassword;

        IsLightMode = string.Equals(_settings.Theme, "Light",
            StringComparison.OrdinalIgnoreCase);
        ApplyTheme();
    }

    // ── Camera selected ────────────────────────────────────────────────────

    partial void OnSelectedCameraChanged(CameraDevice? value)
    {
        if (value is null || _suppressSelectionChanged) return;

        _probeCts.Cancel();
        _probeCts = new CancellationTokenSource();

        // Reset all panels
        ShowManualAuth = false;
        ShowSetupPanel = false;
        AuthErrorText  = "";
        SetupErrorText = "";
        NewCamPassword  = "";
        NewCamPassword2 = "";

        Username = DefaultUsername;
        Password = DefaultPassword;

        ClearDisplayFields();

        if (AutoConnect)
            _ = ProbeAndConnectAsync(value, _probeCts.Token);
    }

    // ── Probe → decide what to show ────────────────────────────────────────

    private async Task ProbeAndConnectAsync(CameraDevice camera, CancellationToken ct)
    {
        IsConnecting          = true;
        IsConnected           = false;
        ConnectionStatus      = "●";
        ConnectionStatusClass = "warning";
        StatusText            = $"Probing {camera.Ip}…";

        _vapix?.Dispose();
        _vapix = new VapixService(camera.Ip, DefaultUsername, DefaultPassword);

        var result = await _vapix.ProbeAsync();

        if (ct.IsCancellationRequested) return;

        switch (result)
        {
            case ProbeResult.Connected:
                // Either no-password camera or default creds worked
                try
                {
                    var p = await _vapix.GetParamsAsync();
                    if (ct.IsCancellationRequested) return;
                    OnConnectSuccess(camera, p);
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                        SetConnectFailed(camera, ex);
                }
                break;

            case ProbeResult.NeedsSetup:
                // Brand new camera — show setup wizard panel
                IsConnecting          = false;
                IsConnected           = false;
                ConnectionStatus      = "●";
                ConnectionStatusClass = "warning";
                ShowSetupPanel        = true;
                ShowManualAuth        = false;
                camera.SetupState     = CameraSetupState.NeedsSetup;
                camera.Status         = "Needs Setup";
                RefreshCameraInList(camera);
                StatusText = $"New camera detected at {camera.Ip}  —  set a password to continue";
                break;

            case ProbeResult.AuthFailed:
                // Camera has a password, default creds didn't work
                IsConnecting          = false;
                IsConnected           = false;
                ConnectionStatus      = "●";
                ConnectionStatusClass = "danger";
                ShowManualAuth        = true;
                ShowSetupPanel        = false;
                camera.SetupState     = CameraSetupState.NeedsAuth;
                AuthErrorText         = "Default credentials failed  —  enter credentials below";
                StatusText            = $"Auth required for {camera.Ip}";
                break;

            case ProbeResult.Unreachable:
                IsConnecting          = false;
                ConnectionStatus      = "●";
                ConnectionStatusClass = "danger";
                StatusText            = $"Cannot reach {camera.Ip}  —  check connection";
                break;
        }
    }

    // ── First-time setup — set initial password ────────────────────────────

    [RelayCommand]
    private async Task CompleteSetupAsync()
    {
        if (SelectedCamera is null || _vapix is null) return;

        SetupErrorText = "";

        if (string.IsNullOrWhiteSpace(NewCamPassword))
        {
            SetupErrorText = "Password cannot be empty";
            return;
        }
        if (NewCamPassword != NewCamPassword2)
        {
            SetupErrorText = "Passwords do not match";
            return;
        }
        if (NewCamPassword.Length < 6)
        {
            SetupErrorText = "Password must be at least 6 characters";
            return;
        }

        IsSettingUp = true;
        StatusText  = "Setting initial password…";

        var camera = SelectedCamera;

        try
        {
            await _vapix.SetInitialPasswordAsync(NewCamPassword);

            // Now connect with the new password
            _vapix.Dispose();
            _vapix = new VapixService(camera.Ip, "root", NewCamPassword);

            // Brief wait for camera to apply credentials
            await Task.Delay(1500);

            var p = await _vapix.GetParamsAsync();

            ShowSetupPanel      = false;
            camera.SetupState   = CameraSetupState.Ready;
            NewCamPassword      = "";
            NewCamPassword2     = "";

            OnConnectSuccess(camera, p);
        }
        catch (Exception ex)
        {
            SetupErrorText = $"Setup failed: {ex.Message}";
            StatusText     = "Setup failed — try opening the web interface manually";
        }
        finally
        {
            IsSettingUp = false;
        }
    }

    [RelayCommand]
    private void UseDefaultPassword()
    {
        NewCamPassword  = DefaultPassword;
        NewCamPassword2 = DefaultPassword;
    }

    [RelayCommand]
    private void OpenSetupInBrowser()
    {
        if (SelectedCamera is null) return;
        OpenUrl($"http://{SelectedCamera.Ip}");
    }

    // ── Initialize All ─────────────────────────────────────────────────────

    [RelayCommand]
    private void ShowInitAll()
    {
        InitAllPassword  = "";
        InitAllPassword2 = "";
        InitAllErrorText = "";
        InitAllProgress  = "";
        ShowInitAllPanel = true;
    }

    [RelayCommand]
    private void CancelInitAll() => ShowInitAllPanel = false;

    [RelayCommand]
    private void UseDefaultPasswordForInitAll()
    {
        InitAllPassword  = DefaultPassword;
        InitAllPassword2 = DefaultPassword;
    }

    [RelayCommand]
    private async Task InitializeAllAsync()
    {
        InitAllErrorText = "";

        if (string.IsNullOrWhiteSpace(InitAllPassword))
        {
            InitAllErrorText = "Password cannot be empty";
            return;
        }
        if (InitAllPassword != InitAllPassword2)
        {
            InitAllErrorText = "Passwords do not match";
            return;
        }
        if (InitAllPassword.Length < 6)
        {
            InitAllErrorText = "Password must be at least 6 characters";
            return;
        }

        var targets = Cameras
            .Where(c => c.SetupState == CameraSetupState.NeedsSetup ||
                        c.SetupState == CameraSetupState.Unknown)
            .ToList();

        if (targets.Count == 0)
        {
            InitAllErrorText = "No uninitialized cameras in the list";
            return;
        }

        IsInitializingAll = true;
        int done = 0, failed = 0;

        for (int i = 0; i < targets.Count; i++)
        {
            var camera = targets[i];
            InitAllProgress = $"Setting password on {camera.Ip}  ({i + 1}/{targets.Count})…";

            try
            {
                using var svc = new VapixService(camera.Ip, "root", "");
                await svc.SetInitialPasswordAsync(InitAllPassword);

                camera.SetupState = CameraSetupState.Ready;
                camera.Status     = "Password Set";
                RefreshCameraInList(camera);
                done++;
            }
            catch
            {
                failed++;
                if (camera.SetupState == CameraSetupState.NeedsSetup)
                {
                    camera.Status = "Init Failed";
                    RefreshCameraInList(camera);
                }
            }
        }

        IsInitializingAll = false;
        ShowInitAllPanel  = false;

        StatusText = done > 0
            ? $"Initialized {done} camera{(done != 1 ? "s" : "")}" +
              (failed > 0 ? $"  —  {failed} skipped or failed" : "")
            : $"No cameras initialized  —  {failed} skipped or unreachable";
    }

    // ── Manual connect (when default creds fail) ───────────────────────────

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (SelectedCamera is null) return;

        IsConnecting          = true;
        IsConnected           = false;
        ConnectionStatus      = "●";
        ConnectionStatusClass = "warning";
        ShowManualAuth        = false;
        AuthErrorText         = "";
        StatusText            = $"Connecting to {SelectedCamera.Ip}…";

        _vapix?.Dispose();
        _vapix = new VapixService(SelectedCamera.Ip, Username, Password);

        var camera = SelectedCamera;
        var ct     = _probeCts.Token;

        try
        {
            var p = await _vapix.GetParamsAsync();
            if (!ct.IsCancellationRequested)
                OnConnectSuccess(camera, p);
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                SetConnectFailed(camera, ex);
        }
    }

    private void SetConnectFailed(CameraDevice camera, Exception ex)
    {
        IsConnecting          = false;
        ConnectionStatus      = "●";
        ConnectionStatusClass = "danger";
        ShowManualAuth        = true;
        AuthErrorText         = IsAuthError(ex)
            ? "Incorrect credentials  —  try again"
            : $"Connection error: {ex.Message}";
        StatusText = $"Connection failed: {ex.Message}";
    }

    private void OnConnectSuccess(
        CameraDevice camera,
        System.Collections.Generic.Dictionary<string, string> p)
    {
        PopulateFromParams(p, camera.Ip);

        camera.Connected  = true;
        camera.Status     = "Connected";
        camera.SetupState = CameraSetupState.Ready;
        camera.Name       = p.GetValueOrDefault("root.Brand.ProdShortName", "Axis Device");

        RefreshCameraInList(camera);

        IsConnected           = true;
        IsConnecting          = false;
        ShowManualAuth        = false;
        ShowSetupPanel        = false;
        AuthErrorText         = "";
        ConnectionStatus      = "●";
        ConnectionStatusClass = "success";
        StatusText            = $"Connected  —  {InfoModel} @ {camera.Ip}";
    }

    private void RefreshCameraInList(CameraDevice camera)
    {
        var idx = Cameras.IndexOf(camera);
        if (idx < 0) return;

        // Only restore selection to this camera if it is still the selected one.
        // If the user has already clicked a different camera, don't override them.
        var restoreSelection = SelectedCamera == camera;

        _suppressSelectionChanged = true;
        try
        {
            Cameras.RemoveAt(idx);
            Cameras.Insert(idx, camera);
            if (restoreSelection)
                SelectedCamera = camera;
        }
        finally
        {
            _suppressSelectionChanged = false;
        }
    }

    // ── Scan ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        StatusText = "Scanning network for Axis devices…";

        try
        {
            Cameras.Clear();

            var found = await DiscoveryService.ScanAsync(4);
            foreach (var cam in found)
                Cameras.Add(cam);

            StatusText = $"Scan complete — {found.Count} device{(found.Count != 1 ? "s" : "")} found";
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
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
        ManualIp   = "";
        StatusText = $"Added {ip}";
    }

    // ── Settings ───────────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleSettings() => ShowSettings = !ShowSettings;

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
        _settings.AutoConnect     = AutoConnect;
        SettingsService.Save(_settings);

        Username     = DefaultUsername;
        Password     = DefaultPassword;
        ShowSettings = false;
        StatusText   = "Default credentials saved";
    }

    [RelayCommand]
    private void CancelSettings()
    {
        DefaultUsername = _settings.DefaultUsername;
        DefaultPassword = _settings.DefaultPassword;
        AutoConnect     = _settings.AutoConnect;
        ShowSettings    = false;
    }

    // ── Network / device commands ──────────────────────────────────────────

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
            CurMode    = "DHCP";
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
        InfoModel    = G(p, "root.Brand.ProdFullName");
        InfoSerial   = G(p, "root.Properties.System.SerialNumber");
        InfoFirmware = G(p, "root.Properties.Firmware.Version");
        InfoSoc      = G(p, "root.Properties.System.Soc");
        InfoMac      = G(p, "root.Network.eth0.MACAddress");
        InfoHostname = G(p, "root.Network.HostName");
        InfoStreams   = $"{G(p, "root.Properties.Image.NbrOfViews", "0")} views";
        InfoStorage  = G(p, "root.Storage.S0.DiskID", "None");

        CurIp     = G(p, "root.Network.IPAddress");
        CurSubnet = G(p, "root.Network.SubnetMask");
        CurGw     = G(p, "root.Network.DefaultRouter");
        CurMode   = G(p, "root.Network.BootProto", "dhcp") == "dhcp" ? "DHCP" : "STATIC";

        NewIp       = G(p, "root.Network.IPAddress",     "");
        NewSubnet   = G(p, "root.Network.SubnetMask",    "255.255.255.0");
        NewGw       = G(p, "root.Network.DefaultRouter", "");
        NewHostname = G(p, "root.Network.HostName",      "");

        Streams.Clear();
        int nbViews = int.TryParse(
            G(p, "root.Image.NbrOfConfigs", "12"), out var n) ? n : 12;
        for (int i = 0; i < nbViews; i++)
        {
            var name = G(p, $"root.Image.I{i}.Name", $"Stream {i}");
            Streams.Add(new StreamInfo
            {
                Index   = i,
                Name    = name,
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
                e.Key.Contains(q,   StringComparison.OrdinalIgnoreCase) ||
                e.Value.Contains(q, StringComparison.OrdinalIgnoreCase))
                FilteredParams.Add(e);
    }

    private void ClearDisplayFields()
    {
        InfoModel    = "—";
        InfoSerial   = "—";
        InfoFirmware = "—";
        InfoSoc      = "—";
        InfoMac      = "—";
        InfoHostname = "—";
        InfoStreams   = "—";
        InfoStorage  = "—";
        CurIp     = "—";
        CurSubnet = "—";
        CurGw     = "—";
        CurMode   = "—";
        NewIp       = "";
        NewSubnet   = "255.255.255.0";
        NewGw       = "";
        NewHostname = "";
        Streams.Clear();
        AllParams.Clear();
        FilteredParams.Clear();
        IsConnected           = false;
        IsConnecting          = false;
        ConnectionStatus      = "●";
        ConnectionStatusClass = "dim";
    }

    private static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(url)
                    { UseShellExecute = true });
            else if (OperatingSystem.IsLinux())
                System.Diagnostics.Process.Start("xdg-open", url);
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", url);
        }
        catch { }
    }
}
