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

**Keyboard:** pops up when you click into a text field and moves so it doesn't cover what you're typing. While it's open, the **D-pad** moves a highlight between keys and **A** types the highlighted key (hold A to repeat it). **B** = Backspace, **X** = Space, **Y** = Shift, **Z** = close. If you move the stick, the highlight hides and A clicks normally again. Press the D-pad to bring it back.

## Install

1. Plug in the adapter (put a Mayflash into Wii U mode).
2. Download **GameCubeMouse-Setup.exe** from [Releases](../../releases/latest) and run it.
3. If setup says the adapter needs a driver, click **Set up adapter driver** and allow the Windows prompt. This one-time step points the adapter at Microsoft's built-in WinUSB driver, the same thing Zadig does for Dolphin. If you've used the adapter with Dolphin before, it's already done.
4. Plug the controller into port 1 and press **Start**.

If the driver setup fails, you can do it by hand with [Zadig](https://zadig.akeo.ie/): pick **WUP-028** (Options → List All Devices), choose **WinUSB**, then click *Replace Driver*.

To uninstall, go to Settings → Apps → GameCube Mouse. To build it yourself, run `build.cmd`. It uses the C# compiler that comes with Windows.
