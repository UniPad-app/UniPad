<div align="center">

# UniPad

**Universal Controller Mapper for Windows**

Make *any* controller work with *any* game.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6.svg)](#)
[![Download](https://img.shields.io/badge/download-latest%20release-success.svg)](../../releases/latest)

</div>

---

UniPad presents **any** input device — modern gamepads, analogue-less retro pads, arcade sticks,
racing wheels, PS1/PS2/N64/SNES USB adapters, no-name clones — to Windows as a standard
**Xbox 360 (XInput)** or **DualShock 4** controller.

If a game only speaks XInput, UniPad makes your hardware speak XInput.

## Quick start (for regular users)

> **You do not need Visual Studio, the .NET SDK, or any programming tools.**

1. Go to **[Releases](../../releases/latest)** and download **`UniPad.exe`**.
2. Double-click it. That is the whole installation — it is a single portable file.
3. On first run a banner may say *ViGEmBus is not installed*. Click **Install** and approve the
   Windows prompt. UniPad downloads and installs it for you.
4. Open a **Player** tab → tick **Connect Controller** → pick your device → click **Auto Map**.
5. Click **Apply**. Verify at [gamepad-tester.com](https://gamepad-tester.com) or by running `joy.cpl`.

### Do I need to install anything else?

| Component | Needed? | Notes |
|---|---|---|
| .NET runtime | ❌ **No** | The release build is self-contained — the runtime is inside the exe. |
| Visual Studio / SDK | ❌ **No** | Only needed if you want to build from source. |
| **ViGEmBus driver** | ✅ **Yes, once** | Installed automatically by UniPad on first run (one UAC prompt). |
| HidHide | ⚪ Optional | Only to stop "double input". UniPad can install it for you too. |

> **Why can't ViGEmBus be inside the exe?**
> Creating a virtual controller requires a **kernel-mode driver**, and Windows does not allow a
> portable application to fabricate one. Every tool of this kind (DS4Windows, x360ce, reWASD) has
> the same requirement. UniPad at least makes it a single click instead of a scavenger hunt.

## Features

**Input**
- Detects *everything* SDL3 can see: gamepads, joysticks, wheels, arcade sticks, generic HID pads
- Hot-plug: connect or disconnect mid-session, mappings survive and re-attach automatically
- Stable device identity, so a controller returning on a different USB port keeps its bindings
- Handles drifting/noisy analogue axes from cheap adapters via resting-value calibration
- Raw device monitor showing live axis/button/hat values — invaluable for unknown hardware

**Mapping**
- **Auto-map** with two paths: the SDL gamepad database, and a heuristic fallback for unknown pads
- **Full manual mapping** — click a bind, press the input, done. Never touch a config file.
- All four conversion scenarios:
  - button → button
  - **button → axis** at full deflection *(this is what makes analogue-less retro pads work)*
  - axis → button (threshold)
  - axis → axis
- **Radial** dead zone (not per-axis), adjustable range, invert, threshold, modifier and toggle
- **Emulate Left Stick with D-Pad** for controllers that have no analogue sticks at all

**Output**
- Virtual **Xbox 360** and **DualShock 4** controllers via ViGEmBus
- **Up to 8 players** for local multiplayer
- Rumble return path from game → virtual pad → your physical controller
- **Identify** button to find out which physical pad is which
- Optional **HidHide** integration to hide physical devices and stop double input

**Application**
- Single portable `.exe` — no installer, no registry mess, nothing left behind
- Portable data folder next to the executable (falls back to `%APPDATA%` on read-only media)
- Profiles with save/load and schema migration
- System tray, minimize-to-tray, run-at-startup
- Dark and light themes
- **English and Persian (فارسی)** with full right-to-left layout
- 1000 Hz polling with a zero-allocation hot path

## Screenshots

<!-- Add screenshots here after your first run:
     1. Take a screenshot of the Player 1 tab and the Advanced tab
     2. Save them as docs/screenshot-player.png and docs/screenshot-advanced.png
     3. Uncomment the lines below
![Player configuration](docs/screenshot-player.png)
![Advanced settings](docs/screenshot-advanced.png)
-->

*Screenshots coming soon.*

## Usage guide

### Mapping an unknown controller

1. **Player** tab → tick **Connect Controller**.
2. Choose your device in **Input Device**. If it is not listed, click **Refresh**.
3. Click **Auto Map** and test. For a known pad this is usually all you need.
4. For anything wrong: click the bind button, then press the physical input within 5 seconds.
5. **Right-click** any bind for extra options:
   - **Clear** — remove the binding
   - **Invert axis** — for reversed sticks and pedals
   - **Toggle** — latch on/off instead of hold
   - **Set threshold** — how far an axis must travel to count as a press

### Controllers with no analogue sticks

Retro pads (NES/SNES/Mega Drive adapters) have only a D-Pad, so games expecting a stick ignore them.

Tick **Emulate Left Stick with D-Pad**. The D-Pad then drives the left stick at full deflection,
with diagonals normalised to 0.7071 so diagonal movement isn't faster than straight movement.

### Local multiplayer

Add players on tabs 1–8. Set each one's **Input Device** and **Output Mode**.

> ⚠️ **Windows provides only 4 XInput slots.** Players 5–8 must use DualShock 4 output, which
> XInput-only games will not see. UniPad states this on those tabs. This is a Windows limitation,
> not a UniPad one.

### Stopping "double input"

Windows sees both your physical controller *and* the virtual one, so some games register every
input twice. Fix: install **HidHide** (Advanced tab → Install) and tick
**Hide physical controllers**.

## Building from source

Requires the **[.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)** on Windows.

```cmd
git clone https://github.com/YOUR-USERNAME/UniPad.git
cd UniPad
dotnet build UniPad.sln -c Release
```

To produce the portable single-file executable:

```cmd
build\publish.cmd
```

Output: `build\out\UniPad.exe` (~48 MB, self-contained, compressed).

Cross-compiling the Windows binary from Linux or macOS also works:

```bash
dotnet publish src/UniPad.App/UniPad.App.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o build/out
```

## Architecture

```
UniPad/
├── UniPad.sln
├── Directory.Build.props          net9.0, C# 13, nullable, unsafe blocks
├── build/publish.cmd              single-file release script
└── src/
    ├── UniPad.Core/               all logic, zero UI dependencies
    │   ├── Input/                 SdlInputBackend, DeviceId, InputSnapshot, InputDevice
    │   ├── Mapping/               MappingEngine, AutoMapper, InputBinding, PadState
    │   ├── Output/                ViGEmX360Pad, ViGEmDs4Pad, OutputManager, FeedbackRouter
    │   ├── Profiles/              ProfileStore + JSON source-generated context
    │   └── System/                PortablePaths, DriverBootstrapper, HidHideService, LogSetup
    └── UniPad.App/                Avalonia 11 UI
        ├── Views/                 MainWindow, PlayerConfigView, AdvancedView, AboutView
        ├── Views/Controls/        ControllerPreview, AnalogStickPreview, TriggerBar, BindButton
        ├── ViewModels/            MainWindow, PlayerConfig, Advanced, About, BindButton
        ├── Services/              AppState, BindCaptureService, TrayIconHost, StartupRegistration
        ├── Styles/                Palette.axaml, Controls.axaml
        └── Localization/          Strings.cs — 99 keys × English/Persian, RTL aware
```

### Design decisions

| Area | Decision |
|---|---|
| Input | SDL3 (`ppy.SDL3-CS`) — joystick + gamepad + events, background-events hint enabled |
| Poll loop | Dedicated thread at 1000 Hz, hybrid `Thread.Sleep(1)` + `SpinWait` precise wait |
| Hot path | Zero allocation: pre-allocated arrays, mutable `PadState` passed `in`/`ref`, no LINQ |
| Dead zone | **Radial**: `mag = √(x²+y²)`, then `min((mag−dz)/(1−dz), 1) × range` |
| Axis drift | Resting values captured at open; bind capture needs >50 % travel from a fresh baseline |
| Feedback loop | `ExpectVirtualDevice()` blacklists joysticks appearing within 500 ms of a ViGEm connect |
| XInput order | Pads connected with a 300 ms stagger so Windows assigns slots in player order |
| UI refresh | 60 Hz dispatcher timer, decoupled from input; diagnostics throttled to ~6 Hz |
| Persistence | `System.Text.Json` **source generator** (trim-safe), portable data folder |

## Troubleshooting

| Problem | Cause / fix |
|---|---|
| *"ViGEmBus is not installed"* won't go away | Restart after installing the driver. If it persists, install [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases) manually. |
| Game registers every input twice | Install HidHide and tick **Hide physical controllers**. |
| Controller not listed | Click **Refresh**. If still missing, check it appears in `joy.cpl`. |
| Game ignores players 5–8 | Windows has only 4 XInput slots; those players emit DS4. Not fixable. |
| Stick drifts on its own | Raise the dead zone on that stick. Cheap adapters often need 0.15–0.25. |
| Buttons map to the wrong thing | Use the Advanced tab's raw monitor to see real indices, then bind manually. |
| Nothing happens in-game | Check **Output enabled** on the Advanced tab, and that the profile was **Applied**. |
| Game with anti-cheat rejects it | Kernel-mode anti-cheat may block virtual pads. UniPad does not attempt to bypass it. |

Logs live in `UniPad_Data\logs\` next to the executable (or `%APPDATA%\UniPad\logs\`).
Attach the newest log when reporting a bug.

## Contributing

Contributions are welcome. Useful things to know:

- The build must stay at **0 errors and 0 warnings**.
- `UniPad.Core` must not take a UI dependency — it is deliberately testable in isolation.
- Nothing may allocate on the polling hot path.
- Please include your controller's name, VID/PID and the raw-monitor output when reporting a
  mapping bug; a device nobody owns cannot be fixed by guesswork.

## Limitations

- Windows only (ViGEmBus is a Windows kernel driver).
- Only 4 XInput slots exist; players 5–8 use DualShock 4 output.
- Games with kernel-mode anti-cheat may reject virtual controllers.
- Trimming (`PublishTrimmed`) is intentionally **off** — SDL3 and ViGEm reach native code through
  P/Invoke and the trimmer cannot see those paths.

## Not an emulator, not a cheat tool

UniPad contains no game code and makes no attempt to bypass any protection. The interface takes
visual inspiration from familiar controller configuration dialogs, but every asset, style and line
of code — including the tray icon and the controller diagram, which are drawn procedurally — is
original work.

## Licence

[MIT](LICENSE) — free to use, modify and redistribute, including commercially.

### Third-party components

| Component | Licence |
|---|---|
| [SDL3](https://libsdl.org) | zlib |
| [ViGEmBus / ViGEm.NET](https://github.com/nefarius/ViGEmBus) — Nefarius Software Solutions | MIT / BSD-3 |
| [HidHide](https://github.com/nefarius/HidHide) — Nefarius Software Solutions | MIT |
| [Avalonia UI](https://avaloniaui.net) | MIT |
| [Serilog](https://serilog.net) | Apache-2.0 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT |
| [SDL_GameControllerDB](https://github.com/mdqinc/SDL_GameControllerDB) | Zlib |
