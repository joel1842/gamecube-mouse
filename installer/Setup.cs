// GameCube Mouse setup: a single exe that carries GameCubeMouse.exe inside it.
// Installs per-user (no admin needed) to %LOCALAPPDATA%\Programs\GameCube Mouse, adds Start menu
// (and optional desktop) shortcuts, optional start-with-Windows, and an entry in
// Settings > Apps so it can be uninstalled.   Setup.exe --uninstall removes everything.

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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
        public const string ZadigUrl = "https://zadig.akeo.ie/";

        static string InstallDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName); } }
        static string StartMenuLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); } }
        static string DesktopLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); } }

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            bool quiet = Array.IndexOf(args, "--quiet") >= 0;
            if (Array.IndexOf(args, "--uninstall") >= 0) return Uninstall(quiet);
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

    sealed class SetupForm : Form
    {
        readonly CheckBox startup, desktop, launch;
        readonly Button install, cancel;
        readonly Label status;

        public SetupForm()
        {
            Text = "GameCube Mouse setup";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(460, 330);
            BackColor = Color.White;

            var title = new Label { Text = "GameCube Mouse", Font = new Font("Segoe UI Semibold", 16f), AutoSize = true, Location = new Point(20, 16) };
            var blurb = new Label
            {
                Text = "Use a GameCube controller (official or Mayflash adapter, port 1) as a mouse " +
                       "and keyboard on Windows. Installs just for you, no admin rights needed.",
                Location = new Point(22, 58), Size = new Size(416, 44)
            };
            var driver = new LinkLabel
            {
                Text = "Needs the WinUSB driver on the adapter (one-time, via Zadig).",
                Location = new Point(22, 106), Size = new Size(416, 22)
            };
            driver.Links.Add(driver.Text.IndexOf("Zadig"), 5);
            driver.LinkClicked += delegate { Process.Start(Setup.ZadigUrl); };

            startup = new CheckBox { Text = "Start with Windows", Checked = true, AutoSize = true, Location = new Point(24, 142) };
            desktop = new CheckBox { Text = "Desktop shortcut", AutoSize = true, Location = new Point(24, 170) };
            launch = new CheckBox { Text = "Start GameCube Mouse when setup finishes", Checked = true, AutoSize = true, Location = new Point(24, 198) };
            status = new Label { Location = new Point(22, 236), Size = new Size(416, 40), ForeColor = Color.FromArgb(90, 90, 90) };

            install = new Button { Text = "Install", Size = new Size(100, 32), Location = new Point(232, 284) };
            cancel = new Button { Text = "Cancel", Size = new Size(100, 32), Location = new Point(340, 284) };
            install.Click += delegate { DoInstall(); };
            cancel.Click += delegate { Close(); };
            AcceptButton = install;
            CancelButton = cancel;

            Controls.AddRange(new Control[] { title, blurb, driver, startup, desktop, launch, status, install, cancel });
        }

        void DoInstall()
        {
            install.Enabled = false;
            status.Text = "Installing...";
            Refresh();
            try
            {
                Setup.Install(startup.Checked, desktop.Checked, launch.Checked);
                status.ForeColor = Color.FromArgb(30, 120, 60);
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
    }
}
