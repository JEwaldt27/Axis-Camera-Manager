# AXIS Camera Manager
### Avalonia .NET 10 — Windows & Linux

A desktop tool for discovering and managing Axis IP cameras on your local network.
Built with Avalonia UI 11.1 and MVVM (CommunityToolkit.Mvvm 8.3), with a switchable light/dark theme.

---

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Linux: standard X11/Wayland desktop (no extra packages needed)
- Windows: Windows 10/11

---

## Build & Run

```bash
# Run from the repo root

# Run directly (development)
dotnet run --project AxisManager

# Build a self-contained release
# Windows:
dotnet publish AxisManager -c Release -r win-x64 --self-contained true -o ./publish/windows

# Linux:
dotnet publish AxisManager -c Release -r linux-x64 --self-contained true -o ./publish/linux
```

The published output in `./publish/` is a single folder you can zip and distribute.

---

## Features

| Tab | What it does |
|-----|-------------|
| **INFO** | Model, serial, firmware, SoC, MAC, hostname, stream count, storage |
| **NETWORK** | View current IP config; set static IP or switch to DHCP; restart camera |
| **STREAMS** | All H.264/RTSP/HTTP stream URLs with one-click copy |
| **ALL PARAMS** | Full VAPIX param dump, filterable/searchable |

### Discovery
- Sends mDNS PTR queries for `_vapix-https._tcp.local`, `_vapix-http._tcp.local`, `_axis-video._tcp.local`
- Camera must be on the same subnet
- Or add cameras manually by IP

### Connection flow
When a camera is selected (or added), the app probes it before connecting:

| Probe result | What the UI shows |
|---|---|
| Connected | Loads tabs immediately (auto-connect with saved credentials) |
| NeedsSetup | Setup panel — prompts for a new password on factory-default cameras |
| AuthFailed | Manual auth panel — enter credentials to retry |
| Unreachable | Status bar error |

### Authentication
- Probes the camera without credentials first to detect factory-default state (`Axis-Setup` header)
- Uses HTTP Digest auth (Axis default) for all authenticated requests
- Self-signed certificate errors are suppressed (camera default)
- Default credentials (username/password) can be saved in the **Settings** panel; with auto-connect enabled, selecting a camera connects automatically using them
- Saved credentials are stored unencrypted in `%AppData%/AxisManager/settings.json`

---

## Project Structure

```
AxisManager/
├── AxisManager.csproj
├── Program.cs
├── App.axaml / App.axaml.cs
├── Assets/
│   └── AppStyles.axaml        ← Light/dark theme + control styles
├── Models/
│   └── CameraDevice.cs        ← Data models (CameraDevice, StreamInfo, ParamEntry)
├── Services/
│   ├── DiscoveryService.cs    ← mDNS scanner (raw UDP/DNS, no library)
│   ├── VapixService.cs        ← VAPIX HTTP client + Digest auth + probe logic
│   └── SettingsService.cs     ← Default credentials (saved to %AppData%)
├── ViewModels/
│   └── MainViewModel.cs       ← All UI logic (single ObservableObject)
└── Views/
    ├── MainWindow.axaml        ← UI layout
    └── MainWindow.axaml.cs    ← Code-behind
```

---

## Notes

- Setting a static IP takes effect immediately but the camera stays reachable at the old IP until restarted
- After setting DHCP, re-scan to find the camera at its new address
- The RTSP streams require a compatible player (VLC, ffplay, etc.)
- RTSP URL format: `rtsp://192.168.x.x/axis-media/media.amp?camera=N`
- Use the ☀ / 🌙 button in the top bar to switch between light and dark mode; the choice is saved to `settings.json` and restored on next launch
