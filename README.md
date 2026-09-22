# MultiDesktopCaptureMod

A [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader) mod for [Resonite](https://resonite.com/) that fixes a permanently blank **Desktop** dash screen, lets **Share Screen** share any of your monitors rather than only the one Resonite is on, and stops the desktop view staying blank after a monitor is disconnected.

## The problem

If you have ever run Resonite on Linux, under Wine, or on any other non-Windows platform, your Desktop dash tab may be completely empty on Windows — no desktop image, and no `≡` controls button.

Resonite forces `Engine.Config.DisableDesktop = true` on those platforms. When the Desktop screen is first created under that flag, `DesktopController.OnAttach` skips building its UI entirely and the component is saved with all of its references null:

```
[C] DesktopController
    _displayColor    = null
    _displayRect     = null
    _desktopTexture  = null
    _interactionRelay= null
    _currentControl  = null
    _buttonRect      = null
```

Your dash is stored in the cloud, so a Windows client then downloads that empty screen. At runtime `DesktopScreen.OnStart` re-enables the tab (because the desktop is not disabled on Windows), but the contents were never built, so the tab renders nothing. `DesktopController.OnCommonUpdate` returns early on `_desktopTexture == null` before it can show the `≡` button, and it does so silently — nothing appears in the logs.

It cannot recover on its own. `DesktopScreen.OnLoading` only rebuilds the screen when the saved type version is older than the current one, and the broken screen is already saved at the current version.

## What the mod does

**Rebuilds an empty Desktop screen.** A frame after the screen starts, the mod checks whether a `DesktopTextureProvider` exists under the screen canvas. If it does not, the screen is empty, and the mod performs the same rebuild the engine's own version migration would (`ScreenCanvas.Slot.DestroyChildren()` then `DesktopScreen.Setup()`) and marks the dash modified so the repair is saved. After that it never has to run again.

**Lets you pick which screen to share.** The stock Share Screen button always shares the display currently shown in the Desktop tab. With Follow Cursor on, that snaps to whichever display your mouse is over — and your mouse has to be over the Resonite window to press the button — so in practice you can only ever share the screen Resonite is on. The mod adds a `▶` button next to Share Screen in Desktop Controls. Each press cycles to the next display, and the button label follows it: `Share Screen 1`, `Share Screen 2`, and so on, or `Stop Sharing Screen 2` while that display is being shared. The screen share dialog then confirms the choice with `Display 2: 2560x1600`. Your pick is remembered while the game is running. The `▶` button is hidden when only one display is connected.

**Local only shares, visible just to you.** The screen share dialog gets a `Local only (visible just to you)` checkbox under Include desktop audio. With it ticked, Start Sharing does not stream anything: it puts a plain view of that display in front of you that you can grab, move and scale. The picture exists only on your machine: nothing is encoded or sent over the network, and other users cannot see it or inspect it. To make grabbing work, the view hangs off an empty handle object that is part of the world. It holds nothing but a position and an invisible collider that only you can hit. It is never saved, and it is removed if you leave. Other users can find that empty handle in an inspector, but never the picture. This is handy for speaker notes on a second monitor. The frame rate, resolution, bitrate and audio options do not apply to it, and the world's video streaming permission does not either, since nothing is shared. Stop it with the same Stop Sharing Screen button as a normal share — both use that display's share token, so stopping one also stops a normal share of the same display. The checkbox remembers its last state until you restart.

**Optionally shares your screen without the player UI.** Starting a share spawns your VideoStream favorite — by default Resonite's video player, with its frame, title, volume and bottom-bar overlays. With `Desktop share without UI layers` turned on (it is off by default), the mod hides those overlay layers as soon as the player is bound to your screen, so everyone in the session sees just the picture. The volume and spatialization controls are hidden along with them. The layers are found by the names used in Resonite's stock player; if your VideoStream favorite is a custom player, nothing is hidden and a warning is logged.

**Keeps the capture display index valid across restarts.** The display you capture is persisted in the dash, and `DesktopController` silently does nothing when that display no longer exists, so a restart with fewer monitors can leave the desktop view blank until you pick a new display by hand. The mod falls back to display 0 when the stored index is out of range. This does not cover unplugging a monitor mid-session: Resonite never removes a display from its list once it has seen one, so the index still looks valid until you restart.

All of this is skipped on clients where the desktop is disabled, so the mod stays inert on Linux.

## Known issue: crashes when monitors change

Resonite's renderer can crash when a monitor is connected or disconnected while the Desktop tab is actively capturing. The crash is inside the renderer's native desktop capture plugins (`uDesktopDuplication.dll`, `uWindowCapture.dll`) and `d3d11.dll`, in the separate `Renderite.Renderer.exe` process, so no ResoniteModLoader mod can fix it. Until it is fixed upstream, switch your dash away from the Desktop tab before plugging in or unplugging a monitor. Capture stops whenever the tab is not active.

## Installation

There are no prebuilt releases. You build the mod yourself, which takes about a minute.

### What you need

- **Resonite**, installed. The build compiles against the game's own `FrooxEngine.dll`, `Elements.Core.dll`, `SkyFrost.Base.dll` and `SkyFrost.Base.Models.dll`, so it needs to find your Resonite folder.
- **[ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader)**, installed in that Resonite.
- **The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).** Check with `dotnet --list-sdks`; you want a `10.0.x` line. The runtime alone is not enough.
- **git**, or download the repository as a ZIP from GitHub.

