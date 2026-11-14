using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PontishApp
{
    public class MainForm : Form
    {
        private const string AppVersion = "0.0.1";
        private readonly SshConfigService _cfg;
        private readonly TunnelManager _tunnels;
        private readonly ConcurrentDictionary<string, ConPtyTerminal> _terminals = new ConcurrentDictionary<string, ConPtyTerminal>();
        private readonly WebView2 _webView;
        private readonly NotifyIcon _trayIcon;
        private readonly ContextMenuStrip _trayMenu;
        private readonly System.Windows.Forms.Timer _pollTimer;
        private bool _allowClose;
        private bool _webReady;
        private bool? _lastAnyActive;
        private readonly Icon _iconIdle;
        private readonly Icon _iconActive;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public MainForm()
        {
            _cfg = new SshConfigService();
            _tunnels = new TunnelManager(_cfg);
            _tunnels.OnLog += msg => PostToWeb(new { type = "log", message = msg });

            Text = $"Pontish v{AppVersion}";
            Size = new Size(1180, 780);
            MinimumSize = new Size(920, 620);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(7, 11, 20);

            _iconIdle = CreateStatusIcon(false);
            _iconActive = CreateStatusIcon(true);

            string icoPath = Path.Combine(_cfg.AppBaseDir, "pontish.ico");
            if (File.Exists(icoPath))
            {
                try { Icon = new Icon(icoPath); } catch { Icon = _iconIdle; }
            }
            else
            {
                Icon = _iconIdle;
            }

            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(7, 11, 20)
            };
            Controls.Add(_webView);

            _trayMenu = new ContextMenuStrip();
            _trayIcon = new NotifyIcon
            {
                Text = "Pontish",
                Icon = _iconIdle,
                ContextMenuStrip = _trayMenu,
                Visible = true
            };
            _trayIcon.DoubleClick += (s, e) => RestoreFromTray();

            _pollTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _pollTimer.Tick += (s, e) => RefreshTunnelStates();

            Shown += async (s, e) =>
            {
                EnableDarkTitleBar();
                await InitWebViewAsync();
            };

            FormClosing += (s, e) =>
            {
                if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                    _trayIcon.ShowBalloonTip(2000, "Pontish", "Aplikace běží na pozadí v System Tray.", ToolTipIcon.Info);
                }
                else
                {
                    CleanupAll();
                }
            };
        }

        private void EnableDarkTitleBar(bool isDark = true)
        {
            try
            {
                int dark = isDark ? 1 : 0;
                DwmSetWindowAttribute(Handle, 20, ref dark, 4);
                int cap = isDark ? 0x001B110B : 0x00FFFFFF; // #0B111B or #FFFFFF (BGR)
                DwmSetWindowAttribute(Handle, 35, ref cap, 4);
            }
            catch { }
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private async Task InitWebViewAsync()
        {
            try
            {
                string userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pontish", "WebView2");
                Directory.CreateDirectory(userData);
                var env = await CoreWebView2Environment.CreateAsync(null, userData);
                await _webView.EnsureCoreWebView2Async(env);

                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;

                string wwwroot = Path.Combine(_cfg.AppBaseDir, "wwwroot");
                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "pontish.local",
                    wwwroot,
                    CoreWebView2HostResourceAccessKind.Allow);

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.CoreWebView2.Navigate("https://pontish.local/index.html");
                _webReady = true;

                _tunnels.ReloadConfig();
                _pollTimer.Start();
            }
            catch (Exception ex)
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pontish");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "crash.log"), $"[{DateTime.Now:O}] InitWebViewAsync:\n{ex}\n");
                MessageBox.Show(this, "Chyba WebView2:\n" + ex.Message, "Pontish", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void PostToWeb(object payload)
        {
            if (!_webReady || IsDisposed) return;
            try
            {
                string json = JsonSerializer.Serialize(payload);
                if (InvokeRequired)
                    BeginInvoke(new Action(() => { try { _webView.CoreWebView2?.PostWebMessageAsJson(json); } catch { } }));
                else
                    _webView.CoreWebView2?.PostWebMessageAsJson(json);
            }
            catch { }
        }

        private void RefreshTunnelStates()
        {
            int activeCount = _tunnels.UpdateStates();
            bool anyActive = activeCount > 0;
            if (_lastAnyActive != anyActive)
            {
                _lastAnyActive = anyActive;
                var ico = anyActive ? _iconActive : _iconIdle;
                Icon = ico;
                _trayIcon.Icon = ico;
            }
            _trayIcon.Text = $"Pontish - Active: {activeCount}/{_tunnels.Tunnels.Count}";
            RebuildTrayMenu();
            SendStateToWeb();
        }

        private void RebuildTrayMenu()
        {
            _trayMenu.Items.Clear();
            var openItem = new ToolStripMenuItem("Pontish") { Font = new Font(_trayMenu.Font, FontStyle.Bold) };
            openItem.Click += (s, e) => RestoreFromTray();
            _trayMenu.Items.Add(openItem);
            _trayMenu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (s, e) =>
            {
                _allowClose = true;
                _trayIcon.Visible = false;
                Close();
            };
            _trayMenu.Items.Add(exitItem);
        }

        private void SendStateToWeb()
        {
            var hostsDto = _tunnels.AllHosts.Select(h => new
            {
                hostAlias = h.HostAlias,
                hostName = h.HostName,
                port = h.Port,
                user = h.User,
                identityFile = h.IdentityFile,
                groupComment = h.GroupComment,
                hasForwards = h.Forwards.Count > 0,
                userRequestedRun = h.UserRequestedRun,
                isConnecting = h.IsConnecting,
                wasConnected = h.WasConnected,
                forwards = h.Forwards.Select(f => new
                {
                    name = f.Name,
                    localPort = f.LocalPort,
                    remoteHost = f.RemoteHost,
                    remotePort = f.RemotePort,
                    isDb = f.IsDb,
                    isListening = f.IsListening
                })
            });

            var langs = _cfg.I18n.Keys.OrderBy(k => k).Select(k => new
            {
                code = k,
                name = _cfg.I18n[k].TryGetValue("_LanguageName", out string n) ? n : k.ToUpperInvariant()
            });

            PostToWeb(new
            {
                type = "state",
                version = AppVersion,
                lang = _cfg.CurrentLang,
                langs,
                i18n = _cfg.I18n.TryGetValue(_cfg.CurrentLang, out var dict) ? dict : new Dictionary<string, string>(),
                autoReconnect = _tunnels.AutoReconnect,
                sshConfigPath = _cfg.SshConfigPath,
                hosts = hostsDto
            });
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var root = doc.RootElement;
                string cmd = root.GetProperty("cmd").GetString();

                switch (cmd)
                {
                    case "init":
                        SendStateToWeb();
                        PostToWeb(new { type = "log", message = "Pontish ready." });
                        break;

                    case "setTheme":
                        bool darkTheme = (root.TryGetProperty("theme", out var tp) ? tp.GetString() : "dark") != "light";
                        EnableDarkTitleBar(darkTheme);
                        break;

                    case "setLang":
                        string newLang = root.GetProperty("lang").GetString()?.ToLowerInvariant() ?? "en";
                        if (_cfg.I18n.ContainsKey(newLang))
                        {
                            _cfg.CurrentLang = newLang;
                            _cfg.SaveSettings();
                            SendStateToWeb();
                        }
                        break;

                    case "setAutoReconnect":
                        _tunnels.AutoReconnect = root.GetProperty("enabled").GetBoolean();
                        break;

                    case "reloadConfig":
                        _tunnels.ReloadConfig();
                        RefreshTunnelStates();
                        PostToWeb(new { type = "log", message = "Reloaded ~/.ssh/config" });
                        break;

                    case "editConfig":
                        if (File.Exists(_cfg.SshConfigPath))
                            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_cfg.SshConfigPath}\"") { UseShellExecute = true });
                        break;

                    case "openUrl":
                        string url = root.GetProperty("url").GetString();
                        if (!string.IsNullOrWhiteSpace(url))
                            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                        break;

                    case "copyText":
                        string text = root.GetProperty("text").GetString() ?? "";
                        Clipboard.SetText(text);
                        PostToWeb(new { type = "log", message = $"Copied: {text}" });
                        break;

                    case "forgetPasswords":
                        _cfg.SavedPasswords.Clear();
                        _cfg.SavedSudoPasswords.Clear();
                        PostToWeb(new { type = "log", message = "Saved passwords cleared." });
                        break;

                    case "toggleBridge":
                        HandleToggleBridge(root.GetProperty("hostAlias").GetString(), root.TryGetProperty("password", out var pwProp) ? pwProp.GetString() : null, root.TryGetProperty("remember", out var remProp) && remProp.GetBoolean());
                        break;

                    case "startAll":
                        foreach (var t in _tunnels.Tunnels.Where(x => !x.UserRequestedRun))
                            HandleToggleBridge(t.HostAlias, null, false);
                        break;

                    case "stopAll":
                        _tunnels.StopAll();
                        RefreshTunnelStates();
                        break;

                    case "exportBundle":
                        HandleExport(root.TryGetProperty("allKeys", out var ak) && ak.GetBoolean());
                        break;

                    case "importBundle":
                        HandleImport();
                        break;

                    case "termStart":
                        StartTerminal(
                            root.GetProperty("id").GetString(),
                            root.GetProperty("mode").GetString(),
                            root.TryGetProperty("hostAlias", out var ha) ? ha.GetString() : "",
                            root.TryGetProperty("asRoot", out var ar) && ar.GetBoolean(),
                            (short)(root.TryGetProperty("cols", out var c) ? c.GetInt32() : 100),
                            (short)(root.TryGetProperty("rows", out var r) ? r.GetInt32() : 30));
                        break;

                    case "termInput":
                        if (_terminals.TryGetValue(root.GetProperty("id").GetString(), out var tIn))
                            tIn.WriteInput(root.GetProperty("data").GetString());
                        break;

                    case "termResize":
                        if (_terminals.TryGetValue(root.GetProperty("id").GetString(), out var tRes))
                            tRes.Resize((short)root.GetProperty("cols").GetInt32(), (short)root.GetProperty("rows").GetInt32());
                        break;

                    case "termClose":
                        if (_terminals.TryRemove(root.GetProperty("id").GetString(), out var tClose))
                            tClose.Dispose();
                        break;

                    case "openExternal":
                        OpenExternalTerminal(root.GetProperty("hostAlias").GetString());
                        break;

                    case "sftpListLocal":
                        string lp = root.TryGetProperty("path", out var lpp) ? lpp.GetString() : "";
                        var localEntries = _cfg.ListLocal(lp);
                        string actualLocal = string.IsNullOrWhiteSpace(lp) || !Directory.Exists(lp) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : Path.GetFullPath(lp);
                        PostToWeb(new { type = "sftpLocal", reqId = root.GetProperty("reqId").GetString(), path = actualLocal, entries = localEntries });
                        break;

                    case "sftpListRemote":
                        HandleSftpListRemote(root);
                        break;

                    case "sftpCheckSudo":
                        HandleSftpCheckSudo(root);
                        break;

                    case "sftpAction":
                        HandleSftpAction(root);
                        break;

                    case "quitApp":
                        _allowClose = true;
                        _trayIcon.Visible = false;
                        Close();
                        break;
                }
            }
            catch (Exception ex)
            {
                PostToWeb(new { type = "log", message = $"ERROR: {ex.Message}" });
            }
        }

        private void HandleToggleBridge(string hostAlias, string password, bool remember)
        {
            var t = _tunnels.Tunnels.FirstOrDefault(x => x.HostAlias == hostAlias);
            if (t == null) return;

            if (t.UserRequestedRun || (t.BridgeProcess != null && !t.BridgeProcess.HasExited))
            {
                _tunnels.StopTunnel(t);
                RefreshTunnelStates();
                return;
            }

            if (!string.IsNullOrEmpty(password))
            {
                if (remember) _cfg.SavedPasswords[hostAlias] = password;
                _tunnels.StartTunnel(t, password);
                RefreshTunnelStates();
                return;
            }

            if (_cfg.SavedPasswords.ContainsKey(hostAlias))
            {
                _tunnels.StartTunnel(t);
                RefreshTunnelStates();
                return;
            }

            string keyPath = SshConfigService.ResolveSshPath(t.IdentityFile);
            if (TunnelManager.IsKeyEncrypted(keyPath))
            {
                PostToWeb(new { type = "needPassword", hostAlias, info = string.IsNullOrEmpty(t.IdentityFile) ? $"{t.User}@{t.HostName}" : t.IdentityFile });
                return;
            }

            _tunnels.StartTunnel(t);
            RefreshTunnelStates();
        }

        private void StartTerminal(string id, string mode, string hostAlias, bool asRoot, short cols, short rows)
        {
            var term = new ConPtyTerminal(id);
            term.OnOutput += (tid, data) => PostToWeb(new { type = "termOutput", id = tid, data });
            term.OnExited += (tid, code) =>
            {
                _terminals.TryRemove(tid, out _);
                PostToWeb(new { type = "termExit", id = tid, code });
            };

            string cmdLine = mode switch
            {
                "powershell" => "powershell.exe -NoLogo",
                "cmd" => "cmd.exe",
                _ => asRoot
                    ? $"ssh.exe -t -F \"{_cfg.SshConfigPath}\" {hostAlias} \"sudo -i\""
                    : $"ssh.exe -F \"{_cfg.SshConfigPath}\" {hostAlias}"
            };

            _terminals[id] = term;
            term.Start(cmdLine, null, cols, rows);
        }

        private void OpenExternalTerminal(string hostAlias)
        {
            string args = $"/k \"title SSH: {hostAlias} && ssh.exe -F \"{_cfg.SshConfigPath}\" {hostAlias}\"";
            Process.Start(new ProcessStartInfo("cmd.exe", args) { UseShellExecute = true });
        }

        private void HandleSftpListRemote(JsonElement root)
        {
            string reqId = root.GetProperty("reqId").GetString();
            string hostAlias = root.GetProperty("hostAlias").GetString();
            string path = root.TryGetProperty("path", out var p) ? p.GetString() : "~";
            bool asRoot = root.TryGetProperty("asRoot", out var r) && r.GetBoolean();

            Task.Run(() =>
            {
                var res = _cfg.ListRemote(hostAlias, path, asRoot);
                if (!res.Ok)
                    PostToWeb(new { type = "log", message = $"SFTP ({hostAlias}): {res.Error}" });
                PostToWeb(new { type = "sftpRemote", reqId, ok = res.Ok, path = res.RealPath, entries = res.Entries, error = res.Error });
            });
        }

        private void HandleSftpCheckSudo(JsonElement root)
        {
            string reqId = root.GetProperty("reqId").GetString();
            string hostAlias = root.GetProperty("hostAlias").GetString();
            string password = root.TryGetProperty("password", out var pw) ? pw.GetString() : null;

            Task.Run(() =>
            {
                if (!string.IsNullOrEmpty(password))
                    _cfg.SavedSudoPasswords[hostAlias] = password;

                string cmd = _cfg.WrapRemoteCmd("true", true, hostAlias);
                var res = _cfg.RunSshTool("ssh.exe", $"-F \"{_cfg.SshConfigPath}\" {hostAlias} \"{cmd}\"", hostAlias);
                if (res.ExitCode != 0 && !string.IsNullOrEmpty(password))
                    _cfg.SavedSudoPasswords.Remove(hostAlias);

                PostToWeb(new { type = "sftpSudoResult", reqId, hostAlias, ok = res.ExitCode == 0, needPassword = res.ExitCode != 0 && string.IsNullOrEmpty(password) });
            });
        }

        private void HandleSftpAction(JsonElement root)
        {
            string action = root.GetProperty("action").GetString();
            string reqId = root.GetProperty("reqId").GetString();
            string hostAlias = root.GetProperty("hostAlias").GetString();
            bool asRoot = root.TryGetProperty("asRoot", out var ar) && ar.GetBoolean();
            string localPath = root.TryGetProperty("localPath", out var lp) ? lp.GetString() : "";
            string remotePath = root.TryGetProperty("remotePath", out var rp) ? rp.GetString() : "";
            string name = root.TryGetProperty("name", out var nm) ? nm.GetString() : "";

            if (action == "pickUpload")
            {
                using var ofd = new OpenFileDialog();
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                localPath = ofd.FileName;
                name = Path.GetFileName(localPath);
                action = "upload";
            }
            else if (action == "pickDownload")
            {
                using var sfd = new SaveFileDialog { FileName = name };
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                localPath = Path.GetDirectoryName(sfd.FileName);
                action = "download";
            }

            Task.Run(() =>
            {
                bool ok = false;
                string err = "";
                if (action == "upload")
                {
                    if (asRoot)
                    {
                        string tmp = "/tmp/.pontish_up_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                        var r1 = _cfg.RunSshTool("scp.exe", $"-F \"{_cfg.SshConfigPath}\" -r \"{localPath}\" \"{hostAlias}:{tmp}\"", hostAlias);
                        if (r1.ExitCode == 0)
                        {
                            string safeDest = $"{remotePath.TrimEnd('/')}/{name}".Replace("'", "'\\''");
                            string mv = _cfg.WrapRemoteCmd($"rm -rf '{safeDest}' && mv '{tmp}' '{safeDest}'", true, hostAlias);
                            var r2 = _cfg.RunSshTool("ssh.exe", $"-F \"{_cfg.SshConfigPath}\" {hostAlias} \"{mv}\"", hostAlias);
                            ok = r2.ExitCode == 0; err = r2.Error;
                        }
                        else err = r1.Error;
                    }
                    else
                    {
                        var r = _cfg.RunSshTool("scp.exe", $"-F \"{_cfg.SshConfigPath}\" -r \"{localPath}\" \"{hostAlias}:{remotePath.TrimEnd('/')}/\"", hostAlias);
                        ok = r.ExitCode == 0; err = r.Error;
                    }
                }
                else if (action == "download")
                {
                    if (asRoot)
                    {
                        string tmpDir = "/tmp/.pontish_dl_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                        string safeSrc = remotePath.Replace("'", "'\\''");
                        string prep = _cfg.WrapRemoteCmd($"mkdir -p '{tmpDir}' && cp -rf '{safeSrc}' '{tmpDir}/' && chmod -R a+rX '{tmpDir}'", true, hostAlias);
                        var r1 = _cfg.RunSshTool("ssh.exe", $"-F \"{_cfg.SshConfigPath}\" {hostAlias} \"{prep}\"", hostAlias);
                        if (r1.ExitCode == 0)
                        {
                            var r2 = _cfg.RunSshTool("scp.exe", $"-F \"{_cfg.SshConfigPath}\" -r \"{hostAlias}:{tmpDir}/{name}\" \"{localPath}\"", hostAlias);
                            string clean = _cfg.WrapRemoteCmd($"rm -rf '{tmpDir}'", true, hostAlias);
                            _cfg.RunSshTool("ssh.exe", $"-F \"{_cfg.SshConfigPath}\" {hostAlias} \"{clean}\"", hostAlias);
                            ok = r2.ExitCode == 0; err = r2.Error;
                        }
                        else err = r1.Error;
                    }
                    else
                    {
                        var r = _cfg.RunSshTool("scp.exe", $"-F \"{_cfg.SshConfigPath}\" -r \"{hostAlias}:{remotePath}\" \"{localPath}\"", hostAlias);
                        ok = r.ExitCode == 0; err = r.Error;
                    }
                }
                else if (action == "mkdir")
                {
                    string safe = $"{remotePath.TrimEnd('/')}/{name}".Replace("'", "'\\''");
                    string mk = _cfg.WrapRemoteCmd($"mkdir -p '{safe}'", asRoot, hostAlias);
                    var r = _cfg.RunSshTool("ssh.exe", $"-F \"{_cfg.SshConfigPath}\" {hostAlias} \"{mk}\"", hostAlias);
                    ok = r.ExitCode == 0; err = r.Error;
                }
                else if (action == "delete")
                {
                    string safe = remotePath.Replace("'", "'\\''");
                    string rm = _cfg.WrapRemoteCmd($"rm -rf '{safe}'", asRoot, hostAlias);
                    var r = _cfg.RunSshTool("ssh.exe", $"-F \"{_cfg.SshConfigPath}\" {hostAlias} \"{rm}\"", hostAlias);
                    ok = r.ExitCode == 0; err = r.Error;
                }

                if (!ok && !string.IsNullOrWhiteSpace(err))
                    PostToWeb(new { type = "log", message = $"SFTP error ({action}): {err.Trim()}" });

                PostToWeb(new { type = "sftpActionDone", reqId, action, ok });
            });
        }

        private void HandleExport(bool allKeys)
        {
            using var sfd = new SaveFileDialog
            {
                Filter = "Pontish Backup (*.zip)|*.zip",
                FileName = $"pontish-backup-{DateTime.Now:yyyy-MM-dd}.zip"
            };
            if (sfd.ShowDialog(this) == DialogResult.OK)
            {
                _cfg.ExportBundle(sfd.FileName, allKeys, _tunnels.AllHosts);
                PostToWeb(new { type = "log", message = $"Exported: {sfd.FileName}" });
            }
        }

        private void HandleImport()
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "Pontish Backup (*.zip)|*.zip|SSH config (*.*)|*.*"
            };
            if (ofd.ShowDialog(this) == DialogResult.OK)
            {
                _cfg.ImportBundle(ofd.FileName);
                _tunnels.ReloadConfig();
                RefreshTunnelStates();
                PostToWeb(new { type = "log", message = $"Imported: {ofd.FileName}" });
            }
        }

        private void CleanupAll()
        {
            _pollTimer.Stop();
            _tunnels.StopAll();
            foreach (var kv in _terminals)
                kv.Value.Dispose();
            _terminals.Clear();
        }

        private static Icon CreateStatusIcon(bool active)
        {
            using var bmp = new Bitmap(32, 32);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            Color c = active ? Color.FromArgb(16, 185, 129) : Color.FromArgb(6, 182, 212);
            using var pen = new Pen(c, 2.5f);
            using var fill = new SolidBrush(Color.FromArgb(15, 22, 41));
            using var dot = new SolidBrush(c);
            g.FillEllipse(fill, 3, 3, 26, 26);
            g.DrawEllipse(pen, 3, 3, 26, 26);
            g.FillEllipse(dot, 11, 11, 10, 10);
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}
