// GameCube Mouse setup: a single exe that carries GameCubeMouse.exe inside it.
// Installs per-user (no admin needed) to %LOCALAPPDATA%\Programs\GameCube Mouse, adds Start menu
// (and optional desktop) shortcuts, optional start-with-Windows, and an entry in
// Settings > Apps so it can be uninstalled.   Setup.exe --uninstall removes everything.

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GameCubeMouseSetup
{
    static class Setup
    {
        const string AppName = "GameCube Mouse";
        const string ExeName = "GameCubeMouse.exe";
        const string UninstallerName = "Uninstall.exe";
        const string Version = "1.0.0";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\GameCubeMouse";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "GameCubeMouse";

        static string InstallDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName); } }
        static string StartMenuLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); } }
        static string DesktopLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); } }

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            bool quiet = Array.IndexOf(args, "--quiet") >= 0;
            if (Array.IndexOf(args, "--uninstall") >= 0) return Uninstall(quiet);
            if (Array.IndexOf(args, "--install-driver") >= 0) return AdapterDriver.InstallElevated();
            if (quiet) { Install(true, false, false); return 0; }
            Application.Run(new SetupForm());
            return 0;
        }

        public static void Install(bool startWithWindows, bool desktopShortcut, bool launch)
        {
            StopRunningCopy();
            Directory.CreateDirectory(InstallDir);
            string exe = Path.Combine(InstallDir, ExeName);
            using (Stream res = Assembly.GetExecutingAssembly().GetManifestResourceStream(ExeName))
            using (FileStream f = File.Create(exe))
                res.CopyTo(f);
            string uninstaller = Path.Combine(InstallDir, UninstallerName);
            string self = Assembly.GetExecutingAssembly().Location;
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(uninstaller), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, uninstaller, true);

            CreateShortcut(StartMenuLink, exe);
            if (desktopShortcut) CreateShortcut(DesktopLink, exe);
            else if (File.Exists(DesktopLink)) File.Delete(DesktopLink);

            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (startWithWindows) k.SetValue(RunName, "\"" + exe + "\"");
                else k.DeleteValue(RunName, false);
            }

            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "GameCube Mouse");
                k.SetValue("DisplayIcon", exe);
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + uninstaller + "\" --uninstall");
                k.SetValue("QuietUninstallString", "\"" + uninstaller + "\" --uninstall --quiet");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                long kb = new FileInfo(exe).Length / 1024 + new FileInfo(uninstaller).Length / 1024;
                k.SetValue("EstimatedSize", (int)kb, RegistryValueKind.DWord);
            }

            if (launch) Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = InstallDir });
        }

        static int Uninstall(bool quiet)
        {
            if (!quiet && MessageBox.Show("Remove " + AppName + " from this PC?", AppName + " uninstall",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return 1;
            StopRunningCopy();
            try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null) k.DeleteValue(RunName, false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\GameCubeMouse", false); } catch { }
            foreach (string link in new[] { StartMenuLink, DesktopLink })
                try { if (File.Exists(link)) File.Delete(link); } catch { }
            try { File.Delete(Path.Combine(InstallDir, ExeName)); } catch { }

            // This exe may be running from the install folder; let cmd remove the folder once we've exited.
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + InstallDir + "\"")
            {
                CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath()
            });
            if (!quiet) MessageBox.Show(AppName + " was removed.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        static void StopRunningCopy()
        {
            foreach (Process p in Process.GetProcessesByName("GameCubeMouse"))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
            Thread.Sleep(200);
        }

        // Uses the Windows Script Host COM object, late-bound so no extra references are needed.
        static void CreateShortcut(string linkPath, string target)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
            Type t = link.GetType();
            t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
            t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(target) });
            t.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "Use a GameCube controller as a mouse and keyboard" });
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        }
    }

    // ------------------------------------------------------------------------------------------
    // The adapter driver. The adapter calls itself a HID device but Windows can't use it as one,
    // so (like Zadig) we point it at Microsoft's built-in WinUSB driver with a tiny INF. Windows
    // only accepts a signed driver package, so the INF's catalog is signed with a certificate
    // made on this PC; its private key is deleted straight after, so it can't sign anything else.
    // ------------------------------------------------------------------------------------------
    static class AdapterDriver
    {
        public enum State { Ready, NeedsDriver, NotPlugged }

        const string HardwareId = @"USB\VID_057E&PID_0337";
        const string EnumKey = @"SYSTEM\CurrentControlSet\Enum\USB\VID_057E&PID_0337";
        const uint INSTALLFLAG_FORCE = 0x1;
        const int ERROR_NO_SUCH_DEVINST = unchecked((int)0xE000020B);

        public const int ExitOk = 0, ExitNotPlugged = 2, ExitSignFailed = 3, ExitInstallFailed = 4;

        public static string WorkDir { get { return Path.Combine(Path.GetTempPath(), "GameCubeMouseDriver"); } }
        public static string LogPath { get { return Path.Combine(WorkDir, "install.log"); } }

        [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwnd, string hardwareId, string infPath, uint flags, out bool reboot);

        // Ready = already on WinUSB (by us, Zadig or Dolphin). The adapter reports the same serial on
        // every port, so once set up it stays set up even when unplugged.
        public static State Check()
        {
            bool seen = false;
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(EnumKey))
                {
                    if (k != null)
                        foreach (string inst in k.GetSubKeyNames())
                            using (RegistryKey d = k.OpenSubKey(inst))
                            {
                                if (d == null) continue;
                                seen = true;
                                if (string.Equals(d.GetValue("Service") as string, "WinUSB", StringComparison.OrdinalIgnoreCase))
                                    return State.Ready;
                            }
                }
            }
            catch { }
            return seen && IsPresent() ? State.NeedsDriver : State.NotPlugged;
        }

        static bool IsPresent()
        {
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\VID_057E&PID_0337%'"))
                using (ManagementObjectCollection found = s.Get())
                    return found.Count > 0;
            }
            catch { return false; }
        }

        const string Inf =
            "; GameCube Controller Adapter (WUP-028) -> Microsoft WinUSB. Written by GameCube Mouse setup.\r\n" +
            "[Version]\r\n" +
            "Signature   = \"$Windows NT$\"\r\n" +
            "Class       = USBDevice\r\n" +
            "ClassGUID   = {88BAE032-5A81-49f0-BC3D-A4FF138216D6}\r\n" +
            "Provider    = %Provider%\r\n" +
            "CatalogFile = gcadapter.cat\r\n" +
            "DriverVer   = 11/15/2026,1.0.0.0\r\n" +
            "\r\n" +
            "[Manufacturer]\r\n" +
            "%Provider% = Devices,NTamd64,NTarm64,NTx86\r\n" +
            "\r\n" +
            "[Devices.NTamd64]\r\n%DeviceName% = USB_Install, USB\\VID_057E&PID_0337\r\n" +
            "[Devices.NTarm64]\r\n%DeviceName% = USB_Install, USB\\VID_057E&PID_0337\r\n" +
            "[Devices.NTx86]\r\n%DeviceName% = USB_Install, USB\\VID_057E&PID_0337\r\n" +
            "\r\n" +
            "[USB_Install]\r\nInclude = winusb.inf\r\nNeeds   = WINUSB.NT\r\n" +
            "\r\n" +
            "[USB_Install.Services]\r\nInclude = winusb.inf\r\nNeeds   = WINUSB.NT.Services\r\n" +
            "\r\n" +
            "[USB_Install.HW]\r\nAddReg = Dev_AddReg\r\n" +
            "\r\n" +
            "[Dev_AddReg]\r\nHKR,,DeviceInterfaceGUIDs,0x10000,\"{6B3C1F0E-2D8A-4C57-9E41-0A6F3B2C7D15}\"\r\n" +
            "\r\n" +
            "[Strings]\r\nProvider   = \"GameCube Mouse\"\r\nDeviceName = \"GameCube Controller Adapter\"\r\n";

        // Makes the catalog, a one-off signing certificate, signs, trusts the certificate, deletes its key.
        const string SignScript = @"
