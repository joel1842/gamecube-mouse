# GameCube Mouse

Use a GameCube controller as a mouse and keyboard on Windows. It reads the controller in **port 1** of a GameCube USB adapter (official Nintendo or Mayflash in Wii U mode) directly over USB. It has no other dependencies, runs in the system tray, and you turn it on and off with **Start**.

## Buttons

| Button | Action |
|---|---|
| Control stick | Move the cursor |
| A | Left click |
| B | Right click |
| X / Y | Forward / Back |
| Z | Open / close the on-screen keyboard |
| C-stick | Scroll (up/down and sideways) |
| D-pad up / down | Scroll |
| D-pad left / right | Volume down / up |
| L (analog) | Turbo: faster cursor the harder you press |
| R (analog) | Precision: slower cursor the harder you press |
| L + R clicked in | Middle click |
| Start | Turn the controller mouse on / off |

**Keyboard:** pops up when you click into a text field and moves so it doesn't cover what you're typing. While it's open: **B** = Backspace, **X** = Space, **Y** = Shift, **D-pad** = arrow keys. Point at a key and press A (hold A to repeat it).

## Install

1. Download **GameCubeMouse-Setup.exe** from [Releases](../../releases/latest) and run it. You don't need admin rights.
2. One-time driver step: run [Zadig](https://zadig.akeo.ie/), pick **WUP-028** (Options → List All Devices), choose **WinUSB** and click *Replace Driver*. This is the same driver Dolphin uses.
3. Plug the controller into port 1 and press **Start**.

To uninstall, go to Settings → Apps → GameCube Mouse.

## Make it your own

This project is open source under the [MIT license](LICENSE). Use it, fork it, and change it however you like.

- The whole program is one file: [`src/GameCubeMouse.cs`](src/GameCubeMouse.cs). Button mappings are in `MouseEngine.Process`, and the keyboard layout is in the `ControllerKeyboard` constructor.
- Run `build.cmd` to build `dist\GameCubeMouse.exe` and the installer. It uses the C# compiler that comes with Windows, so you don't need Visual Studio or any SDK.
- Pull requests and issues are welcome.
