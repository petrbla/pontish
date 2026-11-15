using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

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
            var psi = new ProcessStartInfo("ssh.exe", $"-F \"{_cfg.SshConfigPath}\" -N -o ExitOnForwardFailure=yes -o NumberOfPasswordPrompts=1 -o ServerAliveInterval=30 -o ServerAliveCountMax=3 -o ConnectTimeout=15 {t.HostAlias}")
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
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    string line = e.Data.Trim();
                    Log($"[{t.HostAlias}] {line}");

                    if (line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("Too many authentication failures", StringComparison.OrdinalIgnoreCase))
                    {
                        t.UserRequestedRun = false;
                        t.IsConnecting = false;
                        t.NextReconnectTime = DateTime.MinValue;
                        if (_cfg.SavedPasswords.Remove(t.HostAlias))
                            _cfg.SaveVaultIfEnabled();
                        Log($"[{t.HostAlias}] Overeni selhalo - auto-reconnect zastaven a ulozene heslo odebrano (ochrana proti fail2ban).");
                    }
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

        public bool IsTunnelListening(SshHostConfig t)
        {
            if (t == null || t.Forwards.Count == 0) return false;
            var ports = GetListeningTcpPorts();
            return t.Forwards.All(f => ports.Contains(f.LocalPort));
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, uint reserved = 0);

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;
            public uint localPort;
            public uint remoteAddr;
            public uint remotePort;
            public int owningPid;
        }

        private static void StopExternalTunnelProcesses(SshHostConfig t)
        {
            if (t == null || t.Forwards.Count == 0) return;
            var targetPorts = new HashSet<int>(t.Forwards.Select(f => f.LocalPort));
            var pidsToKill = new HashSet<int>();

            int buffSize = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref buffSize, true, 2, 3);
            if (buffSize > 0)
            {
                IntPtr buff = Marshal.AllocHGlobal(buffSize);
                try
                {
                    if (GetExtendedTcpTable(buff, ref buffSize, true, 2, 3) == 0)
                    {
                        int numEntries = Marshal.ReadInt32(buff);
                        IntPtr rowPtr = IntPtr.Add(buff, 4);
                        int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                        for (int i = 0; i < numEntries; i++)
                        {
                            var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                            int port = IPAddress.NetworkToHostOrder((short)(row.localPort & 0xFFFF)) & 0xFFFF;
                            if (targetPorts.Contains(port) && row.owningPid > 4)
                                pidsToKill.Add(row.owningPid);
                            rowPtr = IntPtr.Add(rowPtr, rowSize);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buff);
                }
            }

            foreach (int pid in pidsToKill)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (p.ProcessName.Equals("ssh", StringComparison.OrdinalIgnoreCase))
                        p.Kill(true);
                }
                catch { }
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
            StopExternalTunnelProcesses(t);
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
