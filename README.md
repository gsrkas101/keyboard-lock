# Keyboard Lock

Lock your laptop keyboard with one shortcut, so kids can't type on it by accident. It works like the
touchpad on/off key, but for the keyboard.

Windows 10/11 · single 50 KB exe · no install · no admin rights

![Keyboard locked box](docs/locked.png)

## Features
- **Ctrl+Alt+K** locks and unlocks. You can change it to any key or combination, such as a laptop's macro key.
- While locked, **every** key is blocked: letters, F1–F12, volume/media keys, the Windows key, Alt+Tab, and keys typed by software.
- Brightness keys are handled by the laptop's firmware and can't be blocked, so any brightness change is undone while locked.
- Padlock tray icon: click it to lock or unlock with the mouse. A "Keyboard locked" box shows on screen while locked.
- Starts as soon as you sign in to Windows, using a Task Scheduler task. No admin rights needed.
- Command line options `--toggle`, `--lock`, `--unlock`, `--exit`, so a vendor macro key or any launcher can control it.

## Download
Get `KeyboardLock.exe` from [Releases](../../releases) and run it. The padlock appears in the taskbar tray.

The exe isn't code-signed, so Windows SmartScreen may warn you: choose **More info → Run anyway**.
Or [build it yourself](#build-from-source). It needs .NET Framework 4.x, which is built into Windows 10 and 11.

## Safety
- **Ctrl+Alt+Del always works.** Windows doesn't let any app block it.
- The mouse or touchpad can always unlock: click the tray icon, or double-click the "Keyboard locked" box.
- It always starts unlocked, and it unlocks itself whenever Windows is locked or you sign in again.
- It changes nothing on your PC: no drivers and no device changes. Exiting the app, or the app crashing, gives the keyboard back instantly.
- A key-up is only blocked if its key-down was blocked, so no key ever gets stuck.
- If an error happens inside the keyboard hook, the key is let through. It never fails into a locked state.

## What it can't block
- Ctrl+Alt+Del (by design).
- Keys handled entirely by the laptop's firmware: keyboard backlight, touchpad on/off, and on some models airplane mode. The power button also still works.
- Keys pressed while an app running as administrator (like Task Manager) is in front.

## Using a laptop's macro key
Right-click the padlock → **Lock/unlock key → change**, then press the macro key. If nothing happens, the key
never reaches Windows as a normal key. In that case use **Copy command for Xiaomi key** (it works for any brand),
and in the vendor's control app set the macro key to run that command (`KeyboardLock.exe --toggle`).

## How it works
It uses a low-level keyboard hook (`WH_KEYBOARD_LL`) on a dedicated thread that does nothing else, so it always
answers fast. The decision logic (`KeyFilter`) is pure code with unit tests. Everything is in one C# file:
[`src/KeyboardLock.cs`](src/KeyboardLock.cs).

Keyloggers use keyboard hooks too, so some antivirus tools may be cautious about it. Keyboard Lock never
records, saves or sends keystrokes, and you can read the source to check.

## Build from source
```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```
This uses the C# compiler that ships with Windows, so no Visual Studio or SDK is needed. It runs the unit tests
first, then builds `KeyboardLock.exe`. GitHub Actions builds every push the same way and publishes the exe for
tagged releases.

## Settings and uninstalling
Settings are stored in `%APPDATA%\KeyboardLock\settings.ini`.

To uninstall: right-click the padlock and untick **Start with Windows**, then choose **Exit**. Then delete the exe
and the settings folder.

## License
[MIT](LICENSE)
