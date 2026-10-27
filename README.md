# Fluffy

A local screenshot and screen-recording tool for Windows, in the style of Snipping Tool and Gyazo.
Nothing is uploaded anywhere: every capture is saved as a file on your PC.

## Features

- **Screenshots**: drag an area, click a window, or grab the full screen. Works on hardware-accelerated
  windows (such as poker clients) that other tools capture as black.
- **Video (MP4) and GIF recording**, with a toolbar to pause/resume, switch sound on or off, stop or cancel.
- **Sound** in videos, from the computer, the microphone or both. Off by default.
- **Keep/delete popup** after every capture, plus a **library** of everything you've saved.
- **Print Screen key**, **hotkeys** and a **tray icon**.

| Action | Shortcut |
|---|---|
| Screenshot | Print Screen, Ctrl+Alt+S, or left-click the tray icon |
| Snip a window | Ctrl+Alt+W |
| Full screen | Ctrl+Alt+F |
| Record video | Ctrl+Alt+V |
| Record GIF | Ctrl+Alt+G |

While snipping, a left-drag picks an area, a left click takes the window under the cursor, and a right-click
or Esc cancels.

## Install

1. Download `Fluffy.exe` from the [Releases](../../releases) page.
2. Put it in a folder you'll keep, for example `Documents\Fluffy`, and run it.
3. Windows may show "Windows protected your PC" because Fluffy isn't digitally signed. Click
   **More info → Run anyway**.

Right-click the tray icon for **Settings** (save folder, video and GIF length, sound, start with Windows,
Print Screen).

### Print Screen on Windows 11

Windows sends Print Screen to Snipping Tool until you switch off **Settings → Accessibility → Keyboard →
"Use the Print screen key to open screen capture"**. Fluffy's Settings window has a button that opens that
page. You may need to sign out and back in afterwards.

### Poker clients

If a poker client's tables come out black, check whether it has a screen-protection option (GGPoker calls
it **Screen Guard**) and switch it off. Fluffy can't capture windows that deliberately block capture.

## Build from source

Needs nothing beyond Windows 10/11, which includes the .NET Framework 4.8 compiler. Run `build.bat`; it
produces `Fluffy.exe` next to the sources.

| File | What it is |
|---|---|
| `Fluffy.cs` | App, tray menu, snipping, popup, library, settings |
| `Recording.cs` | Video/GIF recording, sound capture, encoders, recording toolbar |
| `make_icon.ps1` | Generates `Fluffy.ico` |
| `build.bat` | Builds `Fluffy.exe` |

Settings are stored in `%APPDATA%\Fluffy\settings.ini`; captures go to `Pictures\Fluffy` by default.
