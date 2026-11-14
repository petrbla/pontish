using System;
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
        public string SettingsPath { get; }

        public Dictionary<string, Dictionary<string, string>> I18n { get; } = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        public string CurrentLang { get; set; } = "en";

        public Dictionary<string, string> SavedPasswords { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> SavedSudoPasswords { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public SshConfigService()
        {
            AppBaseDir = AppContext.BaseDirectory;
            SshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
            SshConfigPath = Path.Combine(SshDir, "config");
            SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pontish", "settings.json");

            string sysLang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            CurrentLang = sysLang == "cs" ? "cs" : "en";

            LoadLanguages();
            LoadSettings();
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
                if (cfg != null && !string.IsNullOrWhiteSpace(cfg.Language) && I18n.ContainsKey(cfg.Language.ToLowerInvariant()))
                    CurrentLang = cfg.Language.ToLowerInvariant();
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
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new AppSettings { Language = CurrentLang }), Encoding.UTF8);
            }
            catch { }
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
            var psi = new ProcessStartInfo(exeName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            if (!string.IsNullOrEmpty(hostAlias) && SavedPasswords.TryGetValue(hostAlias, out string pass) && !string.IsNullOrEmpty(pass) && File.Exists(askPass))
            {
                psi.EnvironmentVariables["SSH_ASKPASS"] = askPass;
                psi.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "force";
                psi.EnvironmentVariables["PONTISH_PASS"] = pass;
                psi.EnvironmentVariables["DISPLAY"] = "dummy:0";
            }

            using var proc = Process.Start(psi);
            string outStr = proc.StandardOutput.ReadToEnd();
            string errStr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
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
            string safeTarget = (targetPath ?? "~").Replace("'", "'\\''");
            string rawCmd = targetPath == "~"
                ? "cd ~ && pwd && LC_ALL=C ls -la --group-directories-first"
                : $"cd '{safeTarget}' && pwd && LC_ALL=C ls -la --group-directories-first";
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

            return (true, realPwd, entries, "");
        }

        public void ExportBundle(string destZip, bool includeAllKeys, List<SshHostConfig> hosts)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "pontish_exp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                if (File.Exists(SshConfigPath))
                    File.Copy(SshConfigPath, Path.Combine(tempDir, "config"), true);

                if (includeAllKeys && Directory.Exists(SshDir))
                {
                    var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "config", "known_hosts", "known_hosts.old", "askpass.exe" };
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

                if (File.Exists(destZip)) File.Delete(destZip);
                ZipFile.CreateFromDirectory(tempDir, destZip);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        public void ImportBundle(string srcPath)
        {
            if (!Directory.Exists(SshDir)) Directory.CreateDirectory(SshDir);
            if (File.Exists(SshConfigPath))
                File.Copy(SshConfigPath, SshConfigPath + ".bak." + DateTime.Now.ToString("yyyyMMddHHmmss"), true);

            if (srcPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "pontish_imp_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    ZipFile.ExtractToDirectory(srcPath, tempDir);
                    foreach (string f in Directory.GetFiles(tempDir))
                    {
                        string fn = Path.GetFileName(f);
                        if (fn.Equals("askpass.exe", StringComparison.OrdinalIgnoreCase)) continue;
                        string dest = Path.Combine(SshDir, fn);
                        File.Copy(f, dest, true);
                        RepairPermissions(dest);
                    }
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
            else
            {
                File.Copy(srcPath, SshConfigPath, true);
                RepairPermissions(SshConfigPath);
            }
        }
    }
}
