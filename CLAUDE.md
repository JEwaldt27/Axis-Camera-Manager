# CLAUDE.md

Guidance for working in this repository.

## Resume this Claude Code session

Much of the current state (docs, the light/dark theme feature) was built in a Claude Code session. To resume it with full context:

```bash
claude --resume 00b2cc72-486d-4f52-9a07-4b0c7a4a6a44
```

> Note: Claude Code (CLI) resumes by session ID, not a web URL — run the command above from this repo on the same machine where the session was created (transcript: `~/.claude/projects/C--Users-james-Desktop-AxisManager/`).

## What this is

**AxisManager** is a cross-platform desktop app for discovering and managing Axis IP cameras on a local network. It scans for cameras over mDNS, connects to them via Axis's VAPIX HTTP API, and exposes their info, network config, stream URLs, and full parameter set through a tabbed GUI.

- **Stack:** .NET 8, Avalonia UI 11.1, MVVM via CommunityToolkit.Mvvm 8.3
- **Targets:** Windows 10/11 and Linux (X11/Wayland)
- **Output type:** `WinExe` (desktop app, no console window)

## Build & run

The solution (`AxisManager.sln`) contains one project, `AxisManager/AxisManager.csproj`. Run all commands from the repo root unless noted.

```bash
# Run in development
dotnet run --project AxisManager

# Build
dotnet build

# Self-contained release
dotnet publish AxisManager -c Release -r win-x64   --self-contained true -o ./publish/windows
dotnet publish AxisManager -c Release -r linux-x64 --self-contained true -o ./publish/linux
```

There is no test project and no CI configured.

## Architecture

Entry point is `Program.cs` → `App.axaml(.cs)` → `MainWindow`. The app is a single window; there is no navigation framework — tabs and panels are toggled via observable boolean state on the view model.

```
AxisManager/
├── Program.cs                  Avalonia bootstrap (STAThread Main)
├── App.axaml(.cs)              Application root, theme wiring
├── Assets/AppStyles.axaml      Light/dark themes (ThemeDictionaries) + control styles
├── Models/CameraDevice.cs      POCOs: CameraDevice, StreamInfo, ParamEntry
├── Services/
│   ├── DiscoveryService.cs     mDNS scanner (raw UDP/DNS, no library)
│   ├── VapixService.cs         VAPIX HTTP client + hand-rolled Digest auth
│   └── SettingsService.cs      Load/save default creds to %AppData%
├── ViewModels/MainViewModel.cs Single view model — all UI logic
└── Views/MainWindow.axaml(.cs) UI layout + code-behind
```

### Key flows

- **Discovery** (`DiscoveryService.ScanAsync`): opens a UDP socket on `224.0.0.251:5353`, sends raw DNS PTR queries for `_vapix-https._tcp.local`, `_vapix-http._tcp.local`, `_axis-video._tcp.local`, collects responses for ~4s, and regex-scrapes device names/MACs. The DNS packets are built and parsed by hand — there is no mDNS dependency.
- **Connection** (`VapixService`): talks to `axis-cgi/param.cgi` and `restart.cgi`. HTTP Digest auth (MD5 HA1/HA2/response) is implemented manually in `BuildDigestHeader`; falls back to Basic-over-HTTPS. TLS cert validation is disabled to accept cameras' self-signed certs.
- **View model** (`MainViewModel`): one `ObservableObject` drives four tabs — INFO, NETWORK, STREAMS, ALL PARAMS — plus a settings panel and auth-failure UI. Camera params are pulled once on connect and mapped to display fields (`PopulateFromParams`); stream URLs are synthesized from the IP, not read from the camera.

## Conventions

- **MVVM source generators:** UI-bound fields use `[ObservableProperty]` on private backing fields (`_fooBar` → `FooBar`); commands use `[RelayCommand]` on async/void methods (`DoThingAsync` → `DoThingCommand`). Follow this pattern rather than writing properties/`ICommand` by hand.
- `partial void On<Property>Changed(...)` hooks react to property changes (e.g. `OnSelectedCameraChanged` triggers auto-connect, `OnParamFilterChanged` re-filters).
- VAPIX params are flat `string`→`string` dictionaries keyed like `root.Brand.ProdFullName`; the `G(p, key, default)` helper reads them with a fallback.
- **Theming:** colors live in `AppStyles.axaml` under `ThemeDictionaries` (`Dark` / `Light`). All color references **must** use `{DynamicResource ...}` — `StaticResource` resolves once and won't switch at runtime. The toggle flips `Application.Current.RequestedThemeVariant` from `MainViewModel` (`IsLightMode` / `ToggleThemeCommand`) and persists to `AppSettings.Theme`. Text drawn on accent/danger/warning fills uses `OnAccentBrush` and selections use `SelectionBrush` so both themes stay legible — reuse those rather than hardcoding hex.
- Implicit usings and nullable reference types are enabled. `AllowUnsafeBlocks` is on.

## Gotchas / known rough edges

These are existing characteristics of the code — be aware before "fixing" them, and surface them rather than silently changing behavior:

- **Plaintext credentials:** `SettingsService` writes `DefaultPassword` unencrypted to `%AppData%/AxisManager/settings.json`.
- **TLS validation disabled:** `ServerCertificateCustomValidationCallback` always returns `true`. Intentional for self-signed camera certs, but applies to all HTTPS traffic.
- **Silent `catch {}` blocks** are used throughout (settings I/O, URL launching, name parsing). Failures are swallowed; expect to add logging when diagnosing.
- **Digest parsing is naive:** `ExtractQuoted` does simple substring scanning of `WWW-Authenticate`; it doesn't handle multiple challenges, and the `qop.Contains("auth")` check also matches `auth-int`.
- **Stream count is guessed:** defaults to 12 streams if `root.Image.NbrOfConfigs` is absent, which can produce phantom stream rows.
- **No tests:** changes are verified by building and running against a real/emulated camera.
