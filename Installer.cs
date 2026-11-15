using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PontishInstaller
{
    public class InstallerForm : Form
    {
        private readonly Color BgDark = Color.FromArgb(24, 24, 37);
        private readonly Color BgCard = Color.FromArgb(36, 36, 54);
        private readonly Color TextPrimary = Color.FromArgb(240, 240, 250);
        private readonly Color TextMuted = Color.FromArgb(166, 173, 200);
        private readonly Color GreenActive = Color.FromArgb(39, 174, 96);
        private readonly Color GrayOff = Color.FromArgb(88, 91, 112);

        private CheckBox chkDesktopShortcut;
        private CheckBox chkStartMenuShortcut;
        private CheckBox chkRepairPermissions;
        private CheckBox chkLaunchAfter;
        private ProgressBar progressBar;
        private TextBox txtLog;
        private Button btnInstall;
        private Button btnClose;

        private string installDir;
        public const string AppVersion = "0.1.0";
        private readonly bool isCs = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("cs", StringComparison.OrdinalIgnoreCase);

        private string L(string cs, string en)
        {
            return isCs ? cs : en;
        }

        public InstallerForm()
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            installDir = Path.Combine(localApp, "Programs", "Pontish");
            InitializeUI();
        }

        private void InitializeUI()
        {
            this.Text = L("Instalator - Pontish v" + AppVersion, "Installer - Pontish v" + AppVersion);
            this.Size = new Size(540, 460);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = BgDark;
            this.ForeColor = TextPrimary;
            this.Font = new Font("Segoe UI", 9.5f);
            try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Panel header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 76,
                BackColor = Color.FromArgb(30, 30, 46)
            };

            Label lblTitle = new Label
            {
                Text = L("Pontish v" + AppVersion + " - Instalace / Aktualizace", "Pontish v" + AppVersion + " - Install / Update"),
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = TextPrimary,
                AutoSize = true,
                Location = new Point(20, 12)
            };

            Label lblSub = new Label
            {
                Text = L("Cilova slozka: ", "Target folder: ") + installDir,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = TextMuted,
                AutoSize = true,
                Location = new Point(22, 44)
            };

            header.Controls.Add(lblTitle);
            header.Controls.Add(lblSub);

            Panel card = new Panel
            {
                Location = new Point(20, 92),
                Size = new Size(484, 152),
                BackColor = BgCard
            };

            chkDesktopShortcut = new CheckBox
            {
                Text = L("Vytvorit zastupce na Plose", "Create Desktop shortcut"),
                Checked = true,
                ForeColor = TextPrimary,
                AutoSize = true,
                Location = new Point(16, 14)
            };

            chkStartMenuShortcut = new CheckBox
            {
                Text = L("Pridat do nabidky Start", "Add to Start Menu"),
                Checked = true,
                ForeColor = TextPrimary,
                AutoSize = true,
                Location = new Point(16, 42)
            };

            chkRepairPermissions = new CheckBox
            {
                Text = L("Zkontrolovat a opravit prava NTFS na ~/.ssh/config a klicich (doporuceno)", "Check and repair NTFS permissions on ~/.ssh/config & keys (recommended)"),
                Checked = true,
                ForeColor = TextPrimary,
                AutoSize = true,
                Location = new Point(16, 70)
            };

            chkLaunchAfter = new CheckBox
            {
                Text = L("Spustit aplikaci Pontish po dokonceni", "Launch Pontish after installation"),
                Checked = true,
                ForeColor = TextPrimary,
                AutoSize = true,
                Location = new Point(16, 98)
            };

            Label lblSafe = new Label
            {
                Text = L("Stavajici ~/.ssh/config a SSH klice zustanou nedotceny.", "Existing ~/.ssh/config and SSH keys will remain untouched."),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
                ForeColor = Color.FromArgb(166, 227, 161),
                AutoSize = true,
                Location = new Point(16, 126)
            };

            card.Controls.Add(chkDesktopShortcut);
            card.Controls.Add(chkStartMenuShortcut);
            card.Controls.Add(chkRepairPermissions);
            card.Controls.Add(chkLaunchAfter);
            card.Controls.Add(lblSafe);

            progressBar = new ProgressBar
            {
                Location = new Point(20, 256),
                Size = new Size(484, 16),
                Minimum = 0,
                Maximum = 100
            };

            txtLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(17, 17, 27),
                ForeColor = Color.FromArgb(186, 194, 222),
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 8.5f),
                Location = new Point(20, 282),
                Size = new Size(484, 82)
            };

            btnInstall = new Button
            {
                Text = L("Nainstalovat / Aktualizovat", "Install / Update"),
                Size = new Size(210, 36),
                Location = new Point(184, 374),
                FlatStyle = FlatStyle.Flat,
                BackColor = GreenActive,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnInstall.FlatAppearance.BorderSize = 0;
            btnInstall.Click += async (s, e) => await RunInstallAsync();

            btnClose = new Button
            {
                Text = L("Zavrit", "Close"),
                Size = new Size(100, 36),
                Location = new Point(404, 374),
                FlatStyle = FlatStyle.Flat,
                BackColor = GrayOff,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.Click += (s, e) => this.Close();

            this.Controls.Add(header);
            this.Controls.Add(card);
            this.Controls.Add(progressBar);
            this.Controls.Add(txtLog);
            this.Controls.Add(btnInstall);
            this.Controls.Add(btnClose);

            Log(L("Pripraveno k instalaci do: ", "Ready to install to: ") + installDir);
        }

        private void Log(string msg)
        {
            if (txtLog.InvokeRequired)
            {
                txtLog.Invoke(new Action<string>(Log), msg);
                return;
            }
            txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\r\n");
        }

        private void SetProgress(int val)
        {
            if (progressBar.InvokeRequired)
            {
                progressBar.Invoke(new Action<int>(SetProgress), val);
                return;
            }
            progressBar.Value = Math.Min(100, Math.Max(0, val));
        }

        private async Task RunInstallAsync()
        {
            btnInstall.Enabled = false;
            btnClose.Enabled = false;
            SetProgress(5);

            try
            {
                await Task.Run(() => PerformInstall());
                SetProgress(100);
                Log(L("Instalace uspesne dokoncena!", "Installation completed successfully!"));

                if (chkLaunchAfter.Checked)
                {
                    string exePath = Path.Combine(installDir, "Pontish.exe");
                    if (File.Exists(exePath))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exePath,
                            WorkingDirectory = installDir,
                            UseShellExecute = true
                        });
                    }
                }

                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string sshConfig = Path.Combine(userProfile, ".ssh", "config");
                if (!File.Exists(sshConfig))
                {
                    MessageBox.Show(
                        L(
                            "Aplikace byla nainstalovana.\n\nNa tomto pocitaci zatim nemate soubor ~/.ssh/config.\nV aplikaci kliknete dole na 'Export / Import' a nahrajte svou .zip zalohu z druheho PC.",
                            "Pontish has been installed.\n\nNo ~/.ssh/config file was found on this PC yet.\nClick 'Export / Import' at the bottom of the app to restore a .zip backup, or create ~/.ssh/config."
                        ),
                        L("Instalace dokoncena", "Installation complete"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                }
                else
                {
                    MessageBox.Show(
                        L("Aplikace Pontish byla uspesne nainstalovana!", "Pontish has been installed successfully!"),
                        L("Instalace dokoncena", "Installation complete"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                }

                this.Close();
            }
            catch (Exception ex)
            {
                Log(L("CHYBA: ", "ERROR: ") + ex.Message);
                MessageBox.Show(L("Chyba pri instalaci:\n", "Installation error:\n") + ex.Message, L("Chyba", "Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                btnInstall.Enabled = true;
                btnClose.Enabled = true;
            }
        }

        private void PerformInstall()
        {
            Log(L("Ukoncuji pripadnou bezici instanci Pontish...", "Stopping any running Pontish instances..."));
            StopRunningInstances();
            SetProgress(20);

            if (!Directory.Exists(installDir))
            {
                Directory.CreateDirectory(installDir);
            }

            string legacyPs1 = Path.Combine(installDir, "pontish.ps1");
            if (File.Exists(legacyPs1))
            {
                try { File.Delete(legacyPs1); } catch { }
            }

            string tempZip = Path.Combine(Path.GetTempPath(), "PontishPayload_" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                Log(L("Rozbaluji soubory aplikace...", "Extracting application files..."));
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream resStream = asm.GetManifestResourceStream("PontishPayload.zip"))
                {
                    if (resStream == null)
                    {
                        throw new InvalidOperationException(L("V instalatoru chybi vlozeny balicek aplikace (PontishPayload.zip).", "Embedded application package (PontishPayload.zip) is missing."));
                    }
                    using (FileStream fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write))
                    {
                        resStream.CopyTo(fs);
                    }
                }

                using (ZipArchive archive = ZipFile.OpenRead(tempZip))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;
                        string relPath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                        string destPath = Path.Combine(installDir, relPath);
                        string destDir = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                        {
                            Directory.CreateDirectory(destDir);
                        }
                        entry.ExtractToFile(destPath, true);
                        Log("  + " + entry.FullName);
                    }
                }
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }
            SetProgress(60);

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string sshDir = Path.Combine(userProfile, ".ssh");
            if (!Directory.Exists(sshDir))
            {
                Directory.CreateDirectory(sshDir);
            }

            string askPassSrc = Path.Combine(installDir, "askpass.exe");
            string askPassDst = Path.Combine(sshDir, "askpass.exe");
            if (File.Exists(askPassSrc))
            {
                File.Copy(askPassSrc, askPassDst, true);
                Log(L("Aktualizovan askpass.exe v ~/.ssh/", "Updated askpass.exe in ~/.ssh/"));
            }

            if (chkRepairPermissions.Checked)
            {
                Log(L("Kontroluji a opravuji NTFS prava na ~/.ssh/...", "Checking and repairing NTFS permissions on ~/.ssh/..."));
                RepairSshFolderPermissions(sshDir);
            }
            SetProgress(80);

            string exeTarget = Path.Combine(installDir, "Pontish.exe");
            string icoTarget = Path.Combine(installDir, "pontish.ico");

            if (chkDesktopShortcut.Checked)
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                CreateShortcut(Path.Combine(desktop, "Pontish.lnk"), exeTarget, installDir, icoTarget, L("Pontish - Spravce SSH tunelu k VPS", "Pontish - SSH Tunnel Manager"));
                Log(L("Vytvoren zastupce na Plose.", "Desktop shortcut created."));
            }

            if (chkStartMenuShortcut.Checked)
            {
                string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
                CreateShortcut(Path.Combine(startMenu, "Pontish.lnk"), exeTarget, installDir, icoTarget, L("Pontish - Spravce SSH tunelu k VPS", "Pontish - SSH Tunnel Manager"));
                Log(L("Vytvoren zastupce v nabidce Start.", "Start Menu shortcut created."));
            }

            string uninstallExe = Path.Combine(installDir, "Uninstall.exe");
            try
            {
                string selfExe = Application.ExecutablePath;
                if (!string.Equals(selfExe, uninstallExe, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(selfExe, uninstallExe, true);
                }
            }
            catch { }

            RegisterInWindowsUninstall(installDir, exeTarget, icoTarget, uninstallExe);
            Log(L("Zaregistrovano v Nainstalovanych aplikacich Windows.", "Registered in Windows Installed Apps."));
            SetProgress(95);
        }

        private void RegisterInWindowsUninstall(string installDir, string exeTarget, string icoTarget, string uninstallExe)
        {
            const string regPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Pontish";
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(regPath))
            {
                if (key == null) return;
                key.SetValue("DisplayName", "Pontish", RegistryValueKind.String);
                key.SetValue("DisplayVersion", AppVersion, RegistryValueKind.String);
                key.SetValue("Publisher", "Pontish Open Source", RegistryValueKind.String);
                key.SetValue("DisplayIcon", File.Exists(icoTarget) ? icoTarget : exeTarget, RegistryValueKind.String);
                key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
                key.SetValue("UninstallString", "\"" + uninstallExe + "\" /uninstall", RegistryValueKind.String);
                key.SetValue("QuietUninstallString", "\"" + uninstallExe + "\" /uninstall /quiet", RegistryValueKind.String);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);
                key.SetValue("URLInfoAbout", "https://github.com/petrbla/pontish", RegistryValueKind.String);

                long totalBytes = 0;
                try
                {
                    foreach (string f in Directory.GetFiles(installDir, "*", SearchOption.AllDirectories))
                    {
                        try { totalBytes += new FileInfo(f).Length; } catch { }
                    }
                }
                catch { }
                int sizeKb = (int)Math.Max(1, totalBytes / 1024);
                key.SetValue("EstimatedSize", sizeKb, RegistryValueKind.DWord);
            }
        }

        private static void RunUninstall(bool quiet)
        {
            bool isCs = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("cs", StringComparison.OrdinalIgnoreCase);
            if (!quiet)
            {
                DialogResult res = MessageBox.Show(
                    isCs
                        ? "Opravdu chcete odinstalovat aplikaci Pontish?\n\nVaše konfigurace a klíče ve složce ~/.ssh/ zůstanou zachovány."
                        : "Are you sure you want to uninstall Pontish?\n\nYour configuration and keys in ~/.ssh/ will be preserved.",
                    isCs ? "Odinstalace Pontish" : "Uninstall Pontish",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question
                );
                if (res != DialogResult.Yes) return;
            }

            StopRunningInstancesStatic();

            try
            {
                string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Pontish.lnk");
                if (File.Exists(desktopLnk)) File.Delete(desktopLnk);
            }
            catch { }

            try
            {
                string startLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Pontish.lnk");
                if (File.Exists(startLnk)) File.Delete(startLnk);
            }
            catch { }

            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Pontish", false);
            }
            catch { }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string sshDir = Path.Combine(userProfile, ".ssh");
            string askPassPath = Path.Combine(sshDir, "askpass.exe");
            try
            {
                if (File.Exists(askPassPath)) File.Delete(askPassPath);
            }
            catch { }

            string vaultPath = Path.Combine(sshDir, "pontish.vault");
            if (!quiet && File.Exists(vaultPath))
            {
                DialogResult delVault = MessageBox.Show(
                    isCs
                        ? "Chcete při odinstalaci smazat i soubor s uloženými hesly (~/.ssh/pontish.vault)?\n\nSoubor ~/.ssh/config a vaše SSH klíče zůstanou zachovány."
                        : "Do you also want to delete the saved passwords file (~/.ssh/pontish.vault)?\n\nYour ~/.ssh/config and SSH keys will be preserved.",
                    isCs ? "Uložená hesla" : "Saved passwords",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question
                );
                if (delVault == DialogResult.Yes)
                {
                    try { File.Delete(vaultPath); } catch { }
                }
            }

            string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pontish");
            try
            {
                if (Directory.Exists(appDataDir)) Directory.Delete(appDataDir, true);
            }
            catch { }

            string installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Pontish");
            string selfExe = Application.ExecutablePath;

            if (Directory.Exists(installDir))
            {
                foreach (string file in Directory.GetFiles(installDir, "*", SearchOption.AllDirectories))
                {
                    if (string.Equals(file, selfExe, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(file); } catch { }
                }
                foreach (string dir in Directory.GetDirectories(installDir))
                {
                    try { Directory.Delete(dir, true); } catch { }
                }

                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = string.Format("/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{0}\"", installDir),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                }
                catch { }
            }

            if (!quiet)
            {
                MessageBox.Show(
                    isCs ? "Aplikace Pontish byla úspěšně odinstalována." : "Pontish has been successfully uninstalled.",
                    isCs ? "Odinstalace dokončena" : "Uninstall complete",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
        }

        private void StopRunningInstances()
        {
            StopRunningInstancesStatic();
        }

        private static void StopRunningInstancesStatic()
        {
            try
            {
                foreach (Process proc in Process.GetProcessesByName("Pontish"))
                {
                    try
                    {
                        proc.Kill();
                        proc.WaitForExit(3000);
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -Command \"Get-CimInstance Win32_Process -Filter \\\"Name='powershell.exe'\\\" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -like '*pontish.ps1*' -or $_.CommandLine -like '*SSHMosty.ps1*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(3000);
                }
            }
            catch { }
        }

        private void RepairSshFolderPermissions(string sshDir)
        {
            string currentUser = WindowsIdentity.GetCurrent().Name;
            string[] files = Directory.GetFiles(sshDir);
            foreach (string file in files)
            {
                string name = Path.GetFileName(file).ToLowerInvariant();
                if (name.EndsWith(".pub") || name.EndsWith(".exe") || name.EndsWith(".log") || name.StartsWith("known_hosts"))
                    continue;

                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = "icacls.exe",
                        Arguments = string.Format("\"{0}\" /inheritance:r /grant:r \"{1}:(F)\" \"SYSTEM:(F)\"", file, currentUser),
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (Process p = Process.Start(psi))
                    {
                        p.WaitForExit(3000);
                    }
                    Log("  Prava OK: " + Path.GetFileName(file));
                }
                catch { }
            }
        }

        private void CreateShortcut(string shortcutPath, string targetPath, string workingDir, string iconPath, string description)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = workingDir;
            shortcut.Description = description;
            if (File.Exists(iconPath))
            {
                shortcut.IconLocation = iconPath;
            }
            shortcut.Save();
            Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }

        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool uninstall = false;
            bool quiet = false;
            if (args != null)
            {
                foreach (string a in args)
                {
                    if (string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase))
                        uninstall = true;
                    if (string.Equals(a, "/quiet", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "/q", StringComparison.OrdinalIgnoreCase))
                        quiet = true;
                }
            }
            if (uninstall)
            {
                RunUninstall(quiet);
                return;
            }
            Application.Run(new InstallerForm());
        }
    }
}
