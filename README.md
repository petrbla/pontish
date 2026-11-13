# Pontish 🌉

*🇬🇧 English documentation | 🇨🇿 [Česká verze (Czech)](README.cs.md)*

**Pontish** is a lightweight, modern, and secure open-source SSH bridge and port-forwarding manager for Windows.

It lives in your Windows System Tray, offers a sleek dark UI, monitors and automatically reconnects your tunnels, and seamlessly integrates with your existing OpenSSH configuration.

---

## ✨ Features

* **Sleek Dark Interface:** Clean overview of all your remote servers, active bridges, and forwarded local ports.
* **System Tray Resident:** Color-coded status icon and quick toggle controls directly from the clock tray context menu.
* **Auto-Reconnect Engine:** Automatically detects connection drops (network change, PC sleep/wake) and brings your tunnels back up.
* **Silent & Secure Password Handling:** Utilizes the standard OpenSSH `SSH_ASKPASS` mechanism for silent background authentication without annoying terminal popup windows.
* **Native `~/.ssh/config` Support:** Zero redundant configuration. Pontish directly reads and parses your standard OpenSSH config file.
* **Backup & Restore:** Easily export and import your configurations and keys in a protected `.zip` bundle, including automatic NTFS permission repairs.

---

## ⚙️ Configuration (`~/.ssh/config`)

Pontish does not require a custom database or proprietary settings. It directly reads your existing OpenSSH client configuration file:
* **File location:** `C:\Users\<YourUsername>\.ssh\config` (or `~/.ssh/config`).
* Click the **"Upravit ~/.ssh/config"** button at the bottom of the Pontish window to open it directly in your default text editor.

### How Pontish Parses Configs
Pontish scans your configuration and displays a card for every `Host` entry that contains at least one `LocalForward` directive:

* `Host <alias>` – The server/bridge name displayed on the UI card.
* `HostName <IP_or_domain>` – Remote server IP or hostname.
* `User <username>` – SSH user (e.g. `debian`, `ubuntu`, `root`).
* `Port <number>` – SSH port (defaults to 22).
* `IdentityFile <path>` – Path to your private key (e.g. `~/.ssh/id_ed25519`). If omitted, Pontish will prompt for your password upon connection and pass it securely.
* `LocalForward <localPort> <remoteTarget>` – Port forwarding definition (e.g. `LocalForward 8080 127.0.0.1:80`).

### 💡 Smart UI Comments
You can use standard comments (`#`) directly in your `config` to enrich the Pontish UI:

1. **Card Groups:** A comment formatted as `# === Group Name ===` creates clean visual section headers in the application.
2. **Port Labels & Clickable URLs:** Adding `# Description -> URL` right above a `LocalForward` line creates a clickable link on the card for instant browser access.

### 📋 Example `~/.ssh/config`:

```ssh-config
# === Production Servers ===

Host web-prod
    HostName 198.51.100.25
    User debian
    Port 22
    IdentityFile ~/.ssh/id_ed25519
    # WordPress Admin -> http://localhost:8080
    LocalForward 8080 127.0.0.1:80
    # MariaDB Database
    LocalForward 3306 127.0.0.1:3306

# === Development & Automation ===

Host n8n-dev
    HostName 203.0.113.10
    User ubuntu
    Port 2222
    # N8N Automation -> http://localhost:5678
    LocalForward 5678 127.0.0.1:5678
    # Redis Cache
    LocalForward 6379 127.0.0.1:6379
```

---

## 🚀 Requirements

* **Operating System:** Windows 10 / 11 (64-bit)
* **OpenSSH Client:** Standard Windows feature (Settings -> Optional features -> OpenSSH Client)
* **.NET Framework:** 4.7.2 or newer (preinstalled with modern Windows)

---

## 🛠️ Building from Source

The repository is kept 100% clean and transparent, containing only readable source code and zero precompiled binaries.

To compile the launcher and installer from source, run PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

The script will automatically:
1. Compile `Pontish.exe` (launcher) from `Launcher.cs`.
2. Compile `askpass.exe` (password helper) from `askpass.cs`.
3. Package the application payload.
4. Compile the self-extracting GUI installer `Instalovat-Pontish.exe` from `Installer.cs`.

---

## 📦 Installation

1. Run the generated installer: `Instalovat-Pontish.exe`.
2. The installer will:
   * Install Pontish to `%LOCALAPPDATA%\Programs\Pontish`.
   * Create shortcuts on your Desktop and in the Start Menu.
   * Verify and fix NTFS security permissions on your `~/.ssh/` directory if needed.

---

## 📄 License

This project is open-source software licensed under the [MIT License](LICENSE).
