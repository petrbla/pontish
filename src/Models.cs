using System.Collections.Generic;
using System.Diagnostics;

namespace PontishApp
{
    public class SshForward
    {
        public string Name { get; set; } = "";
        public int LocalPort { get; set; }
        public string RemoteHost { get; set; } = "";
        public int RemotePort { get; set; }
        public bool IsDb { get; set; }
        public bool IsListening { get; set; }
    }

    public class SshHostConfig
    {
        public string HostAlias { get; set; } = "";
        public string HostName { get; set; } = "";
        public int Port { get; set; } = 22;
        public string User { get; set; } = "";
        public string IdentityFile { get; set; } = "";
        public string GroupComment { get; set; } = "";
        public List<SshForward> Forwards { get; set; } = new List<SshForward>();

        // Runtime bridge state
        public Process BridgeProcess { get; set; }
        public bool UserRequestedRun { get; set; }
        public bool WasConnected { get; set; }
        public bool IsConnecting { get; set; }
        public System.DateTime ConnectStartTime { get; set; }
        public int ReconnectAttempts { get; set; }
        public System.DateTime NextReconnectTime { get; set; }
    }

    public class SftpEntry
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string Type { get; set; } = "file"; // "up", "dir", "file"
        public long SizeBytes { get; set; }
        public string SizeText { get; set; } = "";
        public string Info { get; set; } = "";
    }

    public class AppSettings
    {
        public string Language { get; set; } = "en";
        public bool PersistPasswords { get; set; } = false;
        public int? WindowWidth { get; set; }
        public int? WindowHeight { get; set; }
        public int? WindowX { get; set; }
        public int? WindowY { get; set; }
        public bool WindowMaximized { get; set; } = false;
    }

    public class VaultPayload
    {
        public Dictionary<string, string> Passwords { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> SudoPasswords { get; set; } = new Dictionary<string, string>();
    }
}
