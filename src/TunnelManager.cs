using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;

namespace PontishApp
{
    public class TunnelManager
    {
        private readonly SshConfigService _cfg;
        public List<SshHostConfig> Tunnels { get; private set; } = new List<SshHostConfig>();
        public List<SshHostConfig> AllHosts { get; private set; } = new List<SshHostConfig>();
        public bool AutoReconnect { get; set; } = true;

        public event Action<string> OnLog;

        public TunnelManager(SshConfigService cfg)
        {
            _cfg = cfg;
        }

        private void Log(string msg) => OnLog?.Invoke(msg);

        public void ReloadConfig()
        {
            StopAll();
            AllHosts = _cfg.ReadConfig();
            Tunnels = AllHosts.Where(h => h.Forwards.Count > 0).ToList();
        }

        public static HashSet<int> GetListeningTcpPorts()
        {
            var set = new HashSet<int>();
            try
            {
                var props = IPGlobalProperties.GetIPGlobalProperties();
                foreach (var ep in props.GetActiveTcpListeners())
                    set.Add(ep.Port);
            }
            catch { }
            return set;
        }

        public static bool IsKeyEncrypted(string keyPath)
        {
            if (string.IsNullOrEmpty(keyPath) || !File.Exists(keyPath)) return false;
            try
            {
                string content = File.ReadAllText(keyPath);
                if (content.Contains("ENCRYPTED")) return true;
                if (content.Contains("OPENSSH PRIVATE KEY"))
                {
                    string b64 = string.Join("", content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(l => !l.StartsWith("-----")));
                    byte[] raw = Convert.FromBase64String(b64);
                    string ascii = System.Text.Encoding.ASCII.GetString(raw);
                    return !ascii.Contains("none");
                }
            }
            catch { }
            return false;
        }

        public void StartTunnel(SshHostConfig t, string password = null, bool isAutoReconnect = false)
        {
            if (t == null) return;
            StopTunnelProcessOnly(t);

            t.UserRequestedRun = true;
            t.IsConnecting = true;
            t.WasConnected = false;
            t.ConnectStartTime = DateTime.Now;
            if (!isAutoReconnect) t.ReconnectAttempts = 0;

            string askPassExe = _cfg.EnsureAskPassExe();
            var psi = new ProcessStartInfo("ssh.exe", $"-F \"{_cfg.SshConfigPath}\" -N -o ExitOnForwardFailure=yes -o ServerAliveInterval=30 -o ServerAliveCountMax=3 -o ConnectTimeout=15 {t.HostAlias}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            string usePass = password ?? (_cfg.SavedPasswords.TryGetValue(t.HostAlias, out string sp) ? sp : null);
            if (!string.IsNullOrEmpty(usePass) && File.Exists(askPassExe))
            {
                psi.EnvironmentVariables["SSH_ASKPASS"] = askPassExe;
                psi.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "force";
                psi.EnvironmentVariables["PONTISH_PASS"] = usePass;
                psi.EnvironmentVariables["DISPLAY"] = "dummy:0";
            }

            Log($"Pripojuji SSH most '{t.HostAlias}' ({t.User}@{t.HostName}:{t.Port})...");

            try
            {
                var proc = Process.Start(psi);
                t.BridgeProcess = proc;

                proc.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        Log($"[{t.HostAlias}] {e.Data.Trim()}");
                };
                proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                t.IsConnecting = false;
                t.UserRequestedRun = false;
                Log($"CHYBA '{t.HostAlias}': {ex.Message}");
            }
        }

        private void StopTunnelProcessOnly(SshHostConfig t)
        {
            if (t?.BridgeProcess != null)
            {
                try
                {
                    if (!t.BridgeProcess.HasExited)
                        t.BridgeProcess.Kill(true);
                }
                catch { }
                t.BridgeProcess = null;
            }
        }

        public void StopTunnel(SshHostConfig t)
        {
            if (t == null) return;
            t.UserRequestedRun = false;
            t.IsConnecting = false;
            t.WasConnected = false;
            t.NextReconnectTime = DateTime.MinValue;
            StopTunnelProcessOnly(t);
            Log($"Most '{t.HostAlias}' odpojen.");
        }

        public void StopAll()
        {
            foreach (var t in Tunnels)
            {
                if (t.BridgeProcess != null || t.UserRequestedRun)
                    StopTunnel(t);
            }
        }

        public int UpdateStates()
        {
            var ports = GetListeningTcpPorts();
            int activeCount = 0;

            foreach (var t in Tunnels)
            {
                if (t.NextReconnectTime != DateTime.MinValue && DateTime.Now >= t.NextReconnectTime)
                {
                    t.NextReconnectTime = DateTime.MinValue;
                    if (t.UserRequestedRun && AutoReconnect)
                    {
                        t.ReconnectAttempts++;
                        Log($"Most '{t.HostAlias}' vypadl. Pokus o obnoveni {t.ReconnectAttempts}/3...");
                        StartTunnel(t, null, true);
                        continue;
                    }
                }

                if (t.UserRequestedRun && t.BridgeProcess != null && t.BridgeProcess.HasExited && t.NextReconnectTime == DateTime.MinValue)
                {
                    if (AutoReconnect && t.ReconnectAttempts < 3)
                    {
                        t.NextReconnectTime = DateTime.Now.AddSeconds(3);
                    }
                    else
                    {
                        t.UserRequestedRun = false;
                        t.IsConnecting = false;
                    }
                }

                int listenCount = 0;
                foreach (var fw in t.Forwards)
                {
                    fw.IsListening = ports.Contains(fw.LocalPort);
                    if (fw.IsListening) listenCount++;
                }

                bool isFull = t.Forwards.Count > 0 && listenCount == t.Forwards.Count;
                if (isFull)
                {
                    if (!t.WasConnected)
                    {
                        t.WasConnected = true;
                        t.IsConnecting = false;
                        t.ReconnectAttempts = 0;
                        Log($"Most '{t.HostAlias}' je AKTIVNI ({listenCount}/{t.Forwards.Count} portu nasloucha).");
                    }
                    activeCount++;
                }
                else if (t.IsConnecting && (DateTime.Now - t.ConnectStartTime).TotalSeconds > 25)
                {
                    t.IsConnecting = false;
                }
            }

            return activeCount;
        }
    }
}
