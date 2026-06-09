# AXIS Camera Manager
### Avalonia .NET 8 — Windows & Linux

A desktop tool for discovering and managing Axis IP cameras on your local network.
Built with Avalonia UI and MVVM (CommunityToolkit.Mvvm), with a switchable light/dark theme.

---

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Linux: standard X11/Wayland desktop (no extra packages needed)
- Windows: Windows 10/11

---

## Build & Run

```bash
# Clone / extract the project, then:
cd AxisManager

# Run directly (development)
dotnet run

# Build a self-contained release
# Windows:
dotnet publish -c Release -r win-x64 --self-contained true -o ./publish/windows

# Linux:
dotnet publish -c Release -r linux-x64 --self-contained true -o ./publish/linux
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

### Authentication
- Uses HTTP Digest auth (Axis default)
- Falls back to Basic auth over HTTPS if needed
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
│   └── CameraDevice.cs        ← Data models
├── Services/
│   ├── DiscoveryService.cs    ← mDNS scanner
│   ├── VapixService.cs        ← VAPIX HTTP API client
│   └── SettingsService.cs     ← Default credentials (saved to %AppData%)
├── ViewModels/
│   └── MainViewModel.cs       ← MVVM logic
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
