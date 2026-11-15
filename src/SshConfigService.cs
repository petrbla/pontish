using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PontishApp
{
    public class SshConfigService
    {
        public string AppBaseDir { get; }
        public string SshDir { get; }
        public string SshConfigPath { get; }
        public string VaultPath { get; }
        public string SettingsPath { get; }

        public Dictionary<string, Dictionary<string, string>> I18n { get; } = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        public string CurrentLang { get; set; } = "en";
        public bool PersistPasswords { get; private set; }
        public int? WindowWidth { get; set; }
        public int? WindowHeight { get; set; }
        public int? WindowX { get; set; }
        public int? WindowY { get; set; }
        public bool WindowMaximized { get; set; }

        public Dictionary<string, string> SavedPasswords { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> SavedSudoPasswords { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public SshConfigService()
        {
            AppBaseDir = AppContext.BaseDirectory;
            SshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
            SshConfigPath = Path.Combine(SshDir, "config");
            VaultPath = Path.Combine(SshDir, "pontish.vault");
            SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pontish", "settings.json");

            LoadLanguages();
            string sysLang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            CurrentLang = I18n.ContainsKey(sysLang) ? sysLang : (I18n.ContainsKey("en") ? "en" : "cs");
            LoadSettings();
            if (PersistPasswords) LoadVault();
        }

        private void LoadLanguages()
        {
            string langDir = Path.Combine(AppBaseDir, "lang");
            if (!Directory.Exists(langDir)) return;

            foreach (string file in Directory.GetFiles(langDir, "*.json"))
            {
                try
                {
                    string code = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    string json = File.ReadAllText(file, Encoding.UTF8);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (dict != null && dict.Count > 0)
                        I18n[code] = dict;
                }
                catch { }
            }
        }

        private void LoadSettings()
        {
            if (!File.Exists(SettingsPath)) return;
            try
            {
                string json = File.ReadAllText(SettingsPath, Encoding.UTF8);
                var cfg = JsonSerializer.Deserialize<AppSettings>(json);
                if (cfg != null)
                {
                    if (!string.IsNullOrWhiteSpace(cfg.Language) && I18n.ContainsKey(cfg.Language.ToLowerInvariant()))
                        CurrentLang = cfg.Language.ToLowerInvariant();
                    PersistPasswords = cfg.PersistPasswords;
                    WindowWidth = cfg.WindowWidth;
                    WindowHeight = cfg.WindowHeight;
                    WindowX = cfg.WindowX;
                    WindowY = cfg.WindowY;
                    WindowMaximized = cfg.WindowMaximized;
                }
            }
            catch { }
        }

        public void SaveSettings()
        {
            try
            {
                string dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new AppSettings
                {
                    Language = CurrentLang,
                    PersistPasswords = PersistPasswords,
                    WindowWidth = WindowWidth,
                    WindowHeight = WindowHeight,
                    WindowX = WindowX,
                    WindowY = WindowY,
                    WindowMaximized = WindowMaximized
                }), Encoding.UTF8);
            }
            catch { }
        }

        public void SetPersistPasswords(bool enabled)
        {
            PersistPasswords = enabled;
            SaveSettings();
            if (enabled) SaveVaultIfEnabled();
            else DeleteVault();
        }

        public void LoadVault()
        {
            if (!File.Exists(VaultPath)) return;
            try
            {
                byte[] encrypted = File.ReadAllBytes(VaultPath);
                byte[] plain = CryptoHelper.UnprotectDpapi(encrypted);
                var payload = JsonSerializer.Deserialize<VaultPayload>(Encoding.UTF8.GetString(plain));
                if (payload == null) return;

                if (payload.Passwords != null)
                {
                    foreach (var kv in payload.Passwords)
                    {
                        if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                            SavedPasswords[kv.Key] = kv.Value;
                    }
                }
                if (payload.SudoPasswords != null)
                {
                    foreach (var kv in payload.SudoPasswords)
                    {
                        if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                            SavedSudoPasswords[kv.Key] = kv.Value;
                    }
                }
            }
            catch { }
        }

        public void SaveVaultIfEnabled()
        {
            if (!PersistPasswords) return;
            try
            {
                if (!Directory.Exists(SshDir)) Directory.CreateDirectory(SshDir);
                var payload = new VaultPayload
                {
                    Passwords = new Dictionary<string, string>(SavedPasswords, StringComparer.OrdinalIgnoreCase),
                    SudoPasswords = new Dictionary<string, string>(SavedSudoPasswords, StringComparer.OrdinalIgnoreCase)
                };
                byte[] plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
                byte[] encrypted = CryptoHelper.ProtectDpapi(plain);
                File.WriteAllBytes(VaultPath, encrypted);
                RepairPermissions(VaultPath);
            }
            catch { }
        }

        public void DeleteVault()
        {
            try
            {
                if (File.Exists(VaultPath)) File.Delete(VaultPath);
            }
            catch { }
        }

        public void ClearAllPasswords()
        {
            SavedPasswords.Clear();
            SavedSudoPasswords.Clear();
            DeleteVault();
        }

        public string EnsureAskPassExe()
        {
            if (!Directory.Exists(SshDir)) Directory.CreateDirectory(SshDir);
            string target = Path.Combine(SshDir, "askpass.exe");
            string source = Path.Combine(AppBaseDir, "askpass.exe");
            if (!File.Exists(target) && File.Exists(source))
            {
                try { File.Copy(source, target, true); } catch { }
            }
            return target;
        }

        public List<SshHostConfig> ReadConfig()
        {
            var result = new List<SshHostConfig>();
            if (!File.Exists(SshConfigPath)) return result;

            SshHostConfig current = null;
            string lastComment = "";
            string[] lines = File.ReadAllLines(SshConfigPath, Encoding.UTF8);

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                if (line.StartsWith("#"))
                {
                    string c = line.TrimStart('#').Trim();
                    if (!string.IsNullOrEmpty(c)) lastComment = c;
                    continue;
                }

                var hostMatch = Regex.Match(line, @"^Host\s+(.+)$", RegexOptions.IgnoreCase);
                if (hostMatch.Success)
                {
                    if (current != null && !string.IsNullOrEmpty(current.HostAlias) && current.HostAlias != "*")
                        result.Add(current);

                    string alias = hostMatch.Groups[1].Value.Trim().Split(' ')[0];
                    current = new SshHostConfig
                    {
                        HostAlias = alias,
                        GroupComment = lastComment
                    };
                    lastComment = "";
                    continue;
                }

                if (current == null) continue;

                var hn = Regex.Match(line, @"^HostName\s+(.+)$", RegexOptions.IgnoreCase);
                if (hn.Success) { current.HostName = hn.Groups[1].Value.Trim(); continue; }

                var pt = Regex.Match(line, @"^Port\s+(\d+)", RegexOptions.IgnoreCase);
                if (pt.Success && int.TryParse(pt.Groups[1].Value, out int pNum)) { current.Port = pNum; continue; }

                var us = Regex.Match(line, @"^User\s+(.+)$", RegexOptions.IgnoreCase);
                if (us.Success) { current.User = us.Groups[1].Value.Trim(); continue; }

                var idf = Regex.Match(line, @"^IdentityFile\s+(.+)$", RegexOptions.IgnoreCase);
                if (idf.Success) { current.IdentityFile = idf.Groups[1].Value.Trim(); continue; }

                var lf = Regex.Match(line, @"^LocalForward\s+(\d+)\s+([^\s:]+):(\d+)", RegexOptions.IgnoreCase);
                if (lf.Success)
                {
                    int localPort = int.Parse(lf.Groups[1].Value);
                    string rHost = lf.Groups[2].Value;
                    int rPort = int.Parse(lf.Groups[3].Value);
                    string name = !string.IsNullOrEmpty(lastComment) ? lastComment : $"Port {localPort}";
                    lastComment = "";
                    bool isDb = rPort == 3306 || rPort == 5432 || rPort == 6379 || rPort == 27017 ||
                                Regex.IsMatch(name, @"MariaDB|MySQL|Postgres|Redis|Mongo|DB|Database", RegexOptions.IgnoreCase);

                    current.Forwards.Add(new SshForward
                    {
                        Name = name,
                        LocalPort = localPort,
                        RemoteHost = rHost,
                        RemotePort = rPort,
                        IsDb = isDb
                    });
                }
            }

            if (current != null && !string.IsNullOrEmpty(current.HostAlias) && current.HostAlias != "*")
                result.Add(current);

            return result;
        }

        public static string ResolveSshPath(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) return "";
            string p = rawPath.Trim().Trim('"', '\'');
            if (p.StartsWith("~/") || p.StartsWith("~\\"))
                p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.Substring(2));
            else if (p == "~")
                p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return p.Replace('/', '\\');
        }

        public static void RepairPermissions(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
            try
            {
                string user = WindowsIdentity.GetCurrent().Name;
                var psi = new ProcessStartInfo("icacls.exe", $"\"{filePath}\" /inheritance:r /grant:r \"{user}:(F)\" \"SYSTEM:(F)\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(3000);
            }
            catch { }
        }

        public (int ExitCode, string Output, string Error) RunSshTool(string exeName, string arguments, string hostAlias = null)
        {
            string askPass = EnsureAskPassExe();
            string pass = null;
            bool hasPass = !string.IsNullOrEmpty(hostAlias) && SavedPasswords.TryGetValue(hostAlias, out pass) && !string.IsNullOrEmpty(pass) && File.Exists(askPass);
            string finalArgs = hasPass
                ? $"-o NumberOfPasswordPrompts=1 -o ConnectTimeout=8 {arguments}"
                : $"-o BatchMode=yes -o ConnectTimeout=8 {arguments}";

            var psi = new ProcessStartInfo(exeName, finalArgs)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            if (hasPass)
            {
                psi.EnvironmentVariables["SSH_ASKPASS"] = askPass;
                psi.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "force";
                psi.EnvironmentVariables["PONTISH_PASS"] = pass;
                psi.EnvironmentVariables["DISPLAY"] = "dummy:0";
            }

            using var proc = Process.Start(psi);
            try { proc.StandardInput.Close(); } catch { }
            string outStr = proc.StandardOutput.ReadToEnd();
            string errStr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (hasPass && proc.ExitCode != 0 && !string.IsNullOrEmpty(errStr) &&
                (errStr.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
                 errStr.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)))
            {
                if (SavedPasswords.Remove(hostAlias))
                    SaveVaultIfEnabled();
            }

            return (proc.ExitCode, outStr, errStr);
        }

        public string WrapRemoteCmd(string cmd, bool asRoot, string hostAlias)
        {
            if (!asRoot) return cmd;
            string escCmd = cmd.Replace("'", "'\\''");
            if (!string.IsNullOrEmpty(hostAlias) && SavedSudoPasswords.TryGetValue(hostAlias, out string sudoPass) && !string.IsNullOrEmpty(sudoPass))
            {
                string escPass = sudoPass.Replace("'", "'\\''");
                return $"printf '%s\\n' '{escPass}' | sudo -S -p '' sh -c '{escCmd}'";
            }
            return $"sudo -n sh -c '{escCmd}'";
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1048576) return $"{bytes / 1024.0:N1} KB";
            if (bytes < 1073741824) return $"{bytes / 1048576.0:N1} MB";
            return $"{bytes / 1073741824.0:N2} GB";
        }

        public List<SftpEntry> ListLocal(string path)
        {
            var list = new List<SftpEntry>();
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            string full = Path.GetFullPath(path);
            var parent = Directory.GetParent(full);
            if (parent != null)
            {
                list.Add(new SftpEntry { Name = "[..]", Path = parent.FullName, Type = "up", Info = "<DIR>" });
            }

            try
            {
                foreach (var dir in Directory.GetDirectories(full))
                {
                    var di = new DirectoryInfo(dir);
                    list.Add(new SftpEntry { Name = di.Name, Path = di.FullName, Type = "dir", Info = "<DIR>" });
                }
                foreach (var file in Directory.GetFiles(full))
                {
                    var fi = new FileInfo(file);
                    list.Add(new SftpEntry { Name = fi.Name, Path = fi.FullName, Type = "file", SizeBytes = fi.Length, SizeText = FormatSize(fi.Length), Info = fi.Extension });
                }
            }
            catch { }

            return list;
        }

        public (bool Ok, string RealPath, List<SftpEntry> Entries, string Error) ListRemote(string hostAlias, string targetPath, bool asRoot)
        {
            string rawCmd;
            if (string.IsNullOrEmpty(targetPath) || targetPath == "~")
            {
                rawCmd = "cd ~ && pwd && LC_ALL=C ls -la --group-directories-first";
            }
            else if (targetPath.StartsWith("~/"))
            {
                string sub = targetPath.Substring(2).Replace("'", "'\\''");
                rawCmd = $"cd ~/'{sub}' && pwd && LC_ALL=C ls -la --group-directories-first";
            }
            else
            {
                string safeTarget = targetPath.Replace("'", "'\\''");
                rawCmd = $"cd '{safeTarget}' && pwd && LC_ALL=C ls -la --group-directories-first";
            }
            string cmd = WrapRemoteCmd(rawCmd, asRoot, hostAlias);
            var res = RunSshTool("ssh.exe", $"-F \"{SshConfigPath}\" {hostAlias} \"{cmd}\"", hostAlias);
            if (res.ExitCode != 0)
                return (false, targetPath, new List<SftpEntry>(), res.Error.Trim());

            var lines = res.Output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return (false, targetPath, new List<SftpEntry>(), "Empty output");

            string realPwd = lines[0].Trim();
            var entries = new List<SftpEntry>();

            if (realPwd != "/")
            {
                string upParent = Regex.Replace(realPwd.TrimEnd('/'), @"/[^/]+$", "");
                if (string.IsNullOrEmpty(upParent)) upParent = "/";
                entries.Add(new SftpEntry { Name = "[..]", Path = upParent, Type = "up", Info = "<DIR>" });
            }

            for (int i = 1; i < lines.Length; i++)
            {
                var m = Regex.Match(lines[i], @"^([drwxsStTl-]{10})\s+\d+\s+\S+\s+\S+\s+(\d+)\s+\S+\s+\S+\s+\S+\s+(.+)$");
                if (!m.Success) continue;

                string perms = m.Groups[1].Value;
                long size = long.Parse(m.Groups[2].Value);
                string name = m.Groups[3].Value.Trim();
                if (name == "." || name == "..") continue;
                if (perms.StartsWith("l"))
                {
                    var lm = Regex.Match(name, @"^(.+?)\s+->\s+");
                    if (lm.Success) name = lm.Groups[1].Value;
                }
                bool isDir = perms.StartsWith("d");
                string fullRemote = realPwd == "/" ? $"/{name}" : $"{realPwd.TrimEnd('/')}/{name}";
                entries.Add(new SftpEntry
                {
                    Name = name,
                    Path = fullRemote,
                    Type = isDir ? "dir" : "file",
                    SizeBytes = size,
                    SizeText = isDir ? "" : FormatSize(size),
                    Info = perms
                });
            }

            entries = entries
                .OrderBy(e => e.Type == "up" ? 0 : (e.Type == "dir" ? 1 : 2))
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return (true, realPwd, entries, "");
        }

        private sealed class SysSamplePrev
        {
            public long Total;
            public long Idle;
            public long Rx;
            public long Tx;
            public DateTime At;
        }

        private readonly ConcurrentDictionary<string, SysSamplePrev> _prevSysStats = new ConcurrentDictionary<string, SysSamplePrev>();

        public object GetRemoteSysStats(string hostAlias)
        {
            string remoteCmd = "hostname; grep ^ID= /etc/os-release 2>/dev/null | head -n1; head -n1 /proc/stat; grep -E '^(MemTotal|MemAvailable):' /proc/meminfo; sed 's/^/UP /' /proc/uptime; echo NET_START; cat /proc/net/dev; echo DF_START; df -P -k 2>/dev/null";
            var res = RunSshTool("ssh.exe", $"-F \"{SshConfigPath}\" {hostAlias} \"{remoteCmd}\"", hostAlias);
            if (res.ExitCode != 0 || string.IsNullOrWhiteSpace(res.Output))
                return new { ok = false };

            string host = hostAlias;
            string osId = "linux";
            long cpuTotal = 0, cpuIdle = 0;
            long memTotalKb = 0, memAvailKb = 0;
            long rx = 0, tx = 0;
            double upSec = 0;
            var disks = new List<object>();

            string section = "";
            var lines = res.Output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string ln = lines[i].Trim();
                if (ln == "NET_START") { section = "NET"; continue; }
                if (ln == "DF_START") { section = "DF"; continue; }

                if (section == "NET")
                {
                    int colon = ln.IndexOf(':');
                    if (colon > 0)
                    {
                        string iface = ln.Substring(0, colon).Trim();
                        if (iface != "lo")
                        {
                            var nums = ln.Substring(colon + 1).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (nums.Length >= 9 && long.TryParse(nums[0], out long rxb) && long.TryParse(nums[8], out long txb))
                            {
                                rx += rxb;
                                tx += txb;
                            }
                        }
                    }
                    continue;
                }

                if (section == "DF")
                {
                    if (ln.StartsWith("Filesystem", StringComparison.OrdinalIgnoreCase)) continue;
                    var parts = ln.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 6)
                    {
                        string fs = parts[0];
                        if (fs == "tmpfs" || fs == "devtmpfs" || fs == "udev" || fs == "squashfs" || fs == "none" || fs == "overlay")
                            continue;
                        if (disks.Count < 4)
                            disks.Add(new { mount = parts[5], pct = parts[4] });
                    }
                    continue;
                }

                if (i == 0 && !ln.StartsWith("ID=") && !ln.StartsWith("cpu "))
                {
                    host = ln;
                }
                else if (ln.StartsWith("ID="))
                {
                    osId = ln.Substring(3).Trim('"', '\'', ' ');
                }
                else if (ln.StartsWith("cpu "))
                {
                    var parts = ln.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int j = 1; j < parts.Length && j <= 8; j++)
                    {
                        if (long.TryParse(parts[j], out long v))
                        {
                            cpuTotal += v;
                            if (j == 4 || j == 5) cpuIdle += v;
                        }
                    }
                }
                else if (ln.StartsWith("MemTotal:"))
                {
                    var m = Regex.Match(ln, @"(\d+)");
                    if (m.Success) memTotalKb = long.Parse(m.Groups[1].Value);
                }
                else if (ln.StartsWith("MemAvailable:"))
                {
                    var m = Regex.Match(ln, @"(\d+)");
                    if (m.Success) memAvailKb = long.Parse(m.Groups[1].Value);
                }
                else if (ln.StartsWith("UP "))
                {
                    var parts = ln.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                        double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out upSec);
                }
            }

            var now = DateTime.UtcNow;
            double cpuPct = 0;
            double rxMbps = 0;
            double txMbps = 0;

            if (_prevSysStats.TryGetValue(hostAlias, out var prev))
            {
                long dTotal = cpuTotal - prev.Total;
                long dIdle = cpuIdle - prev.Idle;
                if (dTotal > 0)
                    cpuPct = Math.Clamp((1.0 - (double)dIdle / dTotal) * 100.0, 0, 100);

                double dt = (now - prev.At).TotalSeconds;
                if (dt > 0.2)
                {
                    if (rx >= prev.Rx) rxMbps = (rx - prev.Rx) * 8.0 / dt / 1_000_000.0;
                    if (tx >= prev.Tx) txMbps = (tx - prev.Tx) * 8.0 / dt / 1_000_000.0;
                }
            }

            _prevSysStats[hostAlias] = new SysSamplePrev
            {
                Total = cpuTotal,
                Idle = cpuIdle,
                Rx = rx,
                Tx = tx,
                At = now
            };

            double ramTotalGb = memTotalKb / 1048576.0;
            double ramUsedGb = Math.Max(0, (memTotalKb - memAvailKb) / 1048576.0);

            string upText;
            var ts = TimeSpan.FromSeconds(upSec);
            if (ts.TotalDays >= 1) upText = $"{(int)ts.TotalDays} d";
            else if (ts.TotalHours >= 1) upText = $"{(int)ts.TotalHours}h {ts.Minutes}m";
            else upText = $"{ts.Minutes}m";

            return new
            {
                ok = true,
                host,
                osId,
                cpuPct = Math.Round(cpuPct, 0),
                ramUsedGb = Math.Round(ramUsedGb, 2),
                ramTotalGb = Math.Round(ramTotalGb, 2),
                rxMbps = Math.Round(rxMbps, 2),
                txMbps = Math.Round(txMbps, 2),
                uptime = upText,
                disks
            };
        }

        public void ExportBundle(string destPath, bool includeAllKeys, List<SshHostConfig> hosts, string backupPassword = null)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "pontish_exp_" + Guid.NewGuid().ToString("N"));
            string tempZip = Path.Combine(Path.GetTempPath(), "pontish_exp_" + Guid.NewGuid().ToString("N") + ".zip");
            Directory.CreateDirectory(tempDir);
            try
            {
                if (File.Exists(SshConfigPath))
                    File.Copy(SshConfigPath, Path.Combine(tempDir, "config"), true);

                if (includeAllKeys && Directory.Exists(SshDir))
                {
                    var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "config", "known_hosts", "known_hosts.old", "askpass.exe", "pontish.vault" };
                    foreach (string f in Directory.GetFiles(SshDir))
                    {
                        string fn = Path.GetFileName(f);
                        if (!exclude.Contains(fn))
                            File.Copy(f, Path.Combine(tempDir, fn), true);
                    }
                }
                else
                {
                    foreach (var h in hosts)
                    {
                        string key = ResolveSshPath(h.IdentityFile);
                        if (!string.IsNullOrEmpty(key) && File.Exists(key))
                        {
                            File.Copy(key, Path.Combine(tempDir, Path.GetFileName(key)), true);
                            if (File.Exists(key + ".pub"))
                                File.Copy(key + ".pub", Path.Combine(tempDir, Path.GetFileName(key) + ".pub"), true);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(backupPassword) && (SavedPasswords.Count > 0 || SavedSudoPasswords.Count > 0))
                {
                    var payload = new VaultPayload
                    {
                        Passwords = new Dictionary<string, string>(SavedPasswords, StringComparer.OrdinalIgnoreCase),
                        SudoPasswords = new Dictionary<string, string>(SavedSudoPasswords, StringComparer.OrdinalIgnoreCase)
                    };
                    File.WriteAllText(Path.Combine(tempDir, "pontish-passwords.json"), JsonSerializer.Serialize(payload), new UTF8Encoding(false));
                }

                ZipFile.CreateFromDirectory(tempDir, tempZip);
                if (File.Exists(destPath)) File.Delete(destPath);

                if (!string.IsNullOrEmpty(backupPassword))
                {
                    byte[] zipBytes = File.ReadAllBytes(tempZip);
                    byte[] encBytes = CryptoHelper.EncryptWithPassword(zipBytes, backupPassword);
                    File.WriteAllBytes(destPath, encBytes);
                }
                else
                {
                    File.Copy(tempZip, destPath, true);
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }
        }

        public static bool IsEncryptedBackupFile(string srcPath)
        {
            if (string.IsNullOrWhiteSpace(srcPath) || !File.Exists(srcPath)) return false;
            try
            {
                using var fs = File.OpenRead(srcPath);
                byte[] buf = new byte[64];
                int read = fs.Read(buf, 0, buf.Length);
                if (read < 52) return false;
                return CryptoHelper.IsEncryptedBlob(buf);
            }
            catch { return false; }
        }

        public void ImportBundle(string srcPath, string backupPassword = null)
        {
            if (!Directory.Exists(SshDir)) Directory.CreateDirectory(SshDir);
            if (File.Exists(SshConfigPath))
                File.Copy(SshConfigPath, SshConfigPath + ".bak." + DateTime.Now.ToString("yyyyMMddHHmmss"), true);

            bool isEncrypted = IsEncryptedBackupFile(srcPath);
            if (isEncrypted || srcPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || srcPath.EndsWith(".pontish", StringComparison.OrdinalIgnoreCase))
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "pontish_imp_" + Guid.NewGuid().ToString("N"));
                string tempZip = null;
                Directory.CreateDirectory(tempDir);
                try
                {
                    string zipToExtract = srcPath;
                    if (isEncrypted)
                    {
                        byte[] raw = File.ReadAllBytes(srcPath);
                        byte[] plainZip = CryptoHelper.DecryptWithPassword(raw, backupPassword);
                        tempZip = Path.Combine(Path.GetTempPath(), "pontish_dec_" + Guid.NewGuid().ToString("N") + ".zip");
                        File.WriteAllBytes(tempZip, plainZip);
                        zipToExtract = tempZip;
                    }

                    ZipFile.ExtractToDirectory(zipToExtract, tempDir);
                    foreach (string f in Directory.GetFiles(tempDir))
                    {
                        string fn = Path.GetFileName(f);
                        if (fn.Equals("askpass.exe", StringComparison.OrdinalIgnoreCase) ||
                            fn.Equals("pontish.vault", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (fn.Equals("pontish-passwords.json", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var payload = JsonSerializer.Deserialize<VaultPayload>(File.ReadAllText(f, Encoding.UTF8));
                                if (payload?.Passwords != null)
                                    foreach (var kv in payload.Passwords)
                                        if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                                            SavedPasswords[kv.Key] = kv.Value;
                                if (payload?.SudoPasswords != null)
                                    foreach (var kv in payload.SudoPasswords)
                                        if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                                            SavedSudoPasswords[kv.Key] = kv.Value;
                                SaveVaultIfEnabled();
                            }
                            catch { }
                            continue;
                        }

                        string dest = Path.Combine(SshDir, fn);
                        File.Copy(f, dest, true);
                        RepairPermissions(dest);
                    }
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                    try { if (!string.IsNullOrEmpty(tempZip) && File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                }
            }
            else
            {
                File.Copy(srcPath, SshConfigPath, true);
                RepairPermissions(SshConfigPath);
            }
        }

        public void SaveHostConfig(string originalAlias, SshHostConfig cfg)
        {
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.HostAlias))
                throw new ArgumentException("Host alias is required.");

            if (!Directory.Exists(SshDir)) Directory.CreateDirectory(SshDir);
            if (File.Exists(SshConfigPath))
                File.Copy(SshConfigPath, SshConfigPath + ".bak", true);

            var sections = ParseRawSections(File.Exists(SshConfigPath) ? File.ReadAllLines(SshConfigPath, Encoding.UTF8) : Array.Empty<string>());
            string matchKey = !string.IsNullOrWhiteSpace(originalAlias) ? originalAlias.Trim() : cfg.HostAlias.Trim();
            int idx = sections.FindIndex(s => string.Equals(s.Alias, matchKey, StringComparison.OrdinalIgnoreCase));

            var extraLines = new List<string>();
            if (idx >= 0)
            {
                foreach (string raw in sections[idx].BodyLines)
                {
                    string t = raw.Trim();
                    if (string.IsNullOrEmpty(t) || t.StartsWith("#")) continue;
                    if (Regex.IsMatch(t, @"^(HostName|Port|User|IdentityFile|LocalForward)\b", RegexOptions.IgnoreCase)) continue;
                    extraLines.Add("    " + t);
                }
            }

            var newLines = BuildHostSectionLines(cfg, extraLines);
            if (idx >= 0)
                sections[idx] = new RawConfigSection { Alias = cfg.HostAlias.Trim(), Lines = newLines };
            else
                sections.Add(new RawConfigSection { Alias = cfg.HostAlias.Trim(), Lines = newLines });

            WriteRawSections(sections);
        }

        public void DeleteHostConfig(string hostAlias)
        {
            if (string.IsNullOrWhiteSpace(hostAlias) || !File.Exists(SshConfigPath)) return;
            File.Copy(SshConfigPath, SshConfigPath + ".bak", true);

            var sections = ParseRawSections(File.ReadAllLines(SshConfigPath, Encoding.UTF8));
            sections.RemoveAll(s => string.Equals(s.Alias, hostAlias.Trim(), StringComparison.OrdinalIgnoreCase));
            WriteRawSections(sections);
        }

        private class RawConfigSection
        {
            public string Alias { get; set; } = "";
            public List<string> Lines { get; set; } = new List<string>();
            public List<string> BodyLines { get; set; } = new List<string>();
        }

        private static List<RawConfigSection> ParseRawSections(string[] allLines)
        {
            var sections = new List<RawConfigSection>();
            var preamble = new RawConfigSection();
            RawConfigSection current = null;
            var pendingComments = new List<string>();

            foreach (string raw in allLines)
            {
                string t = raw.Trim();
                if (t.StartsWith("#"))
                {
                    pendingComments.Add(raw);
                    continue;
                }

                var m = Regex.Match(t, @"^Host\s+(.+)$", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    if (current != null) sections.Add(current);
                    else if (preamble.Lines.Count > 0) sections.Add(preamble);

                    current = new RawConfigSection { Alias = m.Groups[1].Value.Trim().Split(' ')[0] };
                    current.Lines.AddRange(pendingComments);
                    pendingComments.Clear();
                    current.Lines.Add(raw);
                    continue;
                }

                if (current == null)
                {
                    if (pendingComments.Count > 0) { preamble.Lines.AddRange(pendingComments); pendingComments.Clear(); }
                    preamble.Lines.Add(raw);
                }
                else
                {
                    if (pendingComments.Count > 0) { current.Lines.AddRange(pendingComments); current.BodyLines.AddRange(pendingComments); pendingComments.Clear(); }
                    current.Lines.Add(raw);
                    current.BodyLines.Add(raw);
                }
            }

            if (pendingComments.Count > 0)
            {
                if (current != null) current.Lines.AddRange(pendingComments);
                else preamble.Lines.AddRange(pendingComments);
            }

            if (current != null) sections.Add(current);
            else if (preamble.Lines.Count > 0) sections.Add(preamble);

            return sections;
        }

        private static List<string> BuildHostSectionLines(SshHostConfig cfg, List<string> extraLines)
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(cfg.GroupComment))
                lines.Add($"# {cfg.GroupComment.Trim()}");

            lines.Add($"Host {cfg.HostAlias.Trim()}");
            if (!string.IsNullOrWhiteSpace(cfg.HostName))
                lines.Add($"    HostName {cfg.HostName.Trim()}");
            if (cfg.Port > 0 && cfg.Port != 22)
                lines.Add($"    Port {cfg.Port}");
            else if (cfg.Port == 22)
                lines.Add("    Port 22");
            if (!string.IsNullOrWhiteSpace(cfg.User))
                lines.Add($"    User {cfg.User.Trim()}");
            if (!string.IsNullOrWhiteSpace(cfg.IdentityFile))
                lines.Add($"    IdentityFile {cfg.IdentityFile.Trim()}");

            foreach (string ex in extraLines)
                lines.Add(ex);

            if (cfg.Forwards != null)
            {
                foreach (var fw in cfg.Forwards)
                {
                    if (fw.LocalPort <= 0 || fw.RemotePort <= 0) continue;
                    string rHost = string.IsNullOrWhiteSpace(fw.RemoteHost) ? "127.0.0.1" : fw.RemoteHost.Trim();
                    if (!string.IsNullOrWhiteSpace(fw.Name))
                        lines.Add($"    # {fw.Name.Trim()}");
                    lines.Add($"    LocalForward {fw.LocalPort} {rHost}:{fw.RemotePort}");
                }
            }

            return lines;
        }

        private void WriteRawSections(List<RawConfigSection> sections)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < sections.Count; i++)
            {
                var sec = sections[i].Lines;
                int start = 0;
                int end = sec.Count - 1;
                while (start <= end && string.IsNullOrWhiteSpace(sec[start])) start++;
                while (end >= start && string.IsNullOrWhiteSpace(sec[end])) end--;
                if (start > end) continue;

                if (sb.Length > 0) sb.AppendLine();
                for (int j = start; j <= end; j++)
                    sb.AppendLine(sec[j]);
            }

            File.WriteAllText(SshConfigPath, sb.ToString(), new UTF8Encoding(false));
            RepairPermissions(SshConfigPath);
        }

        public bool EnsureSshConfigExists()
        {
            Directory.CreateDirectory(SshDir);
            if (File.Exists(SshConfigPath)) return false;

            var sb = new StringBuilder();
            sb.AppendLine("# Pontish SSH Config");
            sb.AppendLine("Host *");
            sb.AppendLine("    ServerAliveInterval 30");
            sb.AppendLine("    TCPKeepAlive yes");
            sb.AppendLine("    StrictHostKeyChecking accept-new");
            File.WriteAllText(SshConfigPath, sb.ToString(), new UTF8Encoding(false));
            RepairPermissions(SshConfigPath);
            return true;
        }
    }
}
