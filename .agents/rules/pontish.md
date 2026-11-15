# Pontish – Pravidla projektu

## Architektura
- Desktopová aplikace v .NET 8 (`net8.0-windows`, WinForms hostitel + `Microsoft.Web.WebView2`) v `PontishApp.csproj` a `src/*.cs`.
- Webové UI v `wwwroot/` (`index.html`, `styles.css`, `app.js`, offline `xterm.js` v `wwwroot/vendor/`).
- Terminály běží přes Windows ConPTY (`src/ConPtyTerminal.cs`).
- Původní soubory `pontish.ps1` a `Launcher.cs` byly odstraněny a nesmí se znovu zavádět.

## Kompilace a ochrana proti chybám
- `PontishApp.csproj` musí zachovat `<EnableDefaultCompileItems>false</EnableDefaultCompileItems>` a explicitní `<Compile Include="src\**\*.cs" />`, jinak `askpass.cs` a `Installer.cs` v kořenové složce způsobí chybu `CS0017` (více vstupních bodů `Main`).
- Ikony pro System Tray (`MainForm.CreateStatusIcon`) se cachují (`_iconIdle`, `_iconActive`) a nesmí se vytvářet při každém ticku časovače.
- Při přidání nového textu do UI vždy přidej shodný klíč do `lang/cs.json` i `lang/en.json`.
- Při úpravách `wwwroot/styles.css` vždy doplň i variantu pro světlý motiv `html[data-theme="light"]` a udržuj kompatibilitu s `XTERM_THEMES` v `wwwroot/app.js`.
