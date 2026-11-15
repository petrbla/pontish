using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PontishApp
{
    public class MainForm : Form
    {
        private const string AppVersion = "0.1.0";
        private const string GithubRepo = "petrbla/pontish";
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        private readonly SshConfigService _cfg;
        private readonly TunnelManager _tunnels;
        private readonly ConcurrentDictionary<string, ConPtyTerminal> _terminals = new ConcurrentDictionary<string, ConPtyTerminal>();
        private readonly ConcurrentDictionary<string, TermAuthTracker> _termAuth = new ConcurrentDictionary<string, TermAuthTracker>();

        private sealed class TermAuthTracker
        {
            public string HostAlias;
            public bool AwaitingPass;
            public bool JustSubmittedPass;
            public bool AwaitingSudoPass;
            public bool JustSubmittedSudo;
            public bool Authenticated;
            public string Tail = "";
            public string CwdTail = "";
            public string LastCwd = "";
            public readonly StringBuilder PassBuf = new StringBuilder();
            public readonly StringBuilder SudoPassBuf = new StringBuilder();
        }
        private readonly WebView2 _webView;
        private readonly NotifyIcon _trayIcon;
        private readonly ContextMenuStrip _trayMenu;
        private readonly System.Windows.Forms.Timer _pollTimer;
        private bool _allowClose;
        private bool _webReady;
        private bool? _lastAnyActive;
        private FormWindowState _lastWindowState;
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
            MinimumSize = new Size(920, 620);
            ApplySavedWindowBounds();
            _lastWindowState = WindowState;
            BackColor = Color.FromArgb(241, 245, 249);

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
                DefaultBackgroundColor = Color.FromArgb(241, 245, 249)
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
                EnableDarkTitleBar(false);
                await InitWebViewAsync();
            };

            ResizeEnd += (s, e) => SaveWindowBounds();
            SizeChanged += (s, e) =>
            {
                if (IsHandleCreated && WindowState != _lastWindowState)
                {
                    _lastWindowState = WindowState;
                    if (WindowState != FormWindowState.Minimized)
                        SaveWindowBounds();
                }
            };

            FormClosing += (s, e) =>
            {
                SaveWindowBounds();
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

        private void ApplySavedWindowBounds()
        {
            int w = _cfg.WindowWidth.GetValueOrDefault(1180);
            int h = _cfg.WindowHeight.GetValueOrDefault(780);
            if (w < MinimumSize.Width) w = 1180;
            if (h < MinimumSize.Height) h = 780;
            Size = new Size(w, h);

            if (_cfg.WindowX.HasValue && _cfg.WindowY.HasValue)
            {
                var rect = new Rectangle(_cfg.WindowX.Value, _cfg.WindowY.Value, w, h);
                bool onScreen = Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(rect));
                if (onScreen)
                {
                    StartPosition = FormStartPosition.Manual;
                    Location = new Point(_cfg.WindowX.Value, _cfg.WindowY.Value);
                }
                else
                {
                    StartPosition = FormStartPosition.CenterScreen;
                }
            }
            else
            {
                StartPosition = FormStartPosition.CenterScreen;
            }

            if (_cfg.WindowMaximized)
                WindowState = FormWindowState.Maximized;
        }

        private void SaveWindowBounds()
        {
            if (!IsHandleCreated || WindowState == FormWindowState.Minimized) return;
            Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (bounds.Width >= MinimumSize.Width && bounds.Height >= MinimumSize.Height)
            {
                _cfg.WindowWidth = bounds.Width;
                _cfg.WindowHeight = bounds.Height;
                _cfg.WindowX = bounds.X;
                _cfg.WindowY = bounds.Y;
            }
            _cfg.WindowMaximized = WindowState == FormWindowState.Maximized;
            _cfg.SaveSettings();
        }

        private void EnableDarkTitleBar(bool isDark = true)
        {
            try
            {
                int dark = isDark ? 1 : 0;
                DwmSetWindowAttribute(Handle, 20, ref dark, 4);
                int cap = isDark ? 0x001B110B : 0x00FFFFFF; // #0B111B or #FFFFFF (BGR)
                DwmSetWindowAttribute(Handle, 35, ref cap, 4);
                int txt = isDark ? 0x00FFF0E6 : 0x002A170F; // #E6F0FF or #0F172A (BGR)
                DwmSetWindowAttribute(Handle, 36, ref txt, 4);
            }
            catch { }
        }

        private void RestoreFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized)
                WindowState = _cfg.WindowMaximized ? FormWindowState.Maximized : FormWindowState.Normal;
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
                await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", "{\"cacheDisabled\":true}");

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
                string json = JsonSerializer.Serialize(payload, _jsonOpts);
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
                _trayIcon.Icon = anyActive ? _iconActive : _iconIdle;
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

            var langs = _cfg.I18n.Keys
                .OrderBy(k => k.Equals("en", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
                .Select(k => new
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
                i18nFallback = _cfg.I18n.TryGetValue("en", out var enDict) ? enDict : new Dictionary<string, string>(),
                autoReconnect = _tunnels.AutoReconnect,
                sshConfigPath = _cfg.SshConfigPath,
                sshConfigExists = File.Exists(_cfg.SshConfigPath),
                persistPasswords = _cfg.PersistPasswords,
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
                        if (!string.IsNullOrEmpty(text))
                        {
                            Clipboard.SetText(text);
                            PostToWeb(new { type = "log", message = $"Copied: {text}" });
                        }
                        break;

                    case "termPaste":
                        {
                            string pid = root.GetProperty("id").GetString();
                            string clip = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                            if (!string.IsNullOrEmpty(clip))
                                PostToWeb(new { type = "termPasteText", id = pid, text = clip });
                        }
                        break;

                    case "forgetPasswords":
                        _cfg.ClearAllPasswords();
                        SendStateToWeb();
                        PostToWeb(new { type = "log", message = "Saved passwords cleared." });
                        break;

                    case "setPersistPasswords":
                        _cfg.SetPersistPasswords(root.TryGetProperty("enabled", out var ppEn) && ppEn.GetBoolean());
                        SendStateToWeb();
                        break;

                    case "checkUpdate":
                        HandleCheckUpdate(root.GetProperty("reqId").GetString());
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
                        HandleExport(
                            root.TryGetProperty("allKeys", out var ak) && ak.GetBoolean(),
                            root.TryGetProperty("password", out var epw) ? epw.GetString() : null,
                            root.TryGetProperty("reqId", out var erq) ? erq.GetString() : null);
                        break;

                    case "importBundle":
                        HandleImport(
                            root.TryGetProperty("filePath", out var ifp) ? ifp.GetString() : null,
                            root.TryGetProperty("password", out var ipw) ? ipw.GetString() : null,
                            root.TryGetProperty("reqId", out var irq) ? irq.GetString() : null);
                        break;

                    case "createSshConfig":
                        {
                            bool created = _cfg.EnsureSshConfigExists();
                            _tunnels.ReloadConfig();
                            RefreshTunnelStates();
                            SendStateToWeb();
                            if (created)
                                PostToWeb(new { type = "log", message = $"Created {_cfg.SshConfigPath}" });
                        }
                        break;

                    case "pickIdentityFile":
                        {
                            string reqId = root.GetProperty("reqId").GetString();
                            using var ofd = new OpenFileDialog
                            {
                                InitialDirectory = Directory.Exists(_cfg.SshDir) ? _cfg.SshDir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                Filter = "All files (*.*)|*.*"
                            };
                            string chosen = "";
                            if (ofd.ShowDialog(this) == DialogResult.OK)
                            {
                                chosen = ofd.FileName;
                                if (chosen.StartsWith(_cfg.SshDir, StringComparison.OrdinalIgnoreCase))
                                    chosen = "~/.ssh/" + Path.GetFileName(chosen);
                            }
                            PostToWeb(new { type = "identityFilePicked", reqId, path = chosen });
                        }
                        break;

                    case "saveHostConfig":
                        {
                            string reqId = root.GetProperty("reqId").GetString();
                            try
                            {
                                string orig = root.TryGetProperty("originalAlias", out var oa) ? oa.GetString() : "";
                                var cfg = new SshHostConfig
                                {
                                    HostAlias = root.GetProperty("hostAlias").GetString() ?? "",
                                    GroupComment = root.TryGetProperty("groupComment", out var gc) ? gc.GetString() ?? "" : "",
                                    HostName = root.TryGetProperty("hostName", out var hn) ? hn.GetString() ?? "" : "",
                                    Port = root.TryGetProperty("port", out var pt) ? pt.GetInt32() : 22,
                                    User = root.TryGetProperty("user", out var us) ? us.GetString() ?? "" : "",
                                    IdentityFile = root.TryGetProperty("identityFile", out var idf) ? idf.GetString() ?? "" : ""
                                };
                                if (root.TryGetProperty("forwards", out var fws) && fws.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var f in fws.EnumerateArray())
                                    {
                                        cfg.Forwards.Add(new SshForward
                                        {
                                            Name = f.TryGetProperty("name", out var fwNm) ? fwNm.GetString() ?? "" : "",
                                            LocalPort = f.TryGetProperty("localPort", out var fwLp) ? fwLp.GetInt32() : 0,
                                            RemoteHost = f.TryGetProperty("remoteHost", out var fwRh) ? fwRh.GetString() ?? "127.0.0.1" : "127.0.0.1",
                                            RemotePort = f.TryGetProperty("remotePort", out var fwRp) ? fwRp.GetInt32() : 0
                                        });
                                    }
                                }
                                _cfg.SaveHostConfig(orig, cfg);
                                _tunnels.ReloadConfig();
                                RefreshTunnelStates();
                                PostToWeb(new { type = "cfgOpResult", reqId, ok = true });
                            }
                            catch (Exception ex)
                            {
                                PostToWeb(new { type = "cfgOpResult", reqId, ok = false, error = ex.Message });
                            }
                        }
                        break;

                    case "deleteHostConfig":
                        {
                            string reqId = root.GetProperty("reqId").GetString();
                            try
                            {
                                string alias = root.GetProperty("hostAlias").GetString() ?? "";
                                _cfg.DeleteHostConfig(alias);
                                _tunnels.ReloadConfig();
                                RefreshTunnelStates();
                                PostToWeb(new { type = "cfgOpResult", reqId, ok = true });
                            }
                            catch (Exception ex)
                            {
                                PostToWeb(new { type = "cfgOpResult", reqId, ok = false, error = ex.Message });
                            }
                        }
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
                        {
                            string tid = root.GetProperty("id").GetString();
                            string inData = root.GetProperty("data").GetString();
                            if (_termAuth.TryGetValue(tid, out var tr) && !string.IsNullOrEmpty(inData))
                            {
                                if (tr.AwaitingPass)
                                {
                                    foreach (char ch in inData)
                                    {
                                        if (ch == '\r' || ch == '\n')
                                        {
                                            if (tr.PassBuf.Length > 0)
                                            {
                                                _cfg.SavedPasswords[tr.HostAlias] = tr.PassBuf.ToString();
                                                _cfg.SaveVaultIfEnabled();
                                                tr.JustSubmittedPass = true;
                                                tr.PassBuf.Clear();
                                            }
                                            tr.AwaitingPass = false;
                                        }
                                        else if (ch == '\b' || ch == '\x7f')
                                        {
                                            if (tr.PassBuf.Length > 0) tr.PassBuf.Length--;
                                        }
                                        else if (!char.IsControl(ch))
                                        {
                                            tr.PassBuf.Append(ch);
                                        }
                                    }
                                }
                                else if (tr.AwaitingSudoPass)
                                {
                                    foreach (char ch in inData)
                                    {
                                        if (ch == '\r' || ch == '\n')
                                        {
                                            if (tr.SudoPassBuf.Length > 0)
                                            {
                                                _cfg.SavedSudoPasswords[tr.HostAlias] = tr.SudoPassBuf.ToString();
                                                _cfg.SaveVaultIfEnabled();
                                                tr.JustSubmittedSudo = true;
                                                tr.SudoPassBuf.Clear();
                                            }
                                            tr.AwaitingSudoPass = false;
                                        }
                                        else if (ch == '\b' || ch == '\x7f')
                                        {
                                            if (tr.SudoPassBuf.Length > 0) tr.SudoPassBuf.Length--;
                                        }
                                        else if (!char.IsControl(ch))
                                        {
                                            tr.SudoPassBuf.Append(ch);
                                        }
                                    }
                                }
                            }
                            if (_terminals.TryGetValue(tid, out var tIn))
                                tIn.WriteInput(inData);
                        }
                        break;

                    case "termResize":
                        if (_terminals.TryGetValue(root.GetProperty("id").GetString(), out var tRes))
                            tRes.Resize((short)root.GetProperty("cols").GetInt32(), (short)root.GetProperty("rows").GetInt32());
                        break;

                    case "termClose":
                        {
                            string cid = root.GetProperty("id").GetString();
                            _termAuth.TryRemove(cid, out _);
                            if (_terminals.TryRemove(cid, out var tClose))
                                tClose.Dispose();
                        }
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

                    case "sshSysStats":
                        HandleSshSysStats(root);
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

            if (t.UserRequestedRun || (t.BridgeProcess != null && !t.BridgeProcess.HasExited) || _tunnels.IsTunnelListening(t))
            {
                _tunnels.StopTunnel(t);
                RefreshTunnelStates();
                return;
            }

            if (!string.IsNullOrEmpty(password))
            {
                if (remember)
                {
                    _cfg.SavedPasswords[hostAlias] = password;
                    _cfg.SaveVaultIfEnabled();
                }
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
            TermAuthTracker tracker = mode == "ssh" && !string.IsNullOrEmpty(hostAlias)
                ? (_termAuth[id] = new TermAuthTracker { HostAlias = hostAlias })
                : null;

            term.OnOutput += (tid, data) =>
            {
                if (tracker != null)
                {
                    if (!tracker.Authenticated)
                    {
                        string clean = Regex.Replace(data, @"\x1B\[[0-9;?]*[A-Za-z]|\x1B\][^\x07]*\x07", "");
                        tracker.Tail += clean;
                        if (tracker.Tail.Length > 240) tracker.Tail = tracker.Tail.Substring(tracker.Tail.Length - 240);

                        if (!tracker.AwaitingPass &&
                            (tracker.Tail.Contains("password:", StringComparison.OrdinalIgnoreCase) ||
                             tracker.Tail.Contains("passphrase", StringComparison.OrdinalIgnoreCase)))
                        {
                            tracker.AwaitingPass = true;
                            tracker.PassBuf.Clear();
                            tracker.Tail = "";
                        }
                        else if (!tracker.AwaitingPass &&
                                 (tracker.Tail.Contains("$ ") || tracker.Tail.Contains("# ") || tracker.Tail.Contains("Last login:")))
                        {
                            tracker.JustSubmittedPass = false;
                            tracker.Authenticated = true;
                            PostToWeb(new { type = "termAuthenticated", id = tid, hostAlias = tracker.HostAlias });
                        }
                        else if (tracker.Tail.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
                                 tracker.Tail.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase))
                        {
                            if (_cfg.SavedPasswords.Remove(tracker.HostAlias))
                                _cfg.SaveVaultIfEnabled();
                            tracker.Tail = "";
                        }
                    }

                    if (tracker.Authenticated)
                    {
                        string detectedCwd = null;
                        var osc7 = Regex.Match(data, @"\x1B\]7;file://[^/]*(/[^\x07\x1B]+)");
                        if (osc7.Success)
                        {
                            detectedCwd = Uri.UnescapeDataString(osc7.Groups[1].Value);
                        }
                        else
                        {
                            var oscTitle = Regex.Match(data, @"\x1B\][02];[^:\x07\x1B]+:([~\/][^\x07\x1B]*)");
                            if (oscTitle.Success)
                                detectedCwd = oscTitle.Groups[1].Value.Trim();
                        }

                        string cleanCwd = Regex.Replace(data, @"\x1B\[[0-9;?]*[A-Za-z]|\x1B\][^\x07]*\x07", "");
                        tracker.CwdTail += cleanCwd;
                        if (tracker.CwdTail.Length > 320)
                            tracker.CwdTail = tracker.CwdTail.Substring(tracker.CwdTail.Length - 320);

                        if (!tracker.AwaitingSudoPass &&
                            (tracker.CwdTail.Contains("[sudo]", StringComparison.OrdinalIgnoreCase) ||
                             tracker.CwdTail.Contains("password:", StringComparison.OrdinalIgnoreCase) ||
                             tracker.CwdTail.Contains("heslo", StringComparison.OrdinalIgnoreCase)))
                        {
                            tracker.AwaitingSudoPass = true;
                            tracker.SudoPassBuf.Clear();
                            tracker.CwdTail = "";
                        }
                        else if (tracker.JustSubmittedSudo && tracker.CwdTail.Contains("#"))
                        {
                            tracker.JustSubmittedSudo = false;
                            PostToWeb(new { type = "termSudoReady", id = tid, hostAlias = tracker.HostAlias });
                        }

                        if (string.IsNullOrEmpty(detectedCwd))
                        {
                            var pm = Regex.Match(tracker.CwdTail, @"(?:^|[\r\n])[^\s@:\r\n]+@[^\s:\r\n]+:([~\/][^\r\n$#]*?)[$#]\s*$");
                            if (pm.Success)
                                detectedCwd = pm.Groups[1].Value.Trim();
                        }

                        if (!string.IsNullOrEmpty(detectedCwd) && detectedCwd != tracker.LastCwd)
                        {
                            tracker.LastCwd = detectedCwd;
                            PostToWeb(new { type = "termCwdChanged", id = tid, hostAlias = tracker.HostAlias, path = detectedCwd });
                        }
                    }
                }
                PostToWeb(new { type = "termOutput", id = tid, data });
            };
            term.OnExited += (tid, code) =>
            {
                _termAuth.TryRemove(tid, out _);
                _terminals.TryRemove(tid, out _);
                PostToWeb(new { type = "termExit", id = tid, code });
            };

            Dictionary<string, string> extraEnv = null;
            string sshPassOpt = "";
            if (mode == "ssh" && !string.IsNullOrEmpty(hostAlias))
            {
                string askPass = _cfg.EnsureAskPassExe();
                if (_cfg.SavedPasswords.TryGetValue(hostAlias, out string savedPass) && !string.IsNullOrEmpty(savedPass) && File.Exists(askPass))
                {
                    extraEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["SSH_ASKPASS"] = askPass,
                        ["SSH_ASKPASS_REQUIRE"] = "force",
                        ["PONTISH_PASS"] = savedPass,
                        ["DISPLAY"] = "dummy:0"
                    };
                    sshPassOpt = "-o NumberOfPasswordPrompts=1 ";
                }
            }

            string cmdLine = mode switch
            {
                "powershell" => "powershell.exe -NoLogo",
                "cmd" => "cmd.exe",
                _ => asRoot
                    ? $"ssh.exe {sshPassOpt}-t -F \"{_cfg.SshConfigPath}\" {hostAlias} \"sudo -i\""
                    : $"ssh.exe {sshPassOpt}-F \"{_cfg.SshConfigPath}\" {hostAlias}"
            };

            _terminals[id] = term;
            term.Start(cmdLine, null, cols, rows, extraEnv);
        }

        private readonly ConcurrentDictionary<string, bool> _sysStatsInFlight = new ConcurrentDictionary<string, bool>();

        private void HandleCheckUpdate(string reqId)
        {
            Task.Run(async () =>
            {
                string releasesPage = $"https://github.com/{GithubRepo}/releases";
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{GithubRepo}/releases/latest");
                    req.Headers.UserAgent.ParseAdd($"Pontish/{AppVersion}");
                    req.Headers.Accept.ParseAdd("application/vnd.github+json");

                    using var resp = await _http.SendAsync(req);
                    if (resp.StatusCode == HttpStatusCode.NotFound)
                    {
                        using var tagReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{GithubRepo}/tags");
                        tagReq.Headers.UserAgent.ParseAdd($"Pontish/{AppVersion}");
                        using var tagResp = await _http.SendAsync(tagReq);
                        if (tagResp.IsSuccessStatusCode)
                        {
                            using var tagDoc = JsonDocument.Parse(await tagResp.Content.ReadAsStringAsync());
                            if (tagDoc.RootElement.ValueKind == JsonValueKind.Array && tagDoc.RootElement.GetArrayLength() > 0)
                            {
                                string tag = (tagDoc.RootElement[0].GetProperty("name").GetString() ?? "").TrimStart('v', 'V');
                                bool upd = CompareVersions(tag, AppVersion) > 0;
                                PostToWeb(new
                                {
                                    type = "updateCheckResult",
                                    reqId,
                                    ok = true,
                                    currentVersion = AppVersion,
                                    latestVersion = tag,
                                    hasUpdate = upd,
                                    noRelease = false,
                                    releaseUrl = releasesPage,
                                    downloadUrl = releasesPage
                                });
                                return;
                            }
                        }

                        PostToWeb(new
                        {
                            type = "updateCheckResult",
                            reqId,
                            ok = true,
                            currentVersion = AppVersion,
                            latestVersion = AppVersion,
                            hasUpdate = false,
                            noRelease = true,
                            releaseUrl = releasesPage,
                            downloadUrl = releasesPage
                        });
                        return;
                    }

                    resp.EnsureSuccessStatusCode();
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    var root = doc.RootElement;
                    string tagVer = (root.TryGetProperty("tag_name", out var tn) ? tn.GetString() : "")?.TrimStart('v', 'V') ?? AppVersion;
                    string htmlUrl = root.TryGetProperty("html_url", out var hu) ? hu.GetString() : releasesPage;
                    string downloadUrl = htmlUrl;

                    if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var a in assets.EnumerateArray())
                        {
                            string name = a.TryGetProperty("name", out var an) ? an.GetString() : "";
                            string dl = a.TryGetProperty("browser_download_url", out var adu) ? adu.GetString() : "";
                            if (!string.IsNullOrEmpty(name) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(dl))
                            {
                                downloadUrl = dl;
                                break;
                            }
                        }
                    }

                    bool hasUpdate = CompareVersions(tagVer, AppVersion) > 0;
                    PostToWeb(new
                    {
                        type = "updateCheckResult",
                        reqId,
                        ok = true,
                        currentVersion = AppVersion,
                        latestVersion = tagVer,
                        hasUpdate,
                        noRelease = false,
                        releaseUrl = htmlUrl,
                        downloadUrl
                    });
                }
                catch (Exception ex)
                {
                    PostToWeb(new
                    {
                        type = "updateCheckResult",
                        reqId,
                        ok = false,
                        currentVersion = AppVersion,
                        error = ex.Message
                    });
                }
            });
        }

        private static int CompareVersions(string a, string b)
        {
            if (Version.TryParse(a, out var va) && Version.TryParse(b, out var vb))
                return va.CompareTo(vb);
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private void HandleSshSysStats(JsonElement root)
        {
            string reqId = root.GetProperty("reqId").GetString();
            string hostAlias = root.GetProperty("hostAlias").GetString();
            if (string.IsNullOrEmpty(hostAlias) || !_sysStatsInFlight.TryAdd(hostAlias, true))
            {
                PostToWeb(new { type = "sshSysStats", reqId, ok = false });
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    var stats = _cfg.GetRemoteSysStats(hostAlias);
                    PostToWeb(new { type = "sshSysStats", reqId, stats });
                }
                catch
                {
                    PostToWeb(new { type = "sshSysStats", reqId, stats = new { ok = false } });
                }
                finally
                {
                    _sysStatsInFlight.TryRemove(hostAlias, out _);
                }
            });
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
            string password = root.TryGetProperty("password", out var pw) ? pw.GetString() : null;

            Task.Run(() =>
            {
                if (!string.IsNullOrEmpty(password))
                {
                    _cfg.SavedPasswords[hostAlias] = password;
                    _cfg.SaveVaultIfEnabled();
                }

                var res = _cfg.ListRemote(hostAlias, path, asRoot);
                bool needPass = !res.Ok && !string.IsNullOrEmpty(res.Error) &&
                    (res.Error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
                     res.Error.Contains("passphrase", StringComparison.OrdinalIgnoreCase) ||
                     res.Error.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase));

                if (needPass && !string.IsNullOrEmpty(password))
                {
                    _cfg.SavedPasswords.Remove(hostAlias);
                    _cfg.SaveVaultIfEnabled();
                }

                if (!res.Ok)
                    PostToWeb(new { type = "log", message = $"SFTP ({hostAlias}): {res.Error}" });

                PostToWeb(new { type = "sftpRemote", reqId, ok = res.Ok, needPassword = needPass, path = res.RealPath, entries = res.Entries, error = res.Error });
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
                {
                    _cfg.SavedSudoPasswords[hostAlias] = password;
                    _cfg.SaveVaultIfEnabled();
                }

                string cmd = _cfg.WrapRemoteCmd("true", true, hostAlias);
                var res = _cfg.RunSshTool("ssh.exe", $"-F \"{_cfg.SshConfigPath}\" {hostAlias} \"{cmd}\"", hostAlias);
                if (res.ExitCode != 0 && !string.IsNullOrEmpty(password))
                {
                    _cfg.SavedSudoPasswords.Remove(hostAlias);
                    _cfg.SaveVaultIfEnabled();
                }

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

        private void HandleExport(bool allKeys, string password, string reqId)
        {
            bool encrypted = !string.IsNullOrEmpty(password);
            using var sfd = new SaveFileDialog
            {
                Filter = encrypted
                    ? "Pontish Encrypted Backup (*.pontish)|*.pontish"
                    : "Pontish Backup (*.zip)|*.zip",
                FileName = $"pontish-backup-{DateTime.Now:yyyy-MM-dd}.{(encrypted ? "pontish" : "zip")}"
            };
            if (sfd.ShowDialog(this) == DialogResult.OK)
            {
                _cfg.ExportBundle(sfd.FileName, allKeys, _tunnels.AllHosts, password);
                PostToWeb(new { type = "log", message = $"Exported: {sfd.FileName}" });
                if (!string.IsNullOrEmpty(reqId))
                    PostToWeb(new { type = "exportResult", reqId, ok = true, path = sfd.FileName });
            }
            else if (!string.IsNullOrEmpty(reqId))
            {
                PostToWeb(new { type = "exportResult", reqId, cancelled = true });
            }
        }

        private void HandleImport(string filePath, string password, string reqId)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                using var ofd = new OpenFileDialog
                {
                    Filter = "Pontish Backup (*.pontish;*.zip)|*.pontish;*.zip|SSH config (*.*)|*.*"
                };
                if (ofd.ShowDialog(this) != DialogResult.OK)
                {
                    if (!string.IsNullOrEmpty(reqId))
                        PostToWeb(new { type = "importResult", reqId, cancelled = true });
                    return;
                }
                filePath = ofd.FileName;
            }

            if (SshConfigService.IsEncryptedBackupFile(filePath) && string.IsNullOrEmpty(password))
            {
                if (!string.IsNullOrEmpty(reqId))
                    PostToWeb(new { type = "importResult", reqId, needPassword = true, filePath });
                return;
            }

            try
            {
                _cfg.ImportBundle(filePath, password);
                _tunnels.ReloadConfig();
                RefreshTunnelStates();
                PostToWeb(new { type = "log", message = $"Imported: {filePath}" });
                if (!string.IsNullOrEmpty(reqId))
                    PostToWeb(new { type = "importResult", reqId, ok = true });
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                if (!string.IsNullOrEmpty(reqId))
                    PostToWeb(new { type = "importResult", reqId, badPassword = true, filePath });
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(reqId))
                    PostToWeb(new { type = "importResult", reqId, ok = false, error = ex.Message });
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
            const int s = 32;
            const float sc = s / 256f;
            using var bmp = new Bitmap(s, s, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            using var shieldBrush = new SolidBrush(Color.FromArgb(255, 14, 116, 214));
            using var shield = new GraphicsPath();
            shield.AddBezier(5.12f * sc, 37.12f * sc, 40.96f * sc, 21.76f * sc, 102.4f * sc, 7.68f * sc, 128f * sc, 3.84f * sc);
            shield.AddBezier(128f * sc, 3.84f * sc, 153.6f * sc, 7.68f * sc, 215.04f * sc, 21.76f * sc, 250.88f * sc, 37.12f * sc);
            shield.AddBezier(250.88f * sc, 37.12f * sc, 250.88f * sc, 128f * sc, 225.28f * sc, 213.76f * sc, 128f * sc, 252.16f * sc);
            shield.AddBezier(128f * sc, 252.16f * sc, 30.72f * sc, 213.76f * sc, 5.12f * sc, 128f * sc, 5.12f * sc, 37.12f * sc);
            shield.CloseFigure();
            g.FillPath(shieldBrush, shield);

            using var lockPath = new GraphicsPath(FillMode.Alternate);
            lockPath.StartFigure();
            lockPath.AddBezier(81.231f * sc, 72.993f * sc, 81.231f * sc, 45.953f * sc, 102.79f * sc, 24.032f * sc, 129.385f * sc, 24.032f * sc);
            lockPath.AddBezier(129.385f * sc, 24.032f * sc, 155.98f * sc, 24.032f * sc, 177.539f * sc, 45.953f * sc, 177.539f * sc, 72.993f * sc);
            lockPath.AddLine(177.539f * sc, 72.993f * sc, 177.539f * sc, 82.446f * sc);
            lockPath.AddLine(177.539f * sc, 82.446f * sc, 183.086f * sc, 82.446f * sc);
            lockPath.AddBezier(183.086f * sc, 82.446f * sc, 191.451f * sc, 82.446f * sc, 198.232f * sc, 90.874f * sc, 198.232f * sc, 101.27f * sc);
            lockPath.AddLine(198.232f * sc, 101.27f * sc, 198.232f * sc, 183.415f * sc);
            lockPath.AddBezier(198.232f * sc, 183.415f * sc, 198.232f * sc, 193.812f * sc, 191.451f * sc, 202.24f * sc, 183.086f * sc, 202.24f * sc);
            lockPath.AddLine(183.086f * sc, 202.24f * sc, 75.684f * sc, 202.24f * sc);
            lockPath.AddBezier(75.684f * sc, 202.24f * sc, 67.319f * sc, 202.24f * sc, 60.538f * sc, 193.812f * sc, 60.538f * sc, 183.415f * sc);
            lockPath.AddLine(60.538f * sc, 183.415f * sc, 60.538f * sc, 101.27f * sc);
            lockPath.AddBezier(60.538f * sc, 101.27f * sc, 60.538f * sc, 90.874f * sc, 67.319f * sc, 82.446f * sc, 75.684f * sc, 82.446f * sc);
            lockPath.AddLine(75.684f * sc, 82.446f * sc, 81.231f * sc, 82.446f * sc);
            lockPath.CloseFigure();

            lockPath.StartFigure();
            lockPath.AddBezier(129.385f * sc, 43.232f * sc, 113.394f * sc, 43.232f * sc, 100.431f * sc, 56.557f * sc, 100.431f * sc, 72.993f * sc);
            lockPath.AddLine(100.431f * sc, 72.993f * sc, 100.431f * sc, 82.446f * sc);
            lockPath.AddLine(100.431f * sc, 82.446f * sc, 158.339f * sc, 82.446f * sc);
            lockPath.AddLine(158.339f * sc, 82.446f * sc, 158.339f * sc, 72.993f * sc);
            lockPath.AddBezier(158.339f * sc, 72.993f * sc, 158.339f * sc, 56.557f * sc, 145.376f * sc, 43.232f * sc, 129.385f * sc, 43.232f * sc);
            lockPath.CloseFigure();
            g.FillPath(Brushes.White, lockPath);

            if (active)
            {
                using var badgeBg = new SolidBrush(Color.White);
                using var badgeFg = new SolidBrush(Color.FromArgb(16, 185, 129));
                g.FillEllipse(badgeBg, 20.5f, 20.5f, 10.5f, 10.5f);
                g.FillEllipse(badgeFg, 22.0f, 22.0f, 7.5f, 7.5f);
            }

            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}
