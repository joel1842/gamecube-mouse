// GameCube Mouse
// Turns the controller in PORT 1 of a GameCube USB adapter (Nintendo WUP-028 or a Mayflash
// adapter in Wii U mode, using the WinUSB/Zadig driver) into a mouse.
//
//   Control stick = move cursor            Start = turn controller mouse on / off
//   A = left click     B = right click     Z = open / close the on-screen keyboard
//   Y = Back           X = Forward         C-stick = scroll
//   L (analog)    = turbo: the harder you press, the faster the cursor
//   R (analog)    = precision: the harder you press, the slower the cursor
//   L + R clicked all the way in = middle click
//   D-pad up/down = scroll             D-pad left/right = volume down/up
//
//   While the keyboard is open: D-pad = move the highlight, A = type it, B = Backspace, X = Space,
//   Y = Shift. Moving the stick hides the highlight and A clicks normally again.
//   The keyboard pops up by itself when you click into a text field (tray menu option), and
//   places itself so it doesn't cover the field you're typing into.
//
// Low-level design: the adapter is read directly over raw USB with winusb.dll (no Dolphin, no
// middleware), on a dedicated high-priority thread that wakes on every USB report (~125 Hz).
// Each report is turned straight into SendInput calls, which enter Windows' raw input stream.
//
// Game mode: while an emulator such as PCSX2 is running, the mouse turns off and the controller is
// handed to the game as a virtual Xbox 360 controller through the ViGEmBus driver (if installed).
// While Dolphin is running, the adapter is let go so Dolphin can read it itself.
//
// Build with build.cmd (uses the C# compiler that ships with Windows). No other dependencies.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace GameCubeMouse
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // Diagnostic mode: read the adapter for 2 seconds, write what it saw, never touch the mouse.
            if (args.Length > 0 && args[0] == "--probe")
                return Probe.Run(args.Length > 1 ? args[1] : "probe.txt");

            try { Native.SetProcessDPIAware(); } catch { }   // crisp text on high-DPI screens

            // Diagnostic mode: draw the on-screen keyboard to a PNG file.
            if (args.Length > 0 && args[0] == "--render-keyboard")
            {
                using (var kb = new ControllerKeyboard())
                using (var bmp = new Bitmap(kb.Width, kb.Height))
                {
                    kb.ShowFocus = true;
                    kb.DrawToBitmap(bmp, new Rectangle(0, 0, kb.Width, kb.Height));
                    bmp.Save(args.Length > 1 ? args[1] : "keyboard.png", System.Drawing.Imaging.ImageFormat.Png);
                }
                return 0;
            }

            // Diagnostic mode: report what text field has focus right now (as the auto-popup sees it).
            if (args.Length > 0 && args[0] == "--focus-probe")
            {
                string text = "no result";
                using (var done = new ManualResetEvent(false))
                using (var w = new FocusWatcher(r =>
                {
                    text = string.Format("editable={0} ignore={1} hasTarget={2} target={3}", r.Editable, r.Ignore, r.HasTarget, r.Target);
                    done.Set();
                }))
                {
                    w.Request(FocusWatcher.Reason.Manual);
                    done.WaitOne(5000);
                }
                File.WriteAllText(args.Length > 1 ? args[1] : "focus.txt", text);
                return 0;
            }

            bool firstInstance;
            using (var mutex = new Mutex(true, @"Local\GameCubeMouse.SingleInstance", out firstInstance))
            {
                if (!firstInstance)
                {
                    MessageBox.Show("GameCube Mouse is already running.\nLook for its icon in the system tray, next to the clock.",
                        "GameCube Mouse", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
            return 0;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Raw USB access to the adapter
    // ------------------------------------------------------------------------------------------
    sealed class GCAdapter : IDisposable
    {
        public const int ReportLength = 37;        // 1 header byte (0x21) + 4 ports x 9 bytes
        const string HardwareId = "vid_057e&pid_0337";
        static readonly Guid GuidUsbDevice = new Guid("A5DCBF10-6530-11D2-901F-00C04FB951ED");

        SafeFileHandle file;
        IntPtr usb;
        byte inPipe = 0x81, outPipe = 0x02;
        public int LastError { get; private set; }

        GCAdapter(SafeFileHandle file, IntPtr usb) { this.file = file; this.usb = usb; }

        public string Describe()
        {
            return "IN pipe 0x" + inPipe.ToString("X2") + ", OUT pipe 0x" + outPipe.ToString("X2");
        }

        public static GCAdapter Open(out string error)
        {
            error = "GameCube adapter not found";
            foreach (string path in FindPaths())
            {
                SafeFileHandle h = Native.CreateFile(path, Native.GENERIC_READ | Native.GENERIC_WRITE,
                    Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING,
                    Native.FILE_ATTRIBUTE_NORMAL | Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                if (h.IsInvalid)
                {
                    int e = Marshal.GetLastWin32Error();
                    error = (e == 5 || e == 32) ? "Adapter is in use by another program (Dolphin?)"
                                                : "Couldn't open adapter (error " + e + ")";
                    h.Dispose();
                    continue;
                }
                IntPtr usb;
                if (!Native.WinUsb_Initialize(h, out usb))
                {
                    error = "Adapter needs the WinUSB (Zadig) driver (error " + Marshal.GetLastWin32Error() + ")";
                    h.Dispose();
                    continue;
                }
                var adapter = new GCAdapter(h, usb);
                if (adapter.Start(out error)) return adapter;
                adapter.Dispose();
            }
            return null;
        }

        // Device paths for the adapter: first via the interface GUID Zadig registered, then via the
        // generic USB device interface.
        public static List<string> FindPaths()
        {
            var guids = new List<Guid>();
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB\VID_057E&PID_0337"))
                {
                    if (k != null)
                        foreach (string inst in k.GetSubKeyNames())
                            using (var dp = k.OpenSubKey(inst + @"\Device Parameters"))
                            {
                                if (dp == null) continue;
                                var multi = dp.GetValue("DeviceInterfaceGUIDs") as string[];
                                if (multi != null) foreach (string g in multi) AddGuid(guids, g);
                                AddGuid(guids, dp.GetValue("DeviceInterfaceGUID") as string);
                            }
                }
            }
            catch { }
            if (!guids.Contains(GuidUsbDevice)) guids.Add(GuidUsbDevice);

            var paths = new List<string>();
            foreach (Guid g in guids)
                foreach (string p in EnumerateInterfaces(g))
                    if (p.ToLowerInvariant().Contains(HardwareId) && !paths.Contains(p)) paths.Add(p);
            return paths;
        }

        static void AddGuid(List<Guid> list, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            try { var g = new Guid(s.Trim()); if (!list.Contains(g)) list.Add(g); } catch { }
        }

        public static List<string> EnumerateInterfaces(Guid guid)
        {
            var result = new List<string>();
            IntPtr set = Native.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == Native.INVALID_HANDLE_VALUE) return result;
            try
            {
                var data = new Native.SP_DEVICE_INTERFACE_DATA();
                data.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVICE_INTERFACE_DATA));
                for (uint i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
                {
                    uint needed;
                    Native.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out needed, IntPtr.Zero);
                    if (needed == 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)needed);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);   // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                        if (Native.SetupDiGetDeviceInterfaceDetail(set, ref data, detail, needed, out needed, IntPtr.Zero))
                            result.Add(Marshal.PtrToStringUni(IntPtr.Add(detail, 4)));
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            return result;
        }

        bool Start(out string error)
        {
            Native.USB_INTERFACE_DESCRIPTOR desc;
            if (Native.WinUsb_QueryInterfaceSettings(usb, 0, out desc))
            {
                for (byte i = 0; i < desc.bNumEndpoints; i++)
                {
                    Native.WINUSB_PIPE_INFORMATION pipe;
                    if (!Native.WinUsb_QueryPipe(usb, 0, i, out pipe) || pipe.PipeType != 3) continue;   // 3 = interrupt
                    if ((pipe.PipeId & 0x80) != 0) inPipe = pipe.PipeId; else outPipe = pipe.PipeId;
                }
            }
            uint timeout = 500;   // ms; lets the reader notice shutdown/unplug instead of blocking forever
            Native.WinUsb_SetPipePolicy(usb, inPipe, Native.PIPE_TRANSFER_TIMEOUT, 4, ref timeout);
            Native.WinUsb_SetPipePolicy(usb, outPipe, Native.PIPE_TRANSFER_TIMEOUT, 4, ref timeout);

            byte[] startPolling = { 0x13 };
            uint written;
            if (!Native.WinUsb_WritePipe(usb, outPipe, startPolling, 1, out written, IntPtr.Zero))
            {
                error = "Adapter didn't respond (error " + Marshal.GetLastWin32Error() + ")";
                return false;
            }
            error = null;
            return true;
        }

        // Blocks until the adapter sends its next report. Returns bytes read, 0 on timeout, -1 on failure.
        public int Read(byte[] buffer)
        {
            uint got;
            if (Native.WinUsb_ReadPipe(usb, inPipe, buffer, (uint)buffer.Length, out got, IntPtr.Zero))
                return (int)got;
            LastError = Marshal.GetLastWin32Error();
            return LastError == Native.ERROR_SEM_TIMEOUT ? 0 : -1;
        }

        public void Dispose()
        {
            if (usb != IntPtr.Zero) { Native.WinUsb_Free(usb); usb = IntPtr.Zero; }
            if (file != null) { file.Dispose(); file = null; }
        }
    }

    // ------------------------------------------------------------------------------------------
    // Virtual Xbox 360 controller through the ViGEmBus driver (talked to directly with
    // DeviceIoControl, so no ViGEmClient.dll is needed). Used in game mode so emulators and
    // games see the GameCube controller as an ordinary XInput pad.
    // ------------------------------------------------------------------------------------------
    sealed class VirtualPad : IDisposable
    {
        static readonly Guid BusInterface = new Guid("96E42B22-F5E9-42F8-B043-ED0F932F014F");
        const uint IoctlPlugin = 0x2AA004, IoctlUnplug = 0x2AA008, IoctlCheckVersion = 0x2AA00C,
                   IoctlWaitReady = 0x2AA010, IoctlSubmitXusb = 0x2AA808;

        // XUSB button bits
        public const ushort Up = 0x0001, Down = 0x0002, Left = 0x0004, Right = 0x0008, Start = 0x0010, Back = 0x0020;
        public const ushort LB = 0x0100, RB = 0x0200, Guide = 0x0400, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

        SafeFileHandle bus;
        uint serial;
        readonly byte[] report = new byte[20];
        readonly byte[] lastSent = new byte[20];
        bool sentOnce;

        VirtualPad(SafeFileHandle bus, uint serial) { this.bus = bus; this.serial = serial; }



        public static VirtualPad Plug(out string error)
        {
            var paths = GCAdapter.EnumerateInterfaces(BusInterface);
            if (paths.Count == 0) { error = "ViGEmBus driver not installed"; return null; }
            foreach (string path in paths)
            {
                SafeFileHandle h = Native.CreateFile(path, Native.GENERIC_READ | Native.GENERIC_WRITE,
                    Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING,
                    Native.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
                if (h.IsInvalid) { h.Dispose(); continue; }

                var version = new byte[8];
                BitConverter.GetBytes(8u).CopyTo(version, 0);
                BitConverter.GetBytes((ushort)1).CopyTo(version, 4);
                if (!Ioctl(h, IoctlCheckVersion, version)) { h.Dispose(); continue; }

                for (uint s = 1; s <= 16; s++)
                {
                    var plugin = new byte[16];
                    BitConverter.GetBytes(16u).CopyTo(plugin, 0);
                    BitConverter.GetBytes(s).CopyTo(plugin, 4);
                    // TargetType 0 = Xbox 360 wired; vendor/product 0 = driver default (045E:028E)
                    if (!Ioctl(h, IoctlPlugin, plugin)) continue;
                    var wait = new byte[8];
                    BitConverter.GetBytes(8u).CopyTo(wait, 0);
                    BitConverter.GetBytes(s).CopyTo(wait, 4);
                    Ioctl(h, IoctlWaitReady, wait);   // older drivers don't know this one; that's fine
                    error = null;
                    return new VirtualPad(h, s);
                }
                h.Dispose();
            }
            error = "Couldn't create a virtual controller";
            return null;
        }

        static bool Ioctl(SafeFileHandle h, uint code, byte[] data)
        {
            uint returned;
            return Native.DeviceIoControl(h, code, data, (uint)data.Length, null, 0, out returned, IntPtr.Zero);
        }

        public bool Submit(ushort buttons, byte lt, byte rt, short lx, short ly, short rx, short ry)
        {
            BitConverter.GetBytes(20u).CopyTo(report, 0);
            BitConverter.GetBytes(serial).CopyTo(report, 4);
            BitConverter.GetBytes(buttons).CopyTo(report, 8);
            report[10] = lt; report[11] = rt;
            BitConverter.GetBytes(lx).CopyTo(report, 12);
            BitConverter.GetBytes(ly).CopyTo(report, 14);
            BitConverter.GetBytes(rx).CopyTo(report, 16);
            BitConverter.GetBytes(ry).CopyTo(report, 18);
            bool same = sentOnce;
            for (int i = 8; i < 20 && same; i++) same = report[i] == lastSent[i];
            if (same) return true;
            if (!Ioctl(bus, IoctlSubmitXusb, report)) return false;
            Buffer.BlockCopy(report, 0, lastSent, 0, 20);
            sentOnce = true;
            return true;
        }

        public void Dispose()
        {
            if (bus == null) return;
            var unplug = new byte[8];
            BitConverter.GetBytes(8u).CopyTo(unplug, 0);
            BitConverter.GetBytes(serial).CopyTo(unplug, 4);
            Ioctl(bus, IoctlUnplug, unplug);
            bus.Dispose();
            bus = null;
        }
    }

    // What the reader thread should do with the controller right now.
    enum GameMode
    {
        None,          // normal: controller mouse
        VirtualPad,    // a game/emulator is running: mouse off, controller fed to it as an Xbox pad
        Released,      // a program that reads the adapter itself (Dolphin) is running: let go of it
    }

    // ------------------------------------------------------------------------------------------
    // Controller -> mouse translation (runs on its own thread)
    // ------------------------------------------------------------------------------------------
    sealed class MouseEngine
    {
        public static readonly string[] SpeedNames = { "Slow", "Medium", "Fast", "Very fast" };
        static readonly double[] SpeedPixelsPerSecond = { 600, 1100, 1700, 2500 };

        // Button bits: byte 1 of the port in the low 8 bits, byte 2 in the high bits.
        const int BtnA = 0x01, BtnB = 0x02, BtnX = 0x04, BtnY = 0x08;
        const int DLeft = 0x10, DRight = 0x20, DDown = 0x40, DUp = 0x80;
        const int BtnStart = 0x100, BtnZ = 0x200, BtnR = 0x400, BtnL = 0x800;
        const int AllButtons = 0xFFF;
        const int ModeButtons = BtnB | BtnX | BtnY | DLeft | DRight | DDown | DUp;   // mean something else while typing

        const int StickDeadzone = 12, StickRange = 75;     // GameCube sticks reach roughly 75 from center
        const int CStickDeadzone = 22, CStickRange = 70;
        const int CalibrationWindow = 25;                  // only trust a resting position this close to 128
        const int TriggerDeadzone = 12, TriggerTravel = 170;   // analog L/R rest near 30 and reach ~210 before the click
        const double TurboBoost = 1.5;                     // L all the way = 2.5x cursor speed
        const double PrecisionCut = 0.8;                   // R all the way = 20% cursor speed

        sealed class ScrollAxis { public int Direction; public double Held, Pending; }
        sealed class HeldKey { public bool Down, Extended; public ushort Vk; public double Held, NextRepeat; }
        sealed class ScrollButton { public bool Down; public double Held, Pending; }

        volatile bool enabled = true, running, recenterRequested, controllerConnected, keyboardMode;
        volatile int speedIndex = 1;
        volatile int gameMode;           // GameMode, set by the UI from which programs are running
        volatile string status = "Starting...";
        readonly ManualResetEvent stopSignal = new ManualResetEvent(false);
        Thread thread;

        // Reader-thread state
        bool calibrated, cCalibrated, tCalibrated, prevStart, lastKeyboardMode;
        bool leftDown, rightDown, middleDown, backDown, forwardDown;
        int blocked = AllButtons, prevOn;      // blocked = held through a mode change; ignored until let go
        int centerX = 128, centerY = 128, cCenterX = 128, cCenterY = 128, lRest = 30, rRest = 30;
        double moveX, moveY;
        readonly ScrollAxis vertical = new ScrollAxis(), horizontal = new ScrollAxis();
        readonly ScrollButton scrollUp = new ScrollButton(), scrollDown = new ScrollButton();
        readonly HeldKey volDown = new HeldKey(), volUp = new HeldKey();
        readonly HeldKey backspace = new HeldKey(), space = new HeldKey();
        readonly HeldKey navUp = new HeldKey(), navDown = new HeldKey(), navLeft = new HeldKey(), navRight = new HeldKey();
        readonly HeldKey[] allKeys;
        bool navMode, aTyping;           // navMode: the D-pad highlight is showing and A types it
        double stickTravel;              // cursor movement since the last D-pad press (hides the highlight)

        public MouseEngine()
        {
            allKeys = new[] { volDown, volUp, backspace, space };
        }

        public event Action<bool> Toggled;
        public event Action StatusChanged;
        public event Action KeyboardRequested;   // Z
        public event Action ShiftRequested;      // Y while the keyboard is open
        public event Action LeftClicked;         // A let go (used to pop the keyboard up in text fields)
        public event Action<int> NavRequested;   // D-pad on the keyboard: 0 up, 1 down, 2 left, 3 right
        public event Action<bool> KeyPressRequested;   // A down / up on the highlighted key
        public event Action<bool> NavModeChanged;      // show / hide the highlight

        public string Status { get { return status; } }
        public bool LeftHeld { get { return leftDown; } }   // A is holding the left mouse button down right now
        public bool ControllerConnected { get { return controllerConnected; } }
        public int SpeedIndex
        {
            get { return speedIndex; }
            set { speedIndex = Math.Max(0, Math.Min(SpeedNames.Length - 1, value)); }
        }
        // Set by the UI while the on-screen keyboard is showing: B/X/Y and the D-pad become typing keys.
        public bool KeyboardMode
        {
            get { return keyboardMode; }
            set { keyboardMode = value; }
        }
        public bool Enabled
        {
            get { return enabled; }
            set
            {
                if (enabled == value) return;
                enabled = value;
                var h = Toggled;
                if (h != null) h(value);
            }
        }
        public void Recenter() { recenterRequested = true; }
        public GameMode GameMode
        {
            get { return (GameMode)gameMode; }
            set { gameMode = (int)value; }
        }

        public void Start()
        {
            running = true;
            thread = new Thread(Run) { IsBackground = true, Name = "GameCube reader", Priority = ThreadPriority.Highest };
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            stopSignal.Set();
            if (thread != null) thread.Join(2000);
        }

        void SetStatus(bool controller, string text)
        {
            if (controllerConnected == controller && status == text) return;
            controllerConnected = controller;
            status = text;
            var h = StatusChanged;
            if (h != null) h();
        }

        void Run()
        {
            GCAdapter adapter = null;
            VirtualPad pad = null;
            double padRetryAt = 0;
            var report = new byte[GCAdapter.ReportLength];
            var clock = Stopwatch.StartNew();
            double last = 0;
            try
            {
                while (running)
                {
                    GameMode mode = GameMode;

                    // Game mode: plug in / unplug the virtual Xbox controller.
                    if (mode != GameMode.VirtualPad && pad != null) { pad.Dispose(); pad = null; }
                    if (mode == GameMode.VirtualPad && pad == null && clock.Elapsed.TotalSeconds >= padRetryAt)
                    {
                        string padError;
                        ReleaseAll();
                        pad = VirtualPad.Plug(out padError);
                        if (pad == null) { padRetryAt = clock.Elapsed.TotalSeconds + 5; padStatus = "Game mode: mouse off (" + padError + ")"; }
                        else padStatus = "Game mode: controller goes to the game";
                        statusDirty = true;
                    }
                    this.pad = pad;

                    // A program that reads the adapter itself (Dolphin) is running: let go of it.
                    if (mode == GameMode.Released)
                    {
                        if (adapter != null) { adapter.Dispose(); adapter = null; }
                        ReleaseAll();
                        SetStatus(false, "Paused: Dolphin is using the adapter");
                        if (stopSignal.WaitOne(1000)) break;
                        continue;
                    }

                    if (adapter == null)
                    {
                        string error;
                        adapter = GCAdapter.Open(out error);
                        if (adapter == null)
                        {
                            ReleaseAll();
                            SetStatus(false, error);
                            if (stopSignal.WaitOne(1500)) break;
                            continue;
                        }
                        SetStatus(false, "No controller in port 1");
                        last = clock.Elapsed.TotalSeconds;
                    }

                    int n = adapter.Read(report);
                    if (n < 0)
                    {
                        adapter.Dispose();
                        adapter = null;
                        ReleaseAll();
                        SetStatus(false, "Adapter disconnected");
                        if (stopSignal.WaitOne(500)) break;
                        continue;
                    }
                    if (n < GCAdapter.ReportLength || report[0] != 0x21) continue;

                    double now = clock.Elapsed.TotalSeconds;
                    double dt = Math.Min(now - last, 0.05);
                    last = now;
                    Process(report, dt);
                }
            }
            finally
            {
                ReleaseAll();
                if (pad != null) pad.Dispose();
                this.pad = null;
                if (adapter != null) adapter.Dispose();
            }
        }

        static bool NearCenter(int v) { return Math.Abs(v - 128) <= CalibrationWindow; }

        void Process(byte[] r, double dt)
        {
            const int o = 1;                          // port 1 occupies bytes 1..9
            int type = r[o] >> 4;                     // 1 = wired controller, 2 = WaveBird
            bool present = type == 1 || type == 2;
            if (present != controllerConnected)
            {
                calibrated = cCalibrated = false;
                prevStart = true;                     // don't treat a Start held while plugging in as a toggle
                blocked = AllButtons;
                if (!present) ReleaseAll();
                SetStatus(present, present ? "Controller connected in port 1" : "No controller in port 1");
            }
            if (!present) return;

            byte b1 = r[o + 1], b2 = r[o + 2];
            int sx = r[o + 3], sy = r[o + 4], cx = r[o + 5], cy = r[o + 6], lAnalog = r[o + 7], rAnalog = r[o + 8];

            if (recenterRequested) { recenterRequested = false; calibrated = cCalibrated = tCalibrated = false; }
            if (!calibrated && NearCenter(sx) && NearCenter(sy)) { centerX = sx; centerY = sy; calibrated = true; }
            if (!cCalibrated && NearCenter(cx) && NearCenter(cy)) { cCenterX = cx; cCenterY = cy; cCalibrated = true; }
            if (!tCalibrated && lAnalog < 90 && rAnalog < 90) { lRest = lAnalog; rRest = rAnalog; tCalibrated = true; }

            if (GameMode == GameMode.VirtualPad)
            {
                ReleaseAll();
                prevStart = true;                      // Start doesn't toggle the mouse while gaming
                statusDirty = true;                    // put the normal status back once game mode ends
                SetStatus(true, padStatus);
                VirtualPad p = pad;
                if (p != null) FeedPad(p, b1, b2, sx, sy, cx, cy, lAnalog, rAnalog);
                return;
            }
            if (statusDirty) { statusDirty = false; SetStatus(true, "Controller connected in port 1"); }

            bool start = (b2 & 0x01) != 0;
            if (start && !prevStart) Enabled = !enabled;
            prevStart = start;

            if (!enabled) { ReleaseAll(); return; }

            int buttons = b1 | (b2 << 8);
            blocked &= buttons;                        // a blocked button is usable again once let go
            bool typing = keyboardMode;
            if (typing != lastKeyboardMode)
            {
                lastKeyboardMode = typing;
                ReleaseModeKeys();
                blocked |= buttons & (ModeButtons | BtnA);   // e.g. A held on Enter must not turn into a click once the keyboard closes
                SetNavMode(typing);                    // keyboard just opened: the D-pad picks keys
            }
            int on = buttons & ~blocked;
            int pressedNow = on & ~prevOn;
            prevOn = on;

            // While the keyboard is open, A types the highlighted key, unless the stick has been used since
            // the last D-pad press: then A is an ordinary click again (to click into another field, etc.).
            bool aDown = (on & BtnA) != 0;
            if (aTyping || (typing && navMode && !leftDown))
            {
                if (aDown != aTyping) { aTyping = aDown; RaiseBool(KeyPressRequested, aDown); }
            }
            else
            {
                bool wasLeft = leftDown;
                SetButton(ref leftDown, aDown, Native.MOUSEEVENTF_LEFTDOWN, Native.MOUSEEVENTF_LEFTUP, 0);
                if (wasLeft && !leftDown) Raise(LeftClicked);
            }
            SetButton(ref middleDown, (on & (BtnL | BtnR)) == (BtnL | BtnR), Native.MOUSEEVENTF_MIDDLEDOWN, Native.MOUSEEVENTF_MIDDLEUP, 0);
            if ((pressedNow & BtnZ) != 0) Raise(KeyboardRequested);

            if (typing)
            {
                UpdateKey(backspace, (on & BtnB) != 0, Native.VK_BACK, false, 0.05, dt);
                UpdateKey(space, (on & BtnX) != 0, Native.VK_SPACE, false, 0.05, dt);
                if ((pressedNow & BtnY) != 0) Raise(ShiftRequested);
                UpdateNav(navUp, (on & DUp) != 0, 0, dt);
                UpdateNav(navDown, (on & DDown) != 0, 1, dt);
                UpdateNav(navLeft, (on & DLeft) != 0, 2, dt);
                UpdateNav(navRight, (on & DRight) != 0, 3, dt);
            }
            else
            {
                SetButton(ref rightDown, (on & BtnB) != 0, Native.MOUSEEVENTF_RIGHTDOWN, Native.MOUSEEVENTF_RIGHTUP, 0);
                SetButton(ref backDown, (on & BtnY) != 0, Native.MOUSEEVENTF_XDOWN, Native.MOUSEEVENTF_XUP, Native.XBUTTON1);
                SetButton(ref forwardDown, (on & BtnX) != 0, Native.MOUSEEVENTF_XDOWN, Native.MOUSEEVENTF_XUP, Native.XBUTTON2);
                ScrollWithButton(scrollUp, (on & DUp) != 0, 1, dt);
                ScrollWithButton(scrollDown, (on & DDown) != 0, -1, dt);
                UpdateKey(volDown, (on & DLeft) != 0, Native.VK_VOLUME_DOWN, true, 0.1, dt);   // held: 10 steps/s
                UpdateKey(volUp, (on & DRight) != 0, Native.VK_VOLUME_UP, true, 0.1, dt);
            }

            double speedScale = 1;
            if (tCalibrated)
                speedScale = (1 + TurboBoost * Pressure(lAnalog, lRest)) * (1 - PrecisionCut * Pressure(rAnalog, rRest));
            if (calibrated) MoveCursor(sx - centerX, centerY - sy, dt, speedScale);   // stick up = cursor up
            if (cCalibrated)
            {
                Scroll(vertical, cy - cCenterY, dt, Native.MOUSEEVENTF_WHEEL);     // C up = scroll up
                Scroll(horizontal, cx - cCenterX, dt, Native.MOUSEEVENTF_HWHEEL);  // C right = scroll right
            }
        }

        volatile VirtualPad pad;
        volatile bool statusDirty;
        volatile string padStatus = "";

        // GameCube -> Xbox layout, by position: A = A, B = X (left), X = B (right), Y = Y (top),
        // Z = RB, analog L / R = LT / RT, L clicked all the way in also = LB,
        // Start = Start, Z + Start = Guide, Z + X = Back. Control stick = left stick, C-stick = right stick.
        void FeedPad(VirtualPad p, byte b1, byte b2, int sx, int sy, int cx, int cy, int lAnalog, int rAnalog)
        {
            int buttons = b1 | (b2 << 8);
            ushort x = 0;
            if ((buttons & BtnA) != 0) x |= VirtualPad.A;
            if ((buttons & BtnB) != 0) x |= VirtualPad.X;
            if ((buttons & BtnX) != 0) x |= VirtualPad.B;
            if ((buttons & BtnY) != 0) x |= VirtualPad.Y;
            if ((buttons & DUp) != 0) x |= VirtualPad.Up;
            if ((buttons & DDown) != 0) x |= VirtualPad.Down;
            if ((buttons & DLeft) != 0) x |= VirtualPad.Left;
            if ((buttons & DRight) != 0) x |= VirtualPad.Right;
            if ((buttons & BtnL) != 0) x |= VirtualPad.LB;
            // Z works as a shift key: Z + Start = Guide (opens the emulator's menu), Z + X = Back (Select).
            bool z = (buttons & BtnZ) != 0, start = (buttons & BtnStart) != 0, xBtn = (buttons & BtnX) != 0;
            if (z && start) x |= VirtualPad.Guide;
            else if (z && xBtn) { x |= VirtualPad.Back; x = (ushort)(x & ~VirtualPad.B); }
            else
            {
                if (z) x |= VirtualPad.RB;
                if (start) x |= VirtualPad.Start;
            }

            byte lt = 0, rt = 0;
            if (tCalibrated)
            {
                lt = (byte)Math.Round(255 * PadTrigger(lAnalog, lRest));
                rt = (byte)Math.Round(255 * PadTrigger(rAnalog, rRest));
            }
            if ((buttons & BtnL) != 0) lt = 255;
            if ((buttons & BtnR) != 0) rt = 255;

            short lx = 0, ly = 0, rx = 0, ry = 0;
            if (calibrated) { lx = PadAxis(sx - centerX, StickRange); ly = PadAxis(sy - centerY, StickRange); }
            if (cCalibrated) { rx = PadAxis(cx - cCenterX, CStickRange); ry = PadAxis(cy - cCenterY, CStickRange); }

            p.Submit(x, lt, rt, lx, ly, rx, ry);
        }

        static short PadAxis(int offset, int range)
        {
            const int dead = 4;
            if (Math.Abs(offset) <= dead) return 0;
            double v = (Math.Abs(offset) - dead) / (double)(range - dead);
            v = Math.Min(1.0, v) * Math.Sign(offset);
            return (short)Math.Round(v * 32767);
        }

        static double PadTrigger(int value, int rest)
        {
            const int dead = 6, travel = 180;
            return Math.Max(0.0, Math.Min(1.0, (value - rest - dead) / (double)travel));
        }

        static double Pressure(int value, int rest)
        {
            return Math.Max(0.0, Math.Min(1.0, (value - rest - TriggerDeadzone) / (double)TriggerTravel));
        }

        static void Raise(Action h) { if (h != null) h(); }
        static void RaiseBool(Action<bool> h, bool v) { if (h != null) h(v); }

        void SetNavMode(bool value)
        {
            stickTravel = 0;
            if (navMode == value) return;
            navMode = value;
            RaiseBool(NavModeChanged, value);
        }

        // D-pad on the keyboard: move the highlight once per press; held, it keeps moving like a held key.
        void UpdateNav(HeldKey k, bool pressed, int dir, double dt)
        {
            if (!pressed) { k.Down = false; return; }
            if (!k.Down)
            {
                k.Down = true; k.Held = 0; k.NextRepeat = 0.35;
                bool wasShowing = navMode;
                SetNavMode(true);
                if (wasShowing) RaiseNav(dir);       // first press after using the stick just brings the highlight back
                return;
            }
            k.Held += dt;
            while (k.Held >= k.NextRepeat) { k.NextRepeat += 0.09; RaiseNav(dir); }
        }

        void RaiseNav(int dir) { var h = NavRequested; if (h != null) h(dir); }

        // Injected keys don't auto-repeat by themselves, so a held button repeats here like a real key.
        static void UpdateKey(HeldKey k, bool pressed, ushort vk, bool extended, double interval, double dt)
        {
            if (pressed && !k.Down)
            {
                k.Down = true; k.Vk = vk; k.Extended = extended; k.Held = 0; k.NextRepeat = 0.4;
                Native.SendKey(vk, false, extended);
                return;
            }
            if (!pressed)
            {
                if (k.Down) { k.Down = false; Native.SendKey(k.Vk, true, k.Extended); }
                return;
            }
            k.Held += dt;
            while (k.Held >= k.NextRepeat) { k.NextRepeat += interval; Native.SendKey(k.Vk, false, k.Extended); }
        }

        // D-pad up/down: one notch per tap; held, it repeats and speeds up (6 to 20 notches/s).
        static void ScrollWithButton(ScrollButton s, bool pressed, int dir, double dt)
        {
            if (!pressed) { s.Down = false; return; }
            if (!s.Down)
            {
                s.Down = true; s.Held = 0; s.Pending = 0;
                Native.SendMouse(0, 0, dir * 120, Native.MOUSEEVENTF_WHEEL);
                return;
            }
            s.Held += dt;
            if (s.Held < 0.3) return;
            double t = Math.Min(1.0, (s.Held - 0.3) / 1.5);
            s.Pending += (6 + 14 * t) * dt;
            while (s.Pending >= 1) { s.Pending -= 1; Native.SendMouse(0, 0, dir * 120, Native.MOUSEEVENTF_WHEEL); }
        }

        void MoveCursor(double dx, double dy, double dt, double speedScale)
        {
            double r = Math.Sqrt(dx * dx + dy * dy);
            if (r <= StickDeadzone) { moveX = moveY = 0; return; }
            double t = Math.Min(1.0, (r - StickDeadzone) / (StickRange - StickDeadzone));
            double speed = SpeedPixelsPerSecond[speedIndex] * speedScale * (0.2 * t + 0.8 * t * t);   // gentle near center, fast at the edge
            moveX += dx / r * speed * dt;
            moveY += dy / r * speed * dt;
            int mx = (int)moveX, my = (int)moveY;    // whole pixels now, keep the remainder for smooth slow motion
            if (mx == 0 && my == 0) return;
            moveX -= mx;
            moveY -= my;
            Native.SendMouse(mx, my, 0, Native.MOUSEEVENTF_MOVE);
            if (navMode && !aTyping)
            {
                stickTravel += Math.Abs(mx) + Math.Abs(my);
                if (stickTravel > 30) SetNavMode(false);   // using the stick hands A back to clicking
            }
        }

        static void Scroll(ScrollAxis axis, int offset, double dt, uint flag)
        {
            if (Math.Abs(offset) <= CStickDeadzone) { axis.Direction = 0; return; }
            int dir = offset > 0 ? 1 : -1;
            if (axis.Direction != dir)
            {
                axis.Direction = dir; axis.Held = 0; axis.Pending = 0;
                Native.SendMouse(0, 0, dir * 120, flag);    // a flick scrolls one notch right away
                return;
            }
            axis.Held += dt;
            if (axis.Held < 0.25) return;                   // short pause before repeating, like a held key
            double t = Math.Min(1.0, (Math.Abs(offset) - CStickDeadzone) / (double)(CStickRange - CStickDeadzone));
            axis.Pending += (4 + 16 * t * t) * dt;          // 4 to 20 notches per second
            while (axis.Pending >= 1) { axis.Pending -= 1; Native.SendMouse(0, 0, dir * 120, flag); }
        }

        static void SetButton(ref bool state, bool pressed, uint downFlag, uint upFlag, int data)
        {
            if (pressed == state) return;
            state = pressed;
            Native.SendMouse(0, 0, data, pressed ? downFlag : upFlag);
        }

        // Lets go of every button and key we're holding (used when turned off, unplugged or exiting).
        void ReleaseAll()
        {
            SetButton(ref leftDown, false, 0, Native.MOUSEEVENTF_LEFTUP, 0);
            SetButton(ref middleDown, false, 0, Native.MOUSEEVENTF_MIDDLEUP, 0);
            ReleaseModeKeys();
            blocked = AllButtons;                        // must be let go and pressed again after turning back on
            prevOn = 0;
            moveX = moveY = 0;
            vertical.Direction = horizontal.Direction = 0;
        }

        // Everything whose meaning changes when the keyboard opens or closes.
        void ReleaseModeKeys()
        {
            SetButton(ref rightDown, false, 0, Native.MOUSEEVENTF_RIGHTUP, 0);
            SetButton(ref backDown, false, 0, Native.MOUSEEVENTF_XUP, Native.XBUTTON1);
            SetButton(ref forwardDown, false, 0, Native.MOUSEEVENTF_XUP, Native.XBUTTON2);
            foreach (HeldKey k in allKeys)
                if (k.Down) { k.Down = false; Native.SendKey(k.Vk, true, k.Extended); }
            navUp.Down = navDown.Down = navLeft.Down = navRight.Down = false;
            if (aTyping) { aTyping = false; RaiseBool(KeyPressRequested, false); }
            scrollUp.Down = scrollDown.Down = false;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Tray icon, menu and on-screen ON/OFF indicator
    // ------------------------------------------------------------------------------------------
    sealed class TrayApp : ApplicationContext
    {
        static readonly Color OnColor = Color.FromArgb(108, 84, 214);
        static readonly Color OffColor = Color.FromArgb(130, 130, 130);

        readonly MouseEngine engine = new MouseEngine();
        readonly Osd osd = new Osd();
        readonly ControllerKeyboard keyboard = new ControllerKeyboard();
        readonly FocusWatcher watcher;
        readonly NotifyIcon tray;
        readonly Icon iconOn, iconOff, iconWaiting;
        readonly ToolStripMenuItem itemEnabled, itemStatus, itemStartup, itemAutoKeyboard;
        readonly List<ToolStripMenuItem> speedItems = new List<ToolStripMenuItem>();
        readonly List<ToolStripMenuItem> sizeItems = new List<ToolStripMenuItem>();
        bool autoKeyboard, ignoreNextClick;
        readonly System.Windows.Forms.Timer gameTimer = new System.Windows.Forms.Timer { Interval = 2000 };

        // Running any of these switches the controller over to the game as a virtual Xbox controller
        // and turns the mouse off. Extra names (one per line, without .exe) can go in games.txt next
        // to GameCubeMouse.exe.
        static readonly string[] PadGames = {
            "pcsx2-qt", "pcsx2", "duckstation-qt-x64-ReleaseLTCG", "duckstation-nogui-x64-ReleaseLTCG",
            "rpcs3", "PPSSPPWindows64", "retroarch", "Cemu", "xemu", "flycast", "Ryujinx", "xenia", "xenia_canary" };
        // These read the GameCube adapter themselves, so we just let go of it.
        static readonly string[] AdapterGames = { "Dolphin", "DolphinQt2", "Slippi Dolphin" };

        const string ControlsText =
            "Control stick:  move the cursor\n" +
            "A:  left click\n" +
            "B:  right click\n" +
            "Y:  Back\n" +
            "X:  Forward\n" +
            "Z:  open / close the on-screen keyboard\n" +
            "C-stick:  scroll\n" +
            "D-pad up / down:  scroll\n" +
            "D-pad left / right:  volume down / up\n" +
            "L (press in):  turbo, faster cursor\n" +
            "R (press in):  precision, slower cursor\n" +
            "L + R clicked all the way in:  middle click\n" +
            "Start:  turn the controller mouse on / off\n\n" +
            "KEYBOARD\n" +
            "It pops up by itself when you click into a text field, and moves out of the way of\n" +
            "what you're typing into. While it's open:\n" +
            "    D-pad = move the highlight,  A = type the highlighted key (hold to repeat)\n" +
            "    B = Backspace,  X = Space,  Y = Shift,  Z = close\n" +
            "Moving the stick hides the highlight so A clicks normally again; press the D-pad to bring it back.\n\n" +
            "Only the controller in port 1 is used.";

        public TrayApp()
        {
            IntPtr createHandle = osd.Handle;   // the reader thread posts UI updates through this window

            iconOn = MakeIcon(OnColor, true);
            iconOff = MakeIcon(OffColor, true);
            iconWaiting = MakeIcon(OffColor, false);
            engine.SpeedIndex = Settings.LoadInt("Speed", 1);
            keyboard.SizeIndex = Settings.LoadInt("KeyboardSize", 1);
            autoKeyboard = Settings.LoadInt("AutoKeyboard", 1) != 0;
            watcher = new FocusWatcher(result => Post(() => OnFocusResult(result)));

            itemEnabled = new ToolStripMenuItem("Controller mouse on   (Start)", null, delegate { engine.Enabled = !engine.Enabled; });
            itemEnabled.Font = new Font(itemEnabled.Font, FontStyle.Bold);
            itemStatus = new ToolStripMenuItem("Starting...") { Enabled = false };

            var speedMenu = new ToolStripMenuItem("Cursor speed");
            for (int i = 0; i < MouseEngine.SpeedNames.Length; i++)
            {
                int index = i;
                var item = new ToolStripMenuItem(MouseEngine.SpeedNames[i], null, delegate { SetSpeed(index); });
                speedItems.Add(item);
                speedMenu.DropDownItems.Add(item);
            }
            var keyboardMenu = new ToolStripMenuItem("On-screen keyboard");
            keyboardMenu.DropDownItems.Add(new ToolStripMenuItem("Show / hide   (Z)", null, delegate { ToggleKeyboard(); }));
            itemAutoKeyboard = new ToolStripMenuItem("Auto keyboard (text fields)", null, delegate
            {
                autoKeyboard = !autoKeyboard;
                Settings.SaveInt("AutoKeyboard", autoKeyboard ? 1 : 0);
                if (!autoKeyboard && keyboard.Visible && keyboard.AutoShown) keyboard.HideKeyboard(false);
                RefreshChecks();
            });
            itemAutoKeyboard.ToolTipText = "Automatically show the on-screen keyboard when you click a text field with the controller.";
            keyboardMenu.DropDownItems.Add(new ToolStripSeparator());
            for (int i = 0; i < ControllerKeyboard.SizeNames.Length; i++)
            {
                int index = i;
                var item = new ToolStripMenuItem("Size: " + ControllerKeyboard.SizeNames[i], null, delegate
                {
                    keyboard.SizeIndex = index;
                    Settings.SaveInt("KeyboardSize", index);
                    RefreshChecks();
                });
                sizeItems.Add(item);
                keyboardMenu.DropDownItems.Add(item);
            }
            var controls = new ToolStripMenuItem("Show controls...", null, delegate
            {
                MessageBox.Show(ControlsText, "GameCube Mouse controls", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
            var recenter = new ToolStripMenuItem("Re-center sticks", null, delegate
            {
                engine.Recenter();
                osd.Flash(engine.ControllerConnected ? "Sticks re-centered" : "No controller in port 1", OnColor);
            });
            itemStartup = new ToolStripMenuItem("Start with Windows", null, delegate
            {
                Settings.StartsWithWindows = !itemStartup.Checked;
                itemStartup.Checked = Settings.StartsWithWindows;
            });
            itemStartup.Checked = Settings.StartsWithWindows;
            var exit = new ToolStripMenuItem("Exit", null, delegate { ExitThread(); });

            var menu = new ContextMenuStrip();
            menu.Items.AddRange(new ToolStripItem[] {
                itemEnabled, itemStatus, new ToolStripSeparator(),
                speedMenu, keyboardMenu, itemAutoKeyboard, recenter, controls, itemStartup, new ToolStripSeparator(), exit });

            tray = new NotifyIcon { Icon = iconWaiting, Text = "GameCube Mouse", ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) engine.Enabled = !engine.Enabled; };

            engine.StatusChanged += () => Post(RefreshUi);
            engine.Toggled += on => Post(() =>
            {
                RefreshUi();
                osd.Flash(on ? "Controller mouse ON" : "Controller mouse OFF", on ? OnColor : OffColor);
            });
            engine.KeyboardRequested += () => Post(ToggleKeyboard);
            engine.ShiftRequested += () => Post(keyboard.ToggleShift);
            engine.LeftClicked += () => Post(OnControllerClick);
            engine.NavRequested += dir => Post(() => keyboard.Navigate(dir));
            engine.KeyPressRequested += down => Post(() => keyboard.PressFocused(down));
            engine.NavModeChanged += on => Post(() => keyboard.ShowFocus = on);
            keyboard.VisibleChanged += delegate { engine.KeyboardMode = keyboard.Visible; };
            // Closed by clicking Enter/Hide with the cursor: that click's release must not pop it back open.
            keyboard.ClosedByUser += () => { ignoreNextClick = engine.LeftHeld; };
            keyboard.KeyTyped += navigation =>
            {
                // after Enter/Tab/Esc focus may have moved on; after ordinary typing the caret may have moved under the keyboard
                watcher.Request(navigation ? FocusWatcher.Reason.Navigated : FocusWatcher.Reason.Typed);
            };

            gameTimer.Tick += delegate { CheckGames(); };
            CheckGames();
            gameTimer.Start();

            RefreshChecks();
            RefreshUi();
            engine.Start();
            osd.Flash("GameCube Mouse is running  -  Start toggles it", OnColor);
        }

        string currentGame;

        void CheckGames()
        {
            string game = null;
            GameMode mode = GameMode.None;
            foreach (string name in AdapterGames)
                if (IsRunning(name)) { game = name; mode = GameMode.Released; break; }
            if (game == null)
            {
                var names = new List<string>(PadGames);
                try
                {
                    string extra = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "games.txt");
                    if (File.Exists(extra))
                        foreach (string line in File.ReadAllLines(extra))
                        {
                            string n = line.Trim();
                            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
                            if (n.Length > 0 && !n.StartsWith("#")) names.Add(n);
                        }
                }
                catch { }
                foreach (string name in names)
                    if (IsRunning(name)) { game = name; mode = GameMode.VirtualPad; break; }
            }
            if (mode == engine.GameMode && game == currentGame) return;
            bool was = engine.GameMode != GameMode.None;
            currentGame = game;
            engine.GameMode = mode;
            if (keyboard.Visible && mode != GameMode.None) keyboard.HideKeyboard(false);
            RefreshUi();
            if (mode != GameMode.None && !was) osd.Flash("Game mode  -  controller mouse off", OffColor);
            else if (mode == GameMode.None && was) osd.Flash("Game closed  -  controller mouse back", OnColor);
        }

        static bool IsRunning(string name)
        {
            Process[] list = null;
            try { list = Process.GetProcessesByName(name); return list.Length > 0; }
            catch { return false; }
            finally { if (list != null) foreach (var p in list) p.Dispose(); }
        }

        void Post(Action action)
        {
            try { if (!osd.IsDisposed) osd.BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        void SetSpeed(int index)
        {
            engine.SpeedIndex = index;
            Settings.SaveInt("Speed", index);
            RefreshChecks();
        }

        void ToggleKeyboard()
        {
            if (keyboard.Visible) { keyboard.HideKeyboard(true); return; }
            keyboard.ShowKeyboard(false);
            watcher.Request(FocusWatcher.Reason.Manual);   // then step out of the way of whatever has focus
        }

        void OnControllerClick()
        {
            if (ignoreNextClick) { ignoreNextClick = false; return; }   // the click that pressed Enter / Hide
            if (keyboard.Visible && keyboard.Bounds.Contains(Cursor.Position)) return;   // typing on the keyboard itself
            if (!autoKeyboard && !keyboard.Visible) return;
            watcher.Request(FocusWatcher.Reason.Click);
        }

        void OnFocusResult(FocusWatcher.Result r)
        {
            if (r.Ignore) return;
            switch (r.Reason)
            {
                case FocusWatcher.Reason.Click:
                    if (r.Editable)
                    {
                        if (keyboard.Visible) { keyboard.PlaceFor(r.HasTarget, r.Target); return; }
                        if (!autoKeyboard) return;
                        keyboard.PlaceFor(r.HasTarget, r.Target);
                        keyboard.ShowKeyboard(true);
                    }
                    else if (keyboard.Visible && keyboard.AutoShown) keyboard.HideKeyboard(false);
                    break;
                case FocusWatcher.Reason.Navigated:
                    if (!keyboard.Visible) return;
                    if (r.Editable) keyboard.PlaceFor(r.HasTarget, r.Target);
                    else if (keyboard.AutoShown) keyboard.HideKeyboard(false);
                    break;
                default:
                    if (keyboard.Visible) keyboard.PlaceFor(r.HasTarget, r.Target);
                    break;
            }
        }

        void RefreshChecks()
        {
            for (int i = 0; i < speedItems.Count; i++) speedItems[i].Checked = i == engine.SpeedIndex;
            for (int i = 0; i < sizeItems.Count; i++) sizeItems[i].Checked = i == keyboard.SizeIndex;
            itemAutoKeyboard.Checked = autoKeyboard;
        }

        void RefreshUi()
        {
            bool ready = engine.ControllerConnected;
            bool gaming = engine.GameMode != GameMode.None;
            tray.Icon = !ready ? iconWaiting : (engine.Enabled && !gaming) ? iconOn : iconOff;
            string state = gaming ? "game mode (" + currentGame + ")"
                         : !ready ? engine.Status : engine.Enabled ? "ON (Start to turn off)" : "OFF (Start to turn on)";
            string tip = "GameCube Mouse: " + state;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            itemEnabled.Checked = engine.Enabled;
            itemStatus.Text = engine.Status;
        }

        protected override void ExitThreadCore()
        {
            gameTimer.Stop();
            gameTimer.Dispose();
            engine.Stop();
            watcher.Dispose();
            tray.Visible = false;
            tray.Dispose();
            iconOn.Dispose();
            iconOff.Dispose();
            iconWaiting.Dispose();
            keyboard.Dispose();
            osd.Dispose();
            base.ExitThreadCore();
        }

        static Icon MakeIcon(Color color, bool filled)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    // Compact GameCube silhouette, with its offset sticks and distinctive face buttons.
                    using (var shell = new GraphicsPath())
                    {
                        shell.AddBezier(3, 10, 5, 5, 10, 6, 12, 8);
                        shell.AddBezier(12, 8, 15, 9, 17, 9, 20, 8);
                        shell.AddBezier(20, 8, 24, 5, 29, 7, 30, 12);
                        shell.AddBezier(30, 12, 32, 20, 31, 27, 27, 26);
                        shell.AddBezier(27, 26, 24, 25, 23, 21, 21, 21);
                        shell.AddLine(21, 21, 11, 21);
                        shell.AddBezier(11, 21, 8, 23, 7, 28, 3, 26);
                        shell.AddBezier(3, 26, 0, 25, 1, 16, 3, 10);
                        shell.CloseFigure();
                        using (var b = new SolidBrush(filled ? color : Color.FromArgb(65, color))) g.FillPath(b, shell);
                        using (var p = new Pen(Color.FromArgb(222, 217, 244), 1.2f)) g.DrawPath(p, shell);
                    }
                    using (var b = new SolidBrush(Color.FromArgb(234, 234, 241))) g.FillEllipse(b, 5, 9, 7, 7);
                    using (var b = new SolidBrush(Color.FromArgb(80, 77, 98))) g.FillEllipse(b, 7, 11, 3, 3);
                    using (var p = new Pen(Color.FromArgb(224, 224, 235), 2f))
                    { g.DrawLine(p, 10, 18, 10, 23); g.DrawLine(p, 7.5f, 20.5f, 12.5f, 20.5f); }
                    using (var b = new SolidBrush(filled ? Color.FromArgb(75, 215, 139) : Color.Silver)) g.FillEllipse(b, 22, 11, 6, 6);
                    using (var b = new SolidBrush(filled ? Color.FromArgb(242, 92, 92) : Color.Silver)) g.FillEllipse(b, 18.5f, 15, 3, 3);
                    using (var b = new SolidBrush(Color.FromArgb(222, 222, 235)))
                    { g.FillEllipse(b, 26, 8, 4, 2); g.FillEllipse(b, 19, 8, 2, 4); g.FillEllipse(b, 14.5f, 12, 3, 2); }
                    using (var b = new SolidBrush(filled ? Color.FromArgb(251, 208, 65) : Color.Silver)) g.FillEllipse(b, 18, 19, 5, 5);
                }
                IntPtr handle = bmp.GetHicon();
                try { using (var icon = Icon.FromHandle(handle)) return (Icon)icon.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr icon);
    }

    // Small click-through banner that never takes focus away from what you're doing.
    sealed class Osd : Form
    {
        readonly System.Windows.Forms.Timer hideTimer = new System.Windows.Forms.Timer { Interval = 1400 };
        string message = "";
        Color dot = Color.Gray;

        public Osd()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(30, 30, 34);
            ForeColor = Color.White;
            Font = new Font("Segoe UI Semibold", 11f);
            DoubleBuffered = true;
            Opacity = 0.95;
            Size = new Size(Ui.S(240), Ui.S(44));
            hideTimer.Tick += delegate { hideTimer.Stop(); Hide(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; Native.DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }   // Windows 11 rounded corners
        }

        public void Flash(string text, Color accent)
        {
            message = text;
            dot = accent;
            int width = TextRenderer.MeasureText(text, Font).Width + Ui.S(58), height = Ui.S(44);
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            SetBounds(area.Left + (area.Width - width) / 2, area.Bottom - height - Ui.S(48), width, height);
            Invalidate();
            if (!Visible) Show();
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            hideTimer.Stop();
            hideTimer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(dot)) e.Graphics.FillEllipse(b, Ui.S(18), Height / 2 - Ui.S(6), Ui.S(12), Ui.S(12));
            TextRenderer.DrawText(e.Graphics, message, Font, new Rectangle(Ui.S(40), 0, Width - Ui.S(40), Height), ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) hideTimer.Dispose();
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------------------------------
    // Finds the text field that has keyboard focus (in any app) and where it is on screen.
    // UI Automation can be slow or hang on a busy app, so it runs on its own thread and never
    // blocks the UI; results are handed back through a callback.
    // ------------------------------------------------------------------------------------------
    sealed class FocusWatcher : IDisposable
    {
        public enum Reason { Click, Manual, Typed, Navigated }

        public sealed class Result
        {
            public Reason Reason;
            public bool Editable, HasTarget, Ignore;
            public Rectangle Target;    // the field (or the caret, inside big documents) to keep uncovered
        }

        readonly Action<Result> deliver;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly object gate = new object();
        readonly int ownPid = Process.GetCurrentProcess().Id;
        readonly Thread thread;
        Reason? pending;
        volatile bool running = true;

        public FocusWatcher(Action<Result> deliver)
        {
            this.deliver = deliver;
            thread = new Thread(Run) { IsBackground = true, Name = "Focus watcher" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void Request(Reason reason)
        {
            lock (gate)
                if (pending == null || pending.Value != Reason.Click) pending = reason;   // a click check wins
            wake.Set();
        }

        public void Dispose()
        {
            running = false;
            wake.Set();
            thread.Join(300);
        }

        void Run()
        {
            try { var warm = AutomationElement.FocusedElement; } catch { }   // first UIA call is slow; pay it now
            while (running)
            {
                Reason r;
                lock (gate)
                {
                    if (pending == null) r = (Reason)(-1);
                    else { r = pending.Value; pending = null; }
                }
                if ((int)r < 0) { wake.WaitOne(); continue; }
                try { Handle(r); } catch { }
            }
        }

        // Returns true if the wait was cut short by a newer request (only non-click checks get superseded).
        bool Wait(int ms, Reason current)
        {
            if (ms <= 0) return false;
            if (current == Reason.Click) { Thread.Sleep(ms); return !running; }
            var sw = Stopwatch.StartNew();
            while (running)
            {
                int left = ms - (int)sw.ElapsedMilliseconds;
                if (left <= 0) return false;
                if (!wake.WaitOne(left)) return !running;
                lock (gate) if (pending != null) return true;   // a newer request: drop this one, the loop picks it up
            }
            return true;
        }

        void Handle(Reason r)
        {
            int delay = r == Reason.Click ? 150 : r == Reason.Navigated ? 350 : r == Reason.Typed ? 150 : 30;
            if (Wait(delay, r)) return;
            Result res = Inspect(r);
            if (r == Reason.Click && !res.Editable && !res.Ignore)
            {
                Thread.Sleep(300);    // some apps (browsers) move focus a little late
                if (!running) return;
                res = Inspect(r);
            }
            if (running) deliver(res);
        }

        Result Inspect(Reason r)
        {
            var res = new Result { Reason = r };
            Rectangle caret;
            bool hasCaret = Win32Caret(out caret);
            Rectangle element = Rectangle.Empty;
            bool known = false, editable = false;
            try
            {
                AutomationElement el = AutomationElement.FocusedElement;
                if (el != null)
                {
                    if (el.Current.ProcessId == ownPid) { res.Ignore = true; return res; }
                    known = true;
                    editable = IsEditable(el, el.Current.ControlType, hasCaret);
                    element = ToRect(el.Current.BoundingRectangle);
                    if (editable && !hasCaret) hasCaret = SelectionRect(el, out caret);
                }
            }
            catch { }
            res.Editable = known ? editable : hasCaret;

            Rectangle anchor = !element.IsEmpty ? element : caret;
            if (anchor.IsEmpty) return res;
            Rectangle area = Screen.FromRectangle(anchor).WorkingArea;
            if (!element.IsEmpty && element.Height <= area.Height * 0.4)
            {
                res.Target = hasCaret && !caret.IsEmpty ? Rectangle.Union(element, caret) : element;
                res.HasTarget = true;
            }
            else if (hasCaret && !caret.IsEmpty)
            {
                res.Target = Rectangle.Inflate(caret, Ui.S(20), Ui.S(40));   // a few lines around the caret
                res.HasTarget = true;
            }
            return res;
        }

        static bool IsEditable(AutomationElement el, ControlType ct, bool hasCaret)
        {
            object p;
            bool hasValue = el.TryGetCurrentPattern(ValuePattern.Pattern, out p);
            bool writable = hasValue && !((ValuePattern)p).Current.IsReadOnly;
            if (ct == ControlType.Edit) return !hasValue || writable;
            if (ct == ControlType.Document)
            {
                if (hasValue) return writable;
                return hasCaret && el.TryGetCurrentPattern(TextPattern.Pattern, out p);   // Word, WordPad...
            }
            if (ct == ControlType.ComboBox || ct == ControlType.Group || ct == ControlType.Custom || ct == ControlType.Pane)
                return writable;
            return false;
        }

        static bool SelectionRect(AutomationElement el, out Rectangle rect)
        {
            rect = Rectangle.Empty;
            object p;
            if (!el.TryGetCurrentPattern(TextPattern.Pattern, out p)) return false;
            TextPatternRange[] sel = ((TextPattern)p).GetSelection();
            if (sel == null || sel.Length == 0) return false;
            System.Windows.Rect[] rects = sel[0].GetBoundingRectangles();
            if (rects == null || rects.Length == 0)
            {
                TextPatternRange one = sel[0].Clone();
                one.ExpandToEnclosingUnit(TextUnit.Character);
                rects = one.GetBoundingRectangles();
            }
            if (rects == null) return false;
            foreach (System.Windows.Rect wr in rects)
            {
                Rectangle r = ToRect(wr);
                if (r.IsEmpty) continue;
                rect = rect.IsEmpty ? r : Rectangle.Union(rect, r);
            }
            return !rect.IsEmpty;
        }

        static bool Win32Caret(out Rectangle rect)
        {
            rect = Rectangle.Empty;
            var info = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
            if (!Native.GetGUIThreadInfo(0, ref info) || info.hwndCaret == IntPtr.Zero) return false;
            int h = info.rcCaret.Bottom - info.rcCaret.Top;
            if (h <= 0) return false;
            var pt = new Native.POINT { X = info.rcCaret.Left, Y = info.rcCaret.Top };
            if (!Native.ClientToScreen(info.hwndCaret, ref pt)) return false;
            rect = new Rectangle(pt.X, pt.Y, Math.Max(2, info.rcCaret.Right - info.rcCaret.Left), h);
            return true;
        }

        static Rectangle ToRect(System.Windows.Rect r)
        {
            if (r.IsEmpty || double.IsInfinity(r.Width) || double.IsInfinity(r.Height) || r.Width <= 0 || r.Height <= 0)
                return Rectangle.Empty;
            return new Rectangle((int)r.X, (int)r.Y, (int)Math.Ceiling(r.Width), (int)Math.Ceiling(r.Height));
        }
    }

    // On-screen keyboard made for the controller: point at a key with the stick and press A.
    // It never takes focus, so every key goes to the window you were already typing in.
    sealed class ControllerKeyboard : Form
    {
        enum Kind { Char, Special, Combo, Shift, Caps, Ctrl, Hide }

        sealed class Key
        {
            public Kind Kind;
            public string Normal, Shifted;
            public ushort Vk;
            public bool Extended;
            public float Units = 1, X;
            public int Row;
            public Rectangle Bounds;
        }

        public static readonly string[] SizeNames = { "Small", "Medium", "Large" };
        static readonly float[] SizeFactors = { 0.8f, 1f, 1.25f };

        const float Unit = 50, Gap = 6, Pad = 10, TitleHeight = 30;   // sizes at 100% scaling, Medium
        const int Rows = 6;
        static readonly Color CharColor = Color.FromArgb(58, 58, 66), SpecialColor = Color.FromArgb(42, 42, 50);
        static readonly Color ActionColor = Color.FromArgb(36, 36, 44);
        static readonly Color HoverColor = Color.FromArgb(92, 92, 106), Accent = Color.FromArgb(108, 84, 214);

        readonly List<Key> keys = new List<Key>();
        readonly System.Windows.Forms.Timer repeatTimer = new System.Windows.Forms.Timer();
        Font charFont, labelFont, titleFont;
        float factor = 1;
        int sizeIndex = 1;
        Key hover, pressed, homeKey, focus;
        bool shift, caps, ctrl, placed, autoShown, showFocus;
        float wantX = -1;                // remembered column for up/down, so zig-zagging through rows stays straight

        public event Action<bool> KeyTyped;   // true for Enter / Tab / Esc (focus may move on)

        public bool AutoShown { get { return autoShown; } }

        public ControllerKeyboard()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(24, 24, 28);
            DoubleBuffered = true;

            Row(0, Special("Esc", 0x1B, 2f, false), Combo("Select all", 'A', 2f), Combo("Cut", 'X', 1.5f),
                Combo("Copy", 'C', 1.5f), Combo("Paste", 'V', 1.5f), Combo("Undo", 'Z', 1.5f),
                Special("Home", 0x24, 1.5f, true), Special("End", 0x23, 1.5f, true), Special("Del", 0x2E, 2f, true));
            Row(1, Chars("`1234567890-=", "~!@#$%^&*()_+"), Special("Backspace", 0x08, 2f, false));
            Row(2, Special("Tab", 0x09, 1.5f, false), Chars("qwertyuiop[]", "QWERTYUIOP{}"), Wide("\\", "|", 1.5f));
            Row(3, Modifier(Kind.Caps, "Caps", 1.75f), Chars("asdfghjkl;'", "ASDFGHJKL:\""), Special("Enter", 0x0D, 2.25f, false));
            Row(4, Modifier(Kind.Shift, "Shift", 2.25f), Chars("zxcvbnm,./", "ZXCVBNM<>?"), Modifier(Kind.Shift, "Shift", 2.75f));
            Row(5, Modifier(Kind.Ctrl, "Ctrl", 2f), Special("Space", 0x20, 7f, false), Arrow("←", 0x25), Arrow("↑", 0x26),
                Arrow("↓", 0x28), Arrow("→", 0x27), Modifier(Kind.Hide, "Hide", 2f));
            foreach (Key k in keys) if (k.Kind == Kind.Char && k.Normal == "g") homeKey = k;

            ApplyLayout(1);
            repeatTimer.Tick += delegate { RepeatKey(); };
        }

        // ---- layout ----

        int S(float v) { return Ui.S(v * factor); }

        public int SizeIndex
        {
            get { return sizeIndex; }
            set
            {
                sizeIndex = Math.Max(0, Math.Min(SizeNames.Length - 1, value));
                Point oldCenter = new Point(Left + Width / 2, Bottom);
                ApplyLayout(SizeFactors[sizeIndex]);
                if (placed)
                {
                    Rectangle area = Screen.FromPoint(oldCenter).WorkingArea;
                    MoveTo(Clamp(oldCenter.X - Width / 2, area.Left, area.Right - Width),
                           Clamp(oldCenter.Y - Height, area.Top, area.Bottom - Height));
                }
            }
        }

        void ApplyLayout(float f)
        {
            factor = f;
            if (charFont != null) { charFont.Dispose(); labelFont.Dispose(); titleFont.Dispose(); }
            charFont = new Font("Segoe UI", 13f * f);
            labelFont = new Font("Segoe UI", 9.5f * f);
            titleFont = new Font("Segoe UI", 8.5f * Math.Max(f, 0.9f));
            foreach (Key k in keys)
            {
                int left = S(Pad + k.X * Unit), top = S(TitleHeight + k.Row * Unit);
                k.Bounds = new Rectangle(left, top, S(Pad + (k.X + k.Units) * Unit - Gap) - left, S(Unit - Gap));
            }
            ClientSize = new Size(S(Pad * 2 + 15 * Unit - Gap), S(TitleHeight + Rows * Unit - Gap + Pad));
            Invalidate();
        }

        void Row(int row, params IEnumerable<Key>[] groups)
        {
            float x = 0;
            foreach (IEnumerable<Key> group in groups)
                foreach (Key k in group)
                {
                    k.Row = row; k.X = x;
                    keys.Add(k);
                    x += k.Units;
                }
        }

        static Key[] Chars(string normal, string shifted)
        {
            var list = new Key[normal.Length];
            for (int i = 0; i < normal.Length; i++)
                list[i] = new Key { Kind = Kind.Char, Normal = normal[i].ToString(), Shifted = shifted[i].ToString() };
            return list;
        }
        static Key[] Wide(string normal, string shifted, float units) { return new[] { new Key { Kind = Kind.Char, Normal = normal, Shifted = shifted, Units = units } }; }
        static Key[] Special(string label, ushort vk, float units, bool ext) { return new[] { new Key { Kind = Kind.Special, Normal = label, Vk = vk, Units = units, Extended = ext } }; }
        static Key[] Arrow(string label, ushort vk) { return new[] { new Key { Kind = Kind.Special, Normal = label, Vk = vk, Extended = true } }; }
        static Key[] Combo(string label, char letter, float units) { return new[] { new Key { Kind = Kind.Combo, Normal = label, Vk = letter, Units = units } }; }
        static Key[] Modifier(Kind kind, string label, float units) { return new[] { new Key { Kind = kind, Normal = label, Units = units } }; }

        // ---- showing, hiding and placement ----

        // auto = popped up because a text field was clicked (it then also goes away by itself).
        public void ShowKeyboard(bool auto)
        {
            if (Visible) return;
            autoShown = auto;
            if (!placed) PlaceDefault(Screen.FromPoint(Cursor.Position).WorkingArea);
            if (focus == null) focus = homeKey;
            showFocus = true;
            Show();
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public event Action ClosedByUser;    // Enter, Hide or Z (not the automatic close)

        public void HideKeyboard(bool byUser)
        {
            if (!Visible) return;
            if (byUser) { var h = ClosedByUser; if (h != null) h(); }
            repeatTimer.Stop();
            pressed = hover = null;
            shift = ctrl = false;
            autoShown = false;
            Hide();
        }

        public void ToggleShift() { if (Visible) { shift = !shift; Invalidate(); } }

        // ---- D-pad navigation ----

        public bool ShowFocus
        {
            get { return showFocus; }
            set
            {
                if (focus == null) focus = homeKey;
                if (showFocus != value) { showFocus = value; Invalidate(); }
            }
        }

        static float CenterX(Key k) { return k.X + k.Units / 2; }

        // dir: 0 up, 1 down, 2 left, 3 right. Rows wrap around at the edges.
        public void Navigate(int dir)
        {
            if (!Visible) return;
            if (focus == null) focus = homeKey;
            showFocus = true;
            if (dir >= 2)
            {
                var row = keys.FindAll(k => k.Row == focus.Row);
                int i = row.IndexOf(focus) + (dir == 3 ? 1 : -1);
                focus = row[(i + row.Count) % row.Count];
                wantX = CenterX(focus);
            }
            else
            {
                if (wantX < 0) wantX = CenterX(focus);
                int targetRow = (focus.Row + (dir == 1 ? 1 : -1) + Rows) % Rows;
                Key best = null;
                foreach (Key k in keys)
                    if (k.Row == targetRow && (best == null || Math.Abs(CenterX(k) - wantX) < Math.Abs(CenterX(best) - wantX)))
                        best = k;
                focus = best;
            }
            Invalidate();
        }

        // A on the highlighted key: press now, keep repeating while held (like holding a real key).
        public void PressFocused(bool down)
        {
            if (!down)
            {
                if (pressed != null) { repeatTimer.Stop(); pressed = null; Invalidate(); }
                return;
            }
            if (!Visible || focus == null) return;
            showFocus = true;
            Key k = focus;
            pressed = k;
            Press(k);
            if (Visible && (k.Kind == Kind.Char || k.Kind == Kind.Special || k.Kind == Kind.Combo)) { repeatTimer.Interval = 450; repeatTimer.Start(); }
            else pressed = Visible ? k : null;
            Invalidate();
        }

        void PlaceDefault(Rectangle area)
        {
            MoveTo(area.Left + (area.Width - Width) / 2, area.Bottom - Height - S(16));
            placed = true;
        }

        // Keeps the keyboard off the field you're typing into. Leaves it alone if it isn't in the way
        // (so a spot you dragged it to sticks); otherwise moves it below, above, or to the far side.
        public void PlaceFor(bool hasTarget, Rectangle target)
        {
            if (!hasTarget) { if (!placed) PlaceDefault(Screen.FromPoint(Cursor.Position).WorkingArea); return; }
            Rectangle area = Screen.FromRectangle(target).WorkingArea;
            int m = S(12);
            Rectangle avoid = Rectangle.Inflate(target, m, m);
            if (placed && area.Contains(Bounds) && !Bounds.IntersectsWith(avoid)) return;

            int x = Clamp(area.Left + (area.Width - Width) / 2, area.Left, area.Right - Width);
            int y;
            if (area.Bottom - avoid.Bottom >= Height + m) y = area.Bottom - Height - m;     // room below: bottom of the screen
            else if (avoid.Top - area.Top >= Height) y = avoid.Top - Height;               // room above: just above the field
            else
            {
                // not enough room either way: the side with more space, and slide sideways if that clears it
                y = (area.Bottom - target.Bottom) > (target.Top - area.Top) ? area.Bottom - Height - m : area.Top + m;
                if (area.Right - avoid.Right >= Width) x = area.Right - Width;
                else if (avoid.Left - area.Left >= Width) x = area.Left;
            }
            MoveTo(x, y);
            placed = true;
        }

        // Moves the window; if the cursor is on the keyboard it moves along, so you don't lose your place.
        void MoveTo(int x, int y)
        {
            int dx = x - Left, dy = y - Top;
            if (dx == 0 && dy == 0) return;
            bool carry = Visible && Bounds.Contains(Cursor.Position);
            Location = new Point(x, y);
            if (carry) Cursor.Position = new Point(Cursor.Position.X + dx, Cursor.Position.Y + dy);
        }

        static int Clamp(int v, int lo, int hi) { return hi < lo ? lo : Math.Max(lo, Math.Min(hi, v)); }

        // ---- typing ----

        string Label(Key k)
        {
            if (k.Kind != Kind.Char) return k.Normal;
            bool upper = char.IsLetter(k.Normal[0]) ? shift ^ caps : shift;
            return upper ? k.Shifted : k.Normal;
        }

        void Press(Key k)
        {
            bool typed = true, navigation = false;
            switch (k.Kind)
            {
                case Kind.Shift: shift = !shift; typed = false; break;
                case Kind.Caps: caps = !caps; typed = false; break;
                case Kind.Ctrl: ctrl = !ctrl; typed = false; break;
                case Kind.Hide: HideKeyboard(true); return;
                case Kind.Special:
                    SendWithModifiers(k.Vk, k.Extended);
                    shift = ctrl = false;
                    if (k.Vk == 0x0D) { HideKeyboard(true); return; }   // Enter = done typing
                    navigation = k.Vk == 0x09 || k.Vk == 0x1B;
                    break;
                case Kind.Combo:
                    ctrl = true;
                    SendWithModifiers(k.Vk, false);
                    shift = ctrl = false;
                    break;
                case Kind.Char:
                    if (ctrl) SendWithModifiers(VkFor(k.Normal[0]), false);   // Ctrl+C, Ctrl+V... need the real key
                    else Native.SendUnicode(Label(k)[0]);                     // types exactly the character shown
                    shift = ctrl = false;
                    break;
            }
            Invalidate();
            if (typed) { var h = KeyTyped; if (h != null) h(navigation); }
        }

        static ushort VkFor(char c)
        {
            if (c < 128 && char.IsLetterOrDigit(c)) return (ushort)char.ToUpperInvariant(c);
            return (ushort)(Native.VkKeyScan(c) & 0xFF);
        }

        void SendWithModifiers(ushort vk, bool extended)
        {
            if (ctrl) Native.SendKey(Native.VK_LCONTROL, false, false);
            if (shift) Native.SendKey(Native.VK_LSHIFT, false, false);
            Native.SendKey(vk, false, extended);
            Native.SendKey(vk, true, extended);
            if (shift) Native.SendKey(Native.VK_LSHIFT, true, false);
            if (ctrl) Native.SendKey(Native.VK_LCONTROL, true, false);
        }

        void RepeatKey()
        {
            if (pressed == null || !Visible) { repeatTimer.Stop(); return; }
            repeatTimer.Interval = 60;
            Press(pressed);
        }

        Key KeyAt(Point p)
        {
            foreach (Key k in keys) if (k.Bounds.Contains(p)) return k;
            return null;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Key k = KeyAt(e.Location);
            if (k == null) return;
            pressed = k;
            Press(k);
            if (Visible && (k.Kind == Kind.Char || k.Kind == Kind.Special)) { repeatTimer.Interval = 450; repeatTimer.Start(); }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            repeatTimer.Stop();
            pressed = null;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Key k = KeyAt(e.Location);
            if (k != hover) { hover = k; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != null) { hover = null; Invalidate(); }
        }

        // ---- window plumbing ----

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3, WM_NCHITTEST = 0x84, HTCLIENT = 1, HTCAPTION = 2;
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }   // clicks never steal focus
            base.WndProc(ref m);
            if (m.Msg == WM_NCHITTEST && m.Result == (IntPtr)HTCLIENT)
            {
                int lp = unchecked((int)(long)m.LParam);
                Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                if (p.Y < S(TitleHeight - Gap)) { m.Result = (IntPtr)HTCAPTION; placed = true; }   // drag the top bar to move
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; Native.DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            TextRenderer.DrawText(g, "D-pad pick  ·  A type  ·  B backspace  ·  X space  ·  Y shift  ·  Z close", titleFont,
                new Rectangle(0, 0, ClientSize.Width, S(TitleHeight - Gap)), Color.FromArgb(150, 150, 162),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            int radius = S(6);
            foreach (Key k in keys)
            {
                bool latched = (k.Kind == Kind.Shift && shift) || (k.Kind == Kind.Caps && caps) || (k.Kind == Kind.Ctrl && ctrl);
                Color idle = k.Kind == Kind.Char ? CharColor : k.Row == 0 ? ActionColor : SpecialColor;
                Color fill = k == pressed || latched ? Accent : k == hover ? HoverColor : idle;
                bool focused = showFocus && k == focus;
                if (focused && fill != Accent) fill = HoverColor;
                using (GraphicsPath path = Ui.RoundedRect(k.Bounds, radius))
                using (var b = new SolidBrush(fill))
                {
                    g.FillPath(b, path);
                    if (focused)
                        using (var pen = new Pen(Color.White, Math.Max(2f, S(3))))
                            g.DrawPath(pen, path);
                }
                TextRenderer.DrawText(g, Label(k), k.Kind == Kind.Char ? charFont : labelFont, k.Bounds,
                    k.Row == 0 ? Color.FromArgb(205, 205, 215) : Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                repeatTimer.Dispose();
                charFont.Dispose(); labelFont.Dispose(); titleFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    static class Ui
    {
        public static readonly float Scale = ReadScale();

        static float ReadScale()
        {
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return Math.Max(1f, g.DpiX / 96f); }
            catch { return 1f; }
        }

        public static int S(float v) { return (int)Math.Round(v * Scale); }

        public static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Settings (HKCU only)
    // ------------------------------------------------------------------------------------------
    static class Settings
    {
        const string KeyPath = @"Software\GameCubeMouse";
        const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "GameCubeMouse";

        public static int LoadInt(string name, int fallback)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    object v = k == null ? null : k.GetValue(name);
                    if (v is int) return (int)v;
                }
            }
            catch { }
            return fallback;
        }

        public static void SaveInt(string name, int value)
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(KeyPath)) k.SetValue(name, value, RegistryValueKind.DWord); }
            catch { }
        }

        static string RunCommand { get { return "\"" + Application.ExecutablePath + "\""; } }

        public static bool StartsWithWindows
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(RunPath))
                        return k != null && string.Equals(k.GetValue(RunName) as string, RunCommand, StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }
            set
            {
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(RunPath))
                    {
                        if (value) k.SetValue(RunName, RunCommand);
                        else k.DeleteValue(RunName, false);
                    }
                }
                catch { }
            }
        }
    }

    // ------------------------------------------------------------------------------------------
    // Diagnostics: GameCubeMouse.exe --probe out.txt
    // ------------------------------------------------------------------------------------------
    static class Probe
    {
        public static int Run(string outPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Device paths:");
            foreach (string p in GCAdapter.FindPaths()) sb.AppendLine("  " + p);

            string error;
            using (GCAdapter adapter = GCAdapter.Open(out error))
            {
                if (adapter == null)
                {
                    sb.AppendLine("OPEN FAILED: " + error);
                    File.WriteAllText(outPath, sb.ToString());
                    return 1;
                }
                sb.AppendLine("Opened OK (" + adapter.Describe() + ")");

                var buf = new byte[GCAdapter.ReportLength];
                byte[] last = null;
                int reports = 0, timeouts = 0, other = 0;
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 2000)
                {
                    int n = adapter.Read(buf);
                    if (n < 0) { sb.AppendLine("READ ERROR " + adapter.LastError); break; }
                    if (n == 0) { timeouts++; continue; }
                    if (n >= GCAdapter.ReportLength && buf[0] == 0x21) { reports++; last = (byte[])buf.Clone(); }
                    else other++;
                }
                sb.AppendLine(string.Format("Reports: {0} in {1:0.00}s ({2:0} Hz), timeouts: {3}, other packets: {4}",
                    reports, sw.Elapsed.TotalSeconds, reports / sw.Elapsed.TotalSeconds, timeouts, other));
                if (last != null)
                {
                    for (int port = 0; port < 4; port++)
                    {
                        int o = 1 + port * 9;
                        int type = last[o] >> 4;
                        sb.AppendLine(string.Format("Port {0}: status=0x{1:X2} ({2}) buttons={3:X2} {4:X2} stick=({5},{6}) cstick=({7},{8}) L={9} R={10}",
                            port + 1, last[o], type == 1 ? "wired" : type == 2 ? "wavebird" : "empty",
                            last[o + 1], last[o + 2], last[o + 3], last[o + 4], last[o + 5], last[o + 6], last[o + 7], last[o + 8]));
                    }
                }
            }
            File.WriteAllText(outPath, sb.ToString());
            return 0;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Win32 / WinUSB interop
    // ------------------------------------------------------------------------------------------
    static class Native
    {
        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x80, FILE_FLAG_OVERLAPPED = 0x40000000;
        public const uint PIPE_TRANSFER_TIMEOUT = 0x03;
        public const int ERROR_SEM_TIMEOUT = 121;

        public const uint INPUT_MOUSE = 0;
        public const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
        public const uint MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100;
        public const uint MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000;
        public const int XBUTTON1 = 1, XBUTTON2 = 2;          // Back, Forward

        public const uint INPUT_KEYBOARD = 1, KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;
        public const ushort VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;
        public const ushort VK_LSHIFT = 0xA0, VK_LCONTROL = 0xA2, VK_BACK = 0x08, VK_SPACE = 0x20;
        public const ushort VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF, VK_MEDIA_PLAY_PAUSE = 0xB3;

        public const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct USB_INTERFACE_DESCRIPTOR
        {
            public byte bLength, bDescriptorType, bInterfaceNumber, bAlternateSetting, bNumEndpoints;
            public byte bInterfaceClass, bInterfaceSubClass, bInterfaceProtocol, iInterface;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WINUSB_PIPE_INFORMATION { public int PipeType; public byte PipeId; public ushort MaximumPacketSize; public byte Interval; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public InputUnion u; }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, out uint required, IntPtr devInfo);
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, uint inSize,
            byte[] outBuffer, uint outSize, out uint returned, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_Initialize(SafeFileHandle device, out IntPtr handle);
        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_Free(IntPtr handle);
        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_QueryInterfaceSettings(IntPtr handle, byte altSetting, out USB_INTERFACE_DESCRIPTOR desc);
        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_QueryPipe(IntPtr handle, byte altSetting, byte pipeIndex, out WINUSB_PIPE_INFORMATION info);
        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_SetPipePolicy(IntPtr handle, byte pipeId, uint policy, uint length, ref uint value);
        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_ReadPipe(IntPtr handle, byte pipeId, byte[] buffer, uint length, out uint transferred, IntPtr overlapped);
        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_WritePipe(IntPtr handle, byte pipeId, byte[] buffer, uint length, out uint transferred, IntPtr overlapped);

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("user32.dll")]
        static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern short VkKeyScan(char ch);
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public int cbSize, flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public RECT rcCaret;
        }
        [DllImport("user32.dll")]
        public static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
        [DllImport("user32.dll")]
        public static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

        static readonly int InputSize = Marshal.SizeOf(typeof(INPUT));

        public static void SendMouse(int dx, int dy, int data, uint flags)
        {
            var input = new INPUT[1];
            input[0].type = INPUT_MOUSE;
            input[0].u.mi.dx = dx;
            input[0].u.mi.dy = dy;
            input[0].u.mi.mouseData = unchecked((uint)data);
            input[0].u.mi.dwFlags = flags;
            SendInput(1, input, InputSize);
        }

        // Arrow, volume and media keys are "extended" keys; letters, Enter, Shift, Ctrl etc. are not.
        public static void SendKey(ushort vk, bool up, bool extended)
        {
            var input = new INPUT[1];
            input[0].type = INPUT_KEYBOARD;
            input[0].u.ki.wVk = vk;
            input[0].u.ki.wScan = (ushort)MapVirtualKey(vk, 0);
            input[0].u.ki.dwFlags = (extended ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0);
            SendInput(1, input, InputSize);
        }

        // Types one character regardless of the keyboard layout.
        public static void SendUnicode(char c)
        {
            var input = new INPUT[2];
            input[0].type = INPUT_KEYBOARD;
            input[0].u.ki.wScan = c;
            input[0].u.ki.dwFlags = KEYEVENTF_UNICODE;
            input[1] = input[0];
            input[1].u.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
            SendInput(2, input, InputSize);
        }
    }
}
