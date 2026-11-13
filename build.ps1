$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$Version = "0.0.1"
$AssemblyVersion = "$Version.0"

$root = $PSScriptRoot
Write-Host "=== Sestavuji Pontish v$Version + Instalator ===" -ForegroundColor Cyan

# Ukoncit pripadne bezici procesy, aby neblokovaly zapis do .exe souboru
Get-Process -Name "Instalovat-Pontish", "Pontish" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 200

# Najdeme csc.exe (.NET Framework 4.x)
$cscCandidates = @(
    "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) {
    throw "Kompilator csc.exe (.NET Framework 4.x) nebyl nalezen."
}
Write-Host "Kompilator: $csc"

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

# 1. Zkompilovat Pontish.exe z Launcher.cs
$launcherCs = Join-Path $root "Launcher.cs"
$launcherExe = Join-Path $root "Pontish.exe"
Write-Host "Kompiluji Pontish.exe (v$Version)..."
& $csc /nologo /target:winexe /optimize+ @icoArgs "/out:$launcherExe" "$launcherCs" "$asmInfoCs" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll
if ($LASTEXITCODE -ne 0) { throw "Kompilace Launcher.cs selhala." }

# 2. Zkompilovat askpass.exe z askpass.cs
$askpassCs = Join-Path $root "askpass.cs"
$askpassExe = Join-Path $root "askpass.exe"
Write-Host "Kompiluji askpass.exe (v$Version)..."
& $csc /nologo /target:exe /optimize+ "/out:$askpassExe" "$askpassCs" "$asmInfoCs"
if ($LASTEXITCODE -ne 0) { throw "Kompilace askpass.cs selhala." }

# 3. Pripravit payload.zip pro instalator
$stageDir = Join-Path ([System.IO.Path]::GetTempPath()) ("Pontish_Stage_" + [Guid]::NewGuid().ToString("N"))
$payloadZip = Join-Path ([System.IO.Path]::GetTempPath()) ("Pontish_Payload_" + [Guid]::NewGuid().ToString("N") + ".zip")
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

try {
    $filesToPack = @("pontish.ps1", "Pontish.exe", "askpass.exe", "pontish.ico")
    foreach ($f in $filesToPack) {
        $src = Join-Path $root $f
        if (Test-Path $src) {
            Copy-Item -Path $src -Destination (Join-Path $stageDir $f) -Force
            Write-Host "  Pridavam do balicku: $f"
        } else {
            Write-Warning "Soubor $f nebyl nalezen a bude vynechan."
        }
    }

    $langSrc = Join-Path $root "lang"
    if (Test-Path $langSrc) {
        Copy-Item -Path $langSrc -Destination (Join-Path $stageDir "lang") -Recurse -Force
        Write-Host "  Pridavam do balicku slozku: lang\"
    }

    [System.IO.Compression.ZipFile]::CreateFromDirectory($stageDir, $payloadZip)

    # 4. Zkompilovat Instalovat-Pontish.exe z Installer.cs + payload.zip
    $installerCs = Join-Path $root "Installer.cs"
    $installerExe = Join-Path $root "Instalovat-Pontish.exe"
    Write-Host "Kompiluji Instalovat-Pontish.exe (v$Version)..."
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
