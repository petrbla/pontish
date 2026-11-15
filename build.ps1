$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$Version = "0.1.1"
$AssemblyVersion = "$Version.0"

$root = $PSScriptRoot
Write-Host "=== Sestavuji Pontish v$Version (.NET 8 + WebView2) + Instalator ===" -ForegroundColor Cyan

# Ukoncit pripadne bezici procesy, aby neblokovaly zapis do .exe souboru
Get-Process -Name "Pontish-Setup", "Instalovat-Pontish", "Pontish" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 200

# Najdeme csc.exe (.NET Framework 4.x) pro askpass.exe a instalator
$cscCandidates = @(
    "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) {
    throw "Kompilator csc.exe (.NET Framework 4.x) nebyl nalezen."
}

$icoPath = Join-Path $root "pontish.ico"
$icoArgs = @()
if (Test-Path $icoPath) { $icoArgs = @("/win32icon:$icoPath") }

# Docasny AssemblyInfo pro metadata verzi ve vlastnostech Windows .exe
$asmInfoCs = Join-Path ([System.IO.Path]::GetTempPath()) ("Pontish_AssemblyInfo_" + [Guid]::NewGuid().ToString("N") + ".cs")
@"
using System.Reflection;
[assembly: AssemblyTitle("Pontish")]
[assembly: AssemblyProduct("Pontish")]
[assembly: AssemblyCompany("Pontish Open Source")]
[assembly: AssemblyCopyright("Copyright (c) 2026")]
[assembly: AssemblyVersion("$AssemblyVersion")]
[assembly: AssemblyFileVersion("$AssemblyVersion")]
[assembly: AssemblyInformationalVersion("$Version")]
"@ | Set-Content -Path $asmInfoCs -Encoding UTF8

# 1. Zkompilovat askpass.exe z askpass.cs
$askpassCs = Join-Path $root "askpass.cs"
$askpassExe = Join-Path $root "askpass.exe"
Write-Host "Kompiluji askpass.exe (v$Version)..."
& $csc /nologo /target:exe /optimize+ "/out:$askpassExe" "$askpassCs" "$asmInfoCs"
if ($LASTEXITCODE -ne 0) { throw "Kompilace askpass.cs selhala." }

# 2. Publikovat PontishApp.csproj do docasne stage slozky
$stageDir = Join-Path ([System.IO.Path]::GetTempPath()) ("Pontish_Stage_" + [Guid]::NewGuid().ToString("N"))
$payloadZip = Join-Path ([System.IO.Path]::GetTempPath()) ("Pontish_Payload_" + [Guid]::NewGuid().ToString("N") + ".zip")
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

try {
    $csproj = Join-Path $root "PontishApp.csproj"
    Write-Host "Publikuji PontishApp.csproj (Release, win-x64)..."
    & dotnet publish "$csproj" -c Release -r win-x64 --self-contained false -o "$stageDir"
    if ($LASTEXITCODE -ne 0) { throw "Publikace PontishApp.csproj selhala." }

    # Odstranit pripadne debug symboly (.pdb), aby byl balicek mensi
    Get-ChildItem -Path $stageDir -Filter "*.pdb" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    Write-Host "Balim soubory do PontishPayload.zip..."
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stageDir, $payloadZip)

    $portableZip = Join-Path $root "Pontish-win-x64.zip"
    if (Test-Path $portableZip) { Remove-Item -Path $portableZip -Force }
    Copy-Item -Path $payloadZip -Destination $portableZip -Force
    Write-Host "Vytvoren portable archiv: $portableZip" -ForegroundColor Green

    # 3. Zkompilovat Pontish-Setup.exe z Installer.cs + payload.zip
    $installerCs = Join-Path $root "Installer.cs"
    $installerExe = Join-Path $root "Pontish-Setup.exe"
    Write-Host "Kompiluji Pontish-Setup.exe (v$Version)..."
    & $csc /nologo /target:winexe /optimize+ @icoArgs "/out:$installerExe" "$installerCs" "$asmInfoCs" `
        "/resource:$payloadZip,PontishPayload.zip" `
        /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:Microsoft.CSharp.dll
    if ($LASTEXITCODE -ne 0) { throw "Kompilace Installer.cs selhala." }

    Write-Host "`nHOTOVO! Vytvoren instalator v$($Version): $installerExe" -ForegroundColor Green
}
finally {
    Remove-Item -Path $stageDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $payloadZip -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $asmInfoCs -Force -ErrorAction SilentlyContinue
}