param([string]$Dir)
$ErrorActionPreference = 'Stop'
$subject = 'CN=GameCube Mouse adapter driver (this PC only)'
$cat = Join-Path $Dir 'gcadapter.cat'
New-FileCatalog -Path (Join-Path $Dir 'gcadapter.inf') -CatalogFilePath $cat -CatalogVersion 2 | Out-Null
foreach ($store in 'Root','TrustedPublisher') {
    Get-ChildItem ""Cert:\LocalMachine\$store"" | Where-Object { $_.Subject -eq $subject } | Remove-Item
}
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -CertStoreLocation Cert:\LocalMachine\My `
        -KeyExportPolicy NonExportable -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(30)
try {
    $sig = Set-AuthenticodeSignature -FilePath $cat -Certificate $cert -HashAlgorithm SHA256
    if (-not $sig.SignerCertificate) { throw ('signing failed: ' + $sig.StatusMessage) }
    $public = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 (,$cert.RawData)
    foreach ($store in 'Root','TrustedPublisher') {
        $s = New-Object System.Security.Cryptography.X509Certificates.X509Store($store, 'LocalMachine')
        $s.Open('ReadWrite'); $s.Add($public); $s.Close()
    }
} finally {
    Remove-Item (""Cert:\LocalMachine\My\"" + $cert.Thumbprint) -DeleteKey
}
'Signed ' + $cat
";

        // Runs as administrator (Setup.exe --install-driver). Writes a log next to the files.
        public static int InstallElevated()
        {
            var log = new StringBuilder();
            int result = ExitInstallFailed;
            try
            {
                if (Directory.Exists(WorkDir)) Directory.Delete(WorkDir, true);
                Directory.CreateDirectory(WorkDir);
                string inf = Path.Combine(WorkDir, "gcadapter.inf");
                File.WriteAllText(inf, Inf, Encoding.ASCII);
                string script = Path.Combine(WorkDir, "sign.ps1");
                File.WriteAllText(script, SignScript, Encoding.UTF8);

                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\" -Dir \"" + WorkDir + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    log.AppendLine("sign: exit " + p.ExitCode + "\r\n" + output);
                    if (p.ExitCode != 0) { result = ExitSignFailed; return result; }
                }

                bool reboot;
                if (UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, HardwareId, inf, INSTALLFLAG_FORCE, out reboot))
                {
                    log.AppendLine("driver installed" + (reboot ? " (restart needed)" : ""));
                    result = ExitOk;
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    log.AppendLine("UpdateDriverForPlugAndPlayDevices failed: 0x" + err.ToString("X8"));
                    result = err == ERROR_NO_SUCH_DEVINST ? ExitNotPlugged : ExitInstallFailed;
                }
                return result;
            }
            catch (Exception ex)
            {
                log.AppendLine("error: " + ex);
                return result;
            }
            finally
            {
                log.AppendLine("result " + result);
                try { File.WriteAllText(LogPath, log.ToString()); } catch { }
            }
        }
    }

    // ------------------------------------------------------------------------------------------
    // Game mode needs ViGEmBus (by Nefarius) to create the virtual Xbox controller. Setup can fetch
    // its official installer; the file's SHA-256 is pinned so nothing else ever gets run.
    // ------------------------------------------------------------------------------------------
    static class GameModeDriver
    {
        const string Url = "https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe";
        const string Sha256 = "89220A7865076B342892F98865F3499FB7C4CFD673159E89D352C360FD014C6A";

        public static bool IsInstalled
        {
            get
            {
                try { using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ViGEmBus")) return k != null; }
                catch { return false; }
            }
        }

        // Downloads, checks the hash, runs the ViGEmBus installer (it asks for permission itself) and waits.
        // Returns null on success, or a message saying what went wrong.
        public static string Install()
        {
            string path = Path.Combine(Path.GetTempPath(), "ViGEmBus_1.22.0_setup.exe");
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                using (var web = new System.Net.WebClient()) web.DownloadFile(Url, path);
                string hash;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (FileStream fs = File.OpenRead(path))
                    hash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
                if (!string.Equals(hash, Sha256, StringComparison.OrdinalIgnoreCase))
                    return "The download didn't match the expected file, so it wasn't run.";
                using (Process p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }))
                    p.WaitForExit();
                return IsInstalled ? null : "ViGEmBus wasn't installed. Click the button to try again.";
            }
            catch (System.ComponentModel.Win32Exception) { return "Cancelled. Click the button to try again."; }
            catch (Exception ex) { return "Couldn't install ViGEmBus: " + ex.Message; }
            finally { try { File.Delete(path); } catch { } }
        }
    }

    sealed class SetupForm : Form
    {
        readonly CheckBox startup, desktop, launch;
        readonly Button install, cancel, driverButton, gameButton;
        readonly Label status, driverStatus, gameStatus;
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 2000 };
        bool driverBusy, gameBusy;

        static readonly Color Good = Color.FromArgb(30, 120, 60), Muted = Color.FromArgb(90, 90, 90);

        public SetupForm()
        {
            Text = "GameCube Mouse setup";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(460, 510);
            BackColor = Color.White;

            var title = new Label { Text = "GameCube Mouse", Font = new Font("Segoe UI Semibold", 16f), AutoSize = true, Location = new Point(20, 16) };
            var blurb = new Label
            {
                Text = "Use a GameCube controller (official or Mayflash adapter, port 1) as a mouse " +
                       "and keyboard on Windows. Installs just for you.",
                Location = new Point(22, 58), Size = new Size(416, 44)
            };

            startup = new CheckBox { Text = "Start with Windows", Checked = true, AutoSize = true, Location = new Point(24, 112) };
            desktop = new CheckBox { Text = "Desktop shortcut", AutoSize = true, Location = new Point(24, 140) };
            launch = new CheckBox { Text = "Start GameCube Mouse when setup finishes", Checked = true, AutoSize = true, Location = new Point(24, 168) };

            var driverTitle = new Label { Text = "Adapter driver", Font = new Font("Segoe UI Semibold", 10f), AutoSize = true, Location = new Point(22, 208) };
            driverStatus = new Label { Location = new Point(22, 232), Size = new Size(416, 40), ForeColor = Muted };
            driverButton = new Button { Text = "Set up adapter driver", Size = new Size(190, 32), Location = new Point(22, 274), FlatStyle = FlatStyle.System };
            driverButton.Click += delegate { InstallDriver(); };

            var gameTitle = new Label { Text = "Game mode (optional)", Font = new Font("Segoe UI Semibold", 10f), AutoSize = true, Location = new Point(22, 318) };
            gameStatus = new Label { Location = new Point(22, 342), Size = new Size(416, 40), ForeColor = Muted };
            gameButton = new Button { Text = "Install ViGEmBus", Size = new Size(190, 32), Location = new Point(22, 384), FlatStyle = FlatStyle.System };
            gameButton.Click += delegate { InstallGameDriver(); };

            status = new Label { Location = new Point(22, 426), Size = new Size(416, 34), ForeColor = Muted };
            install = new Button { Text = "Install", Size = new Size(100, 32), Location = new Point(232, 466) };
            cancel = new Button { Text = "Cancel", Size = new Size(100, 32), Location = new Point(340, 466) };
            install.Click += delegate { DoInstall(); };
            cancel.Click += delegate { Close(); };
            AcceptButton = install;
            CancelButton = cancel;

            Controls.AddRange(new Control[] { title, blurb, startup, desktop, launch, driverTitle, driverStatus, driverButton, gameTitle, gameStatus, gameButton, status, install, cancel });

            poll.Tick += delegate { RefreshDriver(); RefreshGame(); };
            Shown += delegate
            {
                const int BCM_SETSHIELD = 0x160C;
                SendMessage(driverButton.Handle, BCM_SETSHIELD, IntPtr.Zero, (IntPtr)1);   // UAC shield on the buttons
                SendMessage(gameButton.Handle, BCM_SETSHIELD, IntPtr.Zero, (IntPtr)1);
                RefreshDriver();
                RefreshGame();
                poll.Start();
            };
        }

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        void RefreshGame()
        {
            if (gameBusy) return;
            if (GameModeDriver.IsInstalled)
            {
                gameStatus.ForeColor = Good;
                gameStatus.Text = "✓ Ready. When an emulator like PCSX2 is running, the controller becomes an Xbox controller.";
                gameButton.Visible = false;
            }
            else
            {
                gameStatus.ForeColor = Muted;
                gameStatus.Text = "To play emulator games with the controller, install the free ViGEmBus driver. " +
                                  "It creates a virtual Xbox controller.";
                gameButton.Visible = true;
                gameButton.Enabled = true;
            }
        }

        void InstallGameDriver()
        {
            gameBusy = true;
            gameButton.Enabled = false;
            gameStatus.ForeColor = Muted;
            gameStatus.Text = "Downloading and installing ViGEmBus...";
            var worker = new Thread(() =>
            {
                string error = GameModeDriver.Install();
                BeginInvoke((Action)(() =>
                {
                    gameBusy = false;
                    RefreshGame();
                    if (error != null)
                    {
                        gameStatus.ForeColor = Color.Firebrick;
                        gameStatus.Text = error;
                        gameButton.Enabled = true;
                    }
                }));
            }) { IsBackground = true };
            worker.Start();
        }

        void RefreshDriver()
        {
            if (driverBusy) return;
            switch (AdapterDriver.Check())
            {
                case AdapterDriver.State.Ready:
                    driverStatus.ForeColor = Good;
                    driverStatus.Text = "✓ The adapter is set up. Nothing else to do.";
                    driverButton.Visible = false;
                    break;
                case AdapterDriver.State.NeedsDriver:
                    driverStatus.ForeColor = Color.Black;
                    driverStatus.Text = "Adapter found. It needs a one-time driver setup (Windows will ask for permission).";
                    driverButton.Visible = true;
                    driverButton.Enabled = true;
                    break;
                default:
                    driverStatus.ForeColor = Muted;
                    driverStatus.Text = "Plug the GameCube adapter into a USB port to set up its driver. " +
                                        "(Mayflash: switch it to Wii U mode.)";
                    driverButton.Visible = true;
                    driverButton.Enabled = false;
                    break;
            }
        }

        void InstallDriver()
        {
            driverBusy = true;
            driverButton.Enabled = false;
            driverStatus.ForeColor = Muted;
            driverStatus.Text = "Setting up the adapter driver...";
            string self = Assembly.GetExecutingAssembly().Location;
            var worker = new Thread(() =>
            {
                int code;
                try
                {
                    using (Process p = Process.Start(new ProcessStartInfo(self, "--install-driver") { Verb = "runas", UseShellExecute = true }))
                    {
                        p.WaitForExit();
                        code = p.ExitCode;
                    }
                }
                catch (System.ComponentModel.Win32Exception) { code = -1; }   // permission prompt was declined
                BeginInvoke((Action)(() => DriverFinished(code)));
            }) { IsBackground = true };
            worker.Start();
        }

        void DriverFinished(int code)
        {
            driverBusy = false;
            RefreshDriver();
            if (code == AdapterDriver.ExitOk) return;
            driverButton.Enabled = true;
            driverStatus.ForeColor = Color.Firebrick;
            driverStatus.Text =
                code == -1 ? "Driver setup was cancelled. Click the button to try again." :
                code == AdapterDriver.ExitNotPlugged ? "The adapter was unplugged. Plug it in and try again." :
                "Driver setup didn't work (details in " + AdapterDriver.LogPath + "). You can use Zadig instead, see the README.";
        }

        void DoInstall()
        {
            install.Enabled = false;
            status.Text = "Installing...";
            Refresh();
            try
            {
                Setup.Install(startup.Checked, desktop.Checked, launch.Checked);
                status.ForeColor = Good;
                status.Text = "Installed. Look for the cursor icon next to the clock. Uninstall any time from Settings > Apps.";
                cancel.Text = "Close";
            }
            catch (Exception ex)
            {
                status.ForeColor = Color.Firebrick;
                status.Text = "Install failed: " + ex.Message;
                install.Enabled = true;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) poll.Dispose();
            base.Dispose(disposing);
        }
    }
}