The ResoniteModLoader and Harmony packages the build references are downloaded from nuget.org automatically the first time you build.

### Build and install

1. **Close Resonite.** While it is running it keeps the mod file locked, and the build cannot replace it.
1. Get the source:
   ```
   git clone https://github.com/DrSciCortex/MultiDesktopCaptureMod.git
   cd MultiDesktopCaptureMod
   ```
1. Build it:
   ```
   dotnet build MultiDesktopCaptureMod.slnx -c Release
   ```
   For a default Steam install (`C:\Program Files (x86)\Steam\steamapps\common\Resonite\` on Windows, `~/.steam/steam/steamapps/common/Resonite/` on Linux) that is all. The build finds Resonite and copies `MultiDesktopCaptureMod.dll` straight into its `rml_mods` folder.
1. Start Resonite. The mod loaded if your Resonite log contains `Loaded mod [MultiDesktopCaptureMod/…]`. If the empty Desktop screen repair runs, you will also see `Desktop screen has no contents … rebuilding it`.

### Resonite somewhere else

Pass the folder that contains `FrooxEngine.dll`. Use forward slashes and keep the trailing slash, since the build appends file names directly to it:

```
dotnet build MultiDesktopCaptureMod.slnx -c Release "-p:ResonitePath=D:/Games/Resonite/"
```

### Building without installing

Add `-p:CopyToMods=false`. The DLL is then left at `MultiDesktopCaptureMod/bin/Release/net10.0/MultiDesktopCaptureMod.dll`, and you copy it into `rml_mods` yourself.

### Updating

Close Resonite, then pull and rebuild:

```
git pull
dotnet build MultiDesktopCaptureMod.slnx -c Release
```

### Troubleshooting

- **`warning MSB3026: Could not copy … The file is locked by: "Renderite.Host"`.** Resonite is still running, so the copy failed and the old version is still installed. The build itself succeeded. Close Resonite and build again. Check Task Manager for a leftover `Renderite.Host` process if the window is already gone.
- **Lots of `error CS0246: The type or namespace name 'DesktopScreen' could not be found`** (or `DesktopControlDialog`, `Display` and so on). The build could not find Resonite. Pass `-p:ResonitePath` as above.
- **`NETSDK1045: The current .NET SDK does not support targeting .NET 10.0`.** Install the .NET 10 SDK.
- **The mod does not appear in the log.** Make sure ResoniteModLoader itself is loading: its lines start with `[ResoniteModLoader]`. If you manage mods with a mod manager such as Resolute, check that it points at the same Resonite folder you actually launch. It is easy to end up installing into a different copy.

### Changing settings

Install [ResoniteModSettings](https://github.com/badhaloninja/ResoniteModSettings) to change the settings below from the dash. Without it, edit `rml_config/MultiDesktopCaptureMod.json` in your Resonite folder while the game is closed. ResoniteModLoader only creates that file once a setting has been changed from its default.

## Configuration

| Key | Default | Effect |
| --- | --- | --- |
| `Enabled` | `true` | Master switch for everything below. |
| `RepairEmptyDesktopScreen` | `true` | Rebuild the Desktop dash screen if it was saved with no contents. |
| `ClampDisplayIndex` | `true` | Fall back to the first display when the selected capture display no longer exists. |
| `ShareScreenPicker` | `true` | Add the `▶` button that picks which display Share Screen shares. Takes effect the next time Desktop Controls is opened. |
| `Default share FPS` | `30` | Frame rate preselected in the screen share dialog: 15, 30 or 60. Resonite's own default is 60. |
| `Default share resolution` | `1080` | Vertical resolution preselected in the screen share dialog: 480, 720, 1080 or 2160. Ignored when it is taller than the shared display, since the dialog disables those options. |
| `FixResolutionLabels` | `true` | Fix the screen share dialog's resolution buttons. Resonite pairs the values 2160/1080/720/480 with the labels 480p/720p/1080p/4K, so every button is mislabeled: the 1080 button reads "720p", and pressing "1080p" actually selects 720. |
| `LocalOnlyOption` | `true` | Add the Local only checkbox to the screen share dialog. |
| `Desktop share without UI layers` | `false` | Hide the video player's frame and controls on your screen shares, so everyone sees only the picture. Applies to shares started after it is turned on. |

## Fixing it without the mod

Launching Resonite once with the `-ResetDash` argument also fixes it, by discarding the saved dash and regenerating it from defaults. The trade-off is that it resets your **whole** dash — your top-bar facets and any screen customisation go back to defaults — whereas this mod repairs only the Desktop screen. If you do use `-ResetDash`, run it from Windows; resetting from a Linux install recreates the same empty screen.
