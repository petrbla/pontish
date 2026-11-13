# Pontish 🌉

*🇨🇿 Česká dokumentace | 🇬🇧 [English documentation](README.md)*

**Pontish** je moderní, odlehčený a bezpečný open-source správce SSH mostů a port-forwarding tunelů k VPS serverům pro systém Windows.

Běží v oznamovací oblasti (System Tray), poskytuje přehledné tmavé grafické rozhraní, automaticky hlídá a obnovuje spojení a usnadňuje práci s více servery současně.

---

## ✨ Hlavní funkce

* **Přehledné tmavé rozhraní:** Okamžitý přehled o stavu všech mostů a lokálních portů.
* **Běh na pozadí v liště (System Tray):** Barevná stavová ikona a rychlé ovládání jednotlivých mostů přímo z kontextového menu u hodin.
* **Automatické obnovení spojení (Auto-reconnect):** Hlídá aktivitu tunelů a při výpadku sítě nebo spánku PC je automaticky znovu nahodí.
* **Bezpečné předávání hesel:** Využívá mechanismus `SSH_ASKPASS` vestavěný v OpenSSH pro tiché a bezpečné přihlášení bez vyskakovacích konzolových oken.
* **Plná integrace s `~/.ssh/config`:** Nemusíte znovu konfigurovat své servery – Pontish přímo čte a respektuje váš standardní OpenSSH konfigurák.
* **Export & Import záloh:** Snadný přenos nastavení a klíčů mezi počítači v chráněném `.zip` balíčku s automatickou opravou NTFS oprávnění.

---

## ⚙️ Konfigurace (~/.ssh/config)

Pontish nevyžaduje žádnou vlastní složitou databázi – čte přímo standardní soubor OpenSSH klienta:
* **Umístění souboru:** `C:\Users\<VášUživatel>\.ssh\config` (zkráceně `~/.ssh/config`).
* V okně Pontish najdete dole odkaz **"Upravit ~/.ssh/config"**, který soubor ihned otevře v editoru.

### Jak Pontish konfiguraci čte:
Aplikace v GUI zobrazí každou sekci `Host`, která obsahuje alespoň jedno přesměrování portu (`LocalForward`):

* `Host <alias>` – název serveru / mostu zobrazený na kartě.
* `HostName <IP_nebo_doména>` – adresa serveru.
* `User <jméno>` – uživatelské jméno pro SSH (např. `debian`, `root`, `ubuntu`).
* `Port <číslo>` – SSH port (výchozí je 22).
* `IdentityFile <cesta>` – cesta k privátnímu SSH klíči (např. `~/.ssh/id_ed25519`). Pokud chybí, Pontish se při spuštění zeptá na heslo a bezpečně ho předá.
* `LocalForward <lokálníPort> <vzdálenýCíl>` – přesměrování portu (např. `LocalForward 8080 127.0.0.1:80`).

### 💡 Chytré komentáře pro hezčí rozhraní:
Pontish umí z běžných komentářů (`#`) v souboru `config` vykouzlit interaktivní prvky:

1. **Skupiny karet:** Řádek ve formátu `# === Název skupiny ===` rozdělí karty mostů do přehledných sekcí.
2. **Popisek portu a klikací odkaz:** Komentář nad řádkem `LocalForward` ve formátu `# Popis -> URL` vytvoří na kartě klikací odkaz pro okamžité otevření v prohlížeči.

### 📋 Vzorový příklad `~/.ssh/config`:

```ssh-config
# === Produkční servery ===

Host web-produkce
    HostName 198.51.100.25
    User debian
    Port 22
    IdentityFile ~/.ssh/id_ed25519
    # WordPress Administrace -> http://localhost:8080
    LocalForward 8080 127.0.0.1:80
    # MariaDB Databáze
    LocalForward 3306 127.0.0.1:3306

# === Vývoj & Automatizace ===

Host n8n-dev
    HostName 203.0.113.10
    User ubuntu
    Port 2222
    # N8N automatizace -> http://localhost:5678
    LocalForward 5678 127.0.0.1:5678
    # Redis cache
    LocalForward 6379 127.0.0.1:6379
```

---

## 🚀 Požadavky

* **Operační systém:** Windows 10 / 11 (64-bit)
* **OpenSSH Client:** Součást Windows (v Nastavení -> Volitelné součásti -> OpenSSH klient)
* **.NET Framework:** 4.7.2 nebo novější (součást Windows)

---

## 🛠️ Sestavení ze zdrojových kódů (Build)

Projekt je navržen tak, aby repozitář obsahoval výhradně čitelné zdrojové kódy bez jakýchkoli předkompilovaných binárek. 

Pro sestavení spouštěče i instalátoru stačí v PowerShellu spustit:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Skript provede:
1. Zkompilování launcheru `Pontish.exe` z `Launcher.cs`.
2. Zkompilování pomocného heslového můstku `askpass.exe` z `askpass.cs`.
3. Přípravu instalačního balíčku.
4. Zkompilování plnohodnotného instalátoru `Instalovat-Pontish.exe` z `Installer.cs`.

---

## 📦 Instalace

1. Spusťte vygenerovaný soubor `Instalovat-Pontish.exe`.
2. Instalátor automaticky:
   * Nainstaluje aplikaci do `%LOCALAPPDATA%\Programs\Pontish`.
   * Vytvoří zástupce na Ploše a v nabídce Start.
   * Ověří a v případě potřeby opraví zabezpečení složky `~/.ssh/`.

---

## 📄 Licence

Tento projekt je svobodný open-source software šířený pod licencí [MIT License](LICENSE).

