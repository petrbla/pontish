---
name: build-pontish
description: >-
  Postup pro kompilaci aplikace Pontish (.NET 8 + WebView2) a sestavení instalátoru
  Pontish-Setup.exe přes build.ps1. Použij při buildu, publikaci nebo řešení
  chyb zámku souborů a ExecutionPolicy.
---

# Sestavení aplikace Pontish a instalátoru

## Postup
1. Ukonči případnou běžící instanci aplikace, aby neblokovala přepis `Pontish.exe` (`MSB3027`):
   `Stop-Process -Name Pontish -Force -ErrorAction SilentlyContinue`
2. Pro rychlou vývojovou kompilaci spusť:
   `dotnet build .\PontishApp.csproj`
3. Pro kompletní sestavení instalátoru `Pontish-Setup.exe` spusť skript s obejitím ExecutionPolicy:
   `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`

## Ověření a spuštění GUI
- GUI okno `Pontish.exe` spuštěné z nástroje `run_command` se po dokončení příkazu automaticky ukončí nebo nezobrazí na ploše uživatele.
- Funkčnost startu ověř kontrolou absence `%LOCALAPPDATA%\Pontish\crash.log` a přítomností profilu `%LOCALAPPDATA%\Pontish\WebView2\EBWebView`.
- Pro živé otestování vyzvi uživatele ke spuštění `bin\Debug\net8.0-windows\Pontish.exe`.
