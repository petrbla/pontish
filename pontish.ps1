Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression.FileSystem

# High-DPI ostrost a vlastni ikona v hlavnim panelu Windows (AppUserModelID)
try {
    Add-Type -TypeDefinition @"
    using System;
    using System.Runtime.InteropServices;
    public class DpiFix {
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();
        [DllImport("SHCore.dll")]
        public static extern int SetProcessDpiAwareness(int awareness);
        [DllImport("shell32.dll", SetLastError = true)]
        public static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string AppID);
    }
"@ -ErrorAction SilentlyContinue
    try {
        [void][DpiFix]::SetProcessDpiAwareness(2)
    } catch {
        [void][DpiFix]::SetProcessDPIAware()
    }
    try {
        [DpiFix]::SetCurrentProcessExplicitAppUserModelID("Pontish.VPSBridge.App")
    } catch {}
} catch {}

[System.Windows.Forms.Application]::EnableVisualStyles()
try {
    [System.Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)
} catch {}

# Automaticke ukonceni pripadne starsi instance pontish.ps1
try {
    Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessId -ne $PID -and ($_.CommandLine -like "*pontish.ps1*" -or $_.CommandLine -like "*SSHMosty.ps1*") } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
} catch {}

# Barvy moderniho tmaveho rozhrani
$BgDark        = [System.Drawing.Color]::FromArgb(24, 24, 37)
$BgTop         = [System.Drawing.Color]::FromArgb(30, 30, 46)
$BgCard        = [System.Drawing.Color]::FromArgb(36, 36, 54)
$BgCardHeader  = [System.Drawing.Color]::FromArgb(45, 45, 68)
$BgRow         = [System.Drawing.Color]::FromArgb(30, 30, 46)
$BgBottom      = [System.Drawing.Color]::FromArgb(20, 20, 32)
$BgLog         = [System.Drawing.Color]::FromArgb(17, 17, 27)
$TextPrimary   = [System.Drawing.Color]::FromArgb(240, 240, 250)
$TextMuted     = [System.Drawing.Color]::FromArgb(166, 173, 200)
$GreenActive   = [System.Drawing.Color]::FromArgb(39, 174, 96)
$OrangePending = [System.Drawing.Color]::FromArgb(243, 156, 18)
$RedInactive   = [System.Drawing.Color]::FromArgb(231, 76, 60)
$GrayOff       = [System.Drawing.Color]::FromArgb(88, 91, 112)
$BlueAccent    = [System.Drawing.Color]::FromArgb(88, 101, 242)

$script:AppVersion = "0.0.1"
$sysLang = [System.Globalization.CultureInfo]::CurrentUICulture.TwoLetterISOLanguageName.ToLower()
$script:CurrentLang = if ($sysLang -eq "cs") { "cs" } else { "en" }
$script:Tunnels = @()
$script:Connections = @()
$script:ActiveTab = "bridges"
$script:AllowClose = $false
$script:CardControls = @{}
$script:SavedPasswords = @{}

$script:I18n = @{}
$langDir = Join-Path $PSScriptRoot "lang"
if (Test-Path $langDir) {
    Get-ChildItem -Path $langDir -Filter "*.json" -File | Sort-Object Name | ForEach-Object {
        try {
            $code = $_.BaseName.ToLower()
            $obj = Get-Content $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
            $dict = @{}
            foreach ($p in $obj.PSObject.Properties) {
                $dict[$p.Name] = [string]$p.Value
            }
            if ($dict.Count -gt 0) {
                $script:I18n[$code] = $dict
            }
        } catch {}
    }
}

$script:SettingsPath = Join-Path $env:LOCALAPPDATA "Pontish\settings.json"
if (Test-Path $script:SettingsPath) {
    try {
        $cfgObj = Get-Content $script:SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not [string]::IsNullOrWhiteSpace($cfgObj.Language) -and $script:I18n.ContainsKey($cfgObj.Language.ToLower())) {
            $script:CurrentLang = $cfgObj.Language.ToLower()
        }
    } catch {}
}

if (-not $script:I18n.ContainsKey($script:CurrentLang) -and $script:I18n.Count -gt 0) {
    $script:CurrentLang = ($script:I18n.Keys | Select-Object -First 1)
}

function Save-AppSettings {
    try {
        $dir = Split-Path $script:SettingsPath -Parent
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        @{ Language = $script:CurrentLang } | ConvertTo-Json | Set-Content -Path $script:SettingsPath -Encoding UTF8
    } catch {}
}

function T([string]$Key, [object[]]$FmtArgs = $null) {
    $dict = $script:I18n[$script:CurrentLang]
    if ($null -eq $dict -or -not $dict.ContainsKey($Key)) {
        if ($script:I18n.ContainsKey("en")) { $dict = $script:I18n["en"] }
        elseif ($script:I18n.ContainsKey("cs")) { $dict = $script:I18n["cs"] }
    }
    if ($null -eq $dict -or -not $dict.ContainsKey($Key)) { return $Key }
    $val = $dict[$Key]
    if ($null -ne $FmtArgs -and $FmtArgs.Count -gt 0) {
        return [string]::Format($val, $FmtArgs)
    }
    return $val
}

try {
    Add-Type -ReferencedAssemblies System.Windows.Forms,System.Drawing -TypeDefinition @"
    using System.Drawing;
    using System.Windows.Forms;
    public class PontishDarkMenuTable : ProfessionalColorTable
    {
        private Color bg = Color.FromArgb(30, 30, 46);
        private Color bgDrop = Color.FromArgb(36, 36, 54);
        private Color hover = Color.FromArgb(56, 56, 84);
        private Color border = Color.FromArgb(69, 71, 90);
        public override Color ToolStripDropDownBackground { get { return bgDrop; } }
        public override Color MenuStripGradientBegin { get { return bg; } }
        public override Color MenuStripGradientEnd { get { return bg; } }
        public override Color MenuItemSelected { get { return hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return hover; } }
        public override Color MenuItemPressedGradientBegin { get { return hover; } }
        public override Color MenuItemPressedGradientEnd { get { return hover; } }
        public override Color MenuItemBorder { get { return border; } }
        public override Color MenuBorder { get { return border; } }
        public override Color ImageMarginGradientBegin { get { return bgDrop; } }
        public override Color ImageMarginGradientMiddle { get { return bgDrop; } }
        public override Color ImageMarginGradientEnd { get { return bgDrop; } }
        public override Color SeparatorDark { get { return border; } }
        public override Color SeparatorLight { get { return border; } }
    }

    public static class PontishTermEmbed
    {
        public delegate bool EnumWindowsProc(System.IntPtr hWnd, System.IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, System.IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        public static extern int GetClassName(System.IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool IsWindowVisible(System.IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern System.IntPtr SetParent(System.IntPtr hWndChild, System.IntPtr hWndNewParent);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(System.IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(System.IntPtr hWnd, int nIndex, int dwNewLong);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(System.IntPtr hWnd, System.IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool MoveWindow(System.IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetForegroundWindow(System.IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(System.IntPtr hWnd, uint Msg, System.IntPtr wParam, System.IntPtr lParam);

        public static System.Collections.Generic.HashSet<System.IntPtr> GetConsoleWindows()
        {
            var set = new System.Collections.Generic.HashSet<System.IntPtr>();
            EnumWindows((h, l) => {
                if (IsWindowVisible(h)) {
                    var cls = new System.Text.StringBuilder(256);
                    GetClassName(h, cls, 256);
                    if (cls.ToString() == "ConsoleWindowClass") {
                        set.Add(h);
                    }
                }
                return true;
            }, System.IntPtr.Zero);
            return set;
        }

        public static System.IntPtr FindNewConsoleWindow(System.Collections.Generic.HashSet<System.IntPtr> before)
        {
            System.IntPtr found = System.IntPtr.Zero;
            EnumWindows((h, l) => {
                if (IsWindowVisible(h) && !before.Contains(h)) {
                    var cls = new System.Text.StringBuilder(256);
                    GetClassName(h, cls, 256);
                    if (cls.ToString() == "ConsoleWindowClass") {
                        found = h;
                        return false;
                    }
                }
                return true;
            }, System.IntPtr.Zero);
            return found;
        }

        public static void Embed(System.IntPtr childHwnd, System.IntPtr parentHwnd, int width, int height)
        {
            const int GWL_STYLE = -16;
            const int WS_CAPTION = 0x00C00000;
            const int WS_BORDER = 0x00800000;
            const int WS_THICKFRAME = 0x00040000;
            const int WS_MINIMIZEBOX = 0x00020000;
            const int WS_MAXIMIZEBOX = 0x00010000;
            const int WS_SYSMENU = 0x00080000;
            const int WS_POPUP = unchecked((int)0x80000000);
            const int WS_CHILD = 0x40000000;
            const uint SWP_NOZORDER = 0x0004;
            const uint SWP_FRAMECHANGED = 0x0020;
            const uint SWP_SHOWWINDOW = 0x0040;

            int style = GetWindowLong(childHwnd, GWL_STYLE);
            style = (style & ~(WS_CAPTION | WS_BORDER | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU | WS_POPUP)) | WS_CHILD;
            SetWindowLong(childHwnd, GWL_STYLE, style);
            SetParent(childHwnd, parentHwnd);
            SetWindowPos(childHwnd, System.IntPtr.Zero, 0, 0, width, height, SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
            MoveWindow(childHwnd, 0, 0, width, height, true);
        }

        public static void CloseWindow(System.IntPtr hWnd)
        {
            if (hWnd != System.IntPtr.Zero) {
                PostMessage(hWnd, 0x0010, System.IntPtr.Zero, System.IntPtr.Zero);
            }
        }
    }
"@ -ErrorAction SilentlyContinue
} catch {}

function New-BridgeBitmap([int]$Size, [bool]$Active) {
    $bmp = New-Object System.Drawing.Bitmap($Size, $Size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [int]([Math]::Max(2, $Size * 0.06))
    $inner = $Size - ($pad * 2)

    $bgColor1 = if ($Active) { [System.Drawing.Color]::FromArgb(22, 101, 52) } else { [System.Drawing.Color]::FromArgb(30, 41, 59) }
    $bgColor2 = if ($Active) { [System.Drawing.Color]::FromArgb(16, 185, 129) } else { [System.Drawing.Color]::FromArgb(79, 70, 229) }

    $rect = New-Object System.Drawing.Rectangle($pad, $pad, $inner, $inner)
    $gradBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $bgColor2, $bgColor1, 45.0)
    $g.FillEllipse($gradBrush, $rect)

    $borderPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(180, 255, 255, 255), [Math]::Max(1.5, [float]($Size * 0.04)))
    $g.DrawEllipse($borderPen, $rect)

    $lineW = [Math]::Max(2.0, [float]($Size * 0.075))
    $whitePen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $lineW)
    $whitePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $whitePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $arcX = [float]($Size * 0.20)
    $arcY = [float]($Size * 0.26)
    $arcW = [float]($Size * 0.60)
    $arcH = [float]($Size * 0.48)
    $g.DrawArc($whitePen, $arcX, $arcY, $arcW, $arcH, 180, 180)

    $deckY = [float]($Size * 0.54)
    $g.DrawLine($whitePen, [float]($Size * 0.16), $deckY, [float]($Size * 0.84), $deckY)

    $thinW = [Math]::Max(1.2, [float]($Size * 0.045))
    $thinPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(220, 255, 255, 255), $thinW)
    $g.DrawLine($thinPen, [float]($Size * 0.36), [float]($Size * 0.31), [float]($Size * 0.36), $deckY)
    $g.DrawLine($thinPen, [float]($Size * 0.50), [float]($Size * 0.26), [float]($Size * 0.50), $deckY)
    $g.DrawLine($thinPen, [float]($Size * 0.64), [float]($Size * 0.31), [float]($Size * 0.64), $deckY)

    if ($Size -ge 64) {
        $fontSize = [float]($Size * 0.17)
        $font = New-Object System.Drawing.Font("Segoe UI", $fontSize, [System.Drawing.FontStyle]::Bold)
        $sf = New-Object System.Drawing.StringFormat
        $sf.Alignment = [System.Drawing.StringAlignment]::Center
        $txtBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        $g.DrawString("SSH", $font, $txtBrush, [float]($Size * 0.50), [float]($Size * 0.60), $sf)
        $txtBrush.Dispose()
        $sf.Dispose()
        $font.Dispose()
    }

    $thinPen.Dispose()
    $whitePen.Dispose()
    $borderPen.Dispose()
    $gradBrush.Dispose()
    $g.Dispose()
    return $bmp
}

function Save-HighQualityIco([string]$FilePath, [bool]$Active) {
    $bmp256 = New-BridgeBitmap 256 $Active
    $msPng = New-Object System.IO.MemoryStream
    $bmp256.Save($msPng, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBytes = $msPng.ToArray()
    $msPng.Dispose()
    $bmp256.Dispose()

    $fs = New-Object System.IO.FileStream($FilePath, [System.IO.FileMode]::Create)
    $bw = New-Object System.IO.BinaryWriter($fs)

    $bw.Write([uint16]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]1)

    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$pngBytes.Length)
    $bw.Write([uint32]22)

    $bw.Write($pngBytes)
    $bw.Flush()
    $bw.Close()
    $fs.Close()
}

function New-StatusIcon([bool]$Active) {
    $bmp = New-BridgeBitmap 64 $Active
    $hIcon = $bmp.GetHicon()
    return [System.Drawing.Icon]::FromHandle($hIcon)
}

$IcoPath = Join-Path $PSScriptRoot "pontish.ico"
try {
    Save-HighQualityIco $IcoPath $false
} catch {}

function Write-AppLog([string]$Message) {
    if ($null -ne $script:LogBox -and -not $script:LogBox.IsDisposed) {
        $ts = (Get-Date).ToString("HH:mm:ss")
        $script:LogBox.AppendText("[$ts] $Message`r`n")
    }
}

function Get-SshConfigPath {
    return Join-Path $env:USERPROFILE ".ssh\config"
}

function Get-AppLogDir {
    $dir = Join-Path $env:LOCALAPPDATA "Pontish\logs"
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force -ErrorAction SilentlyContinue | Out-Null
    }
    return $dir
}

function Resolve-SshPath([string]$RawPath) {
    if ([string]::IsNullOrWhiteSpace($RawPath)) { return "" }
    $p = $RawPath.Trim().Trim('"').Trim("'")
    if ($p.StartsWith("~/") -or $p.StartsWith("~\")) {
        $p = Join-Path $env:USERPROFILE $p.Substring(2)
    } elseif ($p -eq "~") {
        $p = $env:USERPROFILE
    }
    return $p.Replace('/', '\')
}

function Repair-SshFilePermissions([string]$FilePath) {
    if ([string]::IsNullOrWhiteSpace($FilePath) -or -not (Test-Path $FilePath)) { return }
    try {
        $currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = "icacls.exe"
        $psi.Arguments = "`"$FilePath`" /inheritance:r /grant:r `"$($currentUser):(F)`" `"SYSTEM:(F)`""
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $proc = [System.Diagnostics.Process]::Start($psi)
        $proc.WaitForExit(3000) | Out-Null
    } catch {}
}

function Show-PasswordDialog($Tunnel) {
    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = (T "PwdTitle" @($Tunnel.HostAlias))
    $dlg.Size = New-Object System.Drawing.Size(460, 255)
    $dlg.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $dlg.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterParent
    $dlg.MaximizeBox = $false
    $dlg.MinimizeBox = $false
    $dlg.BackColor = $BgCard
    $dlg.ForeColor = $TextPrimary
    $dlg.Font = New-Object System.Drawing.Font("Segoe UI", 9.5)
    $dlg.Icon = $script:Form.Icon

    $keyInfo = if (-not [string]::IsNullOrWhiteSpace($Tunnel.IdentityFile)) { $Tunnel.IdentityFile } else { "$($Tunnel.User)@$($Tunnel.HostName)" }

    $lblPrompt = New-Object System.Windows.Forms.Label
    $lblPrompt.Text = "$(T 'PwdPrompt' @($Tunnel.HostAlias))`r`n($keyInfo):"
    $lblPrompt.Location = New-Object System.Drawing.Point(20, 16)
    $lblPrompt.Size = New-Object System.Drawing.Size(405, 42)
    $lblPrompt.ForeColor = $TextPrimary

    $txtPass = New-Object System.Windows.Forms.TextBox
    $txtPass.Location = New-Object System.Drawing.Point(20, 64)
    $txtPass.Size = New-Object System.Drawing.Size(400, 28)
    $txtPass.Font = New-Object System.Drawing.Font("Segoe UI", 11)
    $txtPass.UseSystemPasswordChar = $true
    $txtPass.BackColor = $BgLog
    $txtPass.ForeColor = $TextPrimary
    $txtPass.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle

    $chkShow = New-Object System.Windows.Forms.CheckBox
    $chkShow.Text = (T "PwdShow")
    $chkShow.Location = New-Object System.Drawing.Point(20, 100)
    $chkShow.AutoSize = $true
    $chkShow.ForeColor = $TextMuted
    $chkShow.Add_CheckedChanged({
        $txtPass.UseSystemPasswordChar = (-not $chkShow.Checked)
    })

    $chkRemember = New-Object System.Windows.Forms.CheckBox
    $chkRemember.Text = (T "PwdRemember")
    $chkRemember.Checked = $true
    $chkRemember.Location = New-Object System.Drawing.Point(20, 126)
    $chkRemember.AutoSize = $true
    $chkRemember.ForeColor = $TextMuted

    $btnOk = New-StyledButton (T "PwdConnect") $GreenActive 145 36
    $btnOk.Location = New-Object System.Drawing.Point(165, 162)
    $btnOk.DialogResult = [System.Windows.Forms.DialogResult]::OK

    $btnCancel = New-StyledButton (T "PwdCancel") $GrayOff 100 36
    $btnCancel.Location = New-Object System.Drawing.Point(320, 162)
    $btnCancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel

    $dlg.AcceptButton = $btnOk
    $dlg.CancelButton = $btnCancel

    $dlg.Controls.Add($lblPrompt)
    $dlg.Controls.Add($txtPass)
    $dlg.Controls.Add($chkShow)
    $dlg.Controls.Add($chkRemember)
    $dlg.Controls.Add($btnOk)
    $dlg.Controls.Add($btnCancel)

    $dlg.Add_Shown({ $txtPass.Focus() })

    $res = $dlg.ShowDialog($script:Form)
    if ($res -eq [System.Windows.Forms.DialogResult]::OK) {
        return [PSCustomObject]@{
            Confirmed = $true
            Password  = $txtPass.Text
            Remember  = $chkRemember.Checked
        }
    }
    return [PSCustomObject]@{
        Confirmed = $false
        Password  = $null
        Remember  = $false
    }
}

function Export-SshSettings([bool]$IncludeKeys, [bool]$AllSshKeys, [string]$TargetPath) {
    $cfgPath = Get-SshConfigPath
    if (-not (Test-Path $cfgPath)) {
        [System.Windows.Forms.MessageBox]::Show("Soubor $cfgPath nebyl nalezen.", "Export nastaveni", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Warning)
        return
    }

    if (-not $IncludeKeys) {
        Copy-Item -Path $cfgPath -Destination $TargetPath -Force
        Write-AppLog "Export konfigurace (~/.ssh/config) ulozen do: $TargetPath"
        [System.Windows.Forms.MessageBox]::Show("Konfigurace byla uspesne exportovana do:`r`n$TargetPath", "Export dokoncen", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Information)
        return
    }

    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("Pontish_Export_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        Copy-Item -Path $cfgPath -Destination (Join-Path $tempDir "config") -Force
        $sshDir = Join-Path $env:USERPROFILE ".ssh"
        $copiedCount = 1

        if ($AllSshKeys) {
            $excludeNames = @("config", "known_hosts", "known_hosts.old", "askpass.exe")
            Get-ChildItem -Path $sshDir -File -ErrorAction SilentlyContinue | Where-Object {
                ($excludeNames -notcontains $_.Name.ToLower()) -and ($_.Name -notmatch '\.(log|bak.*)$')
            } | ForEach-Object {
                Copy-Item -Path $_.FullName -Destination (Join-Path $tempDir $_.Name) -Force
                $copiedCount++
            }
        } else {
            $keySet = @{}
            foreach ($t in $script:Tunnels) {
                $rk = Resolve-SshPath $t.IdentityFile
                if (-not [string]::IsNullOrWhiteSpace($rk) -and (Test-Path $rk)) {
                    $keySet[$rk] = $true
                    if (Test-Path "$rk.pub") { $keySet["$rk.pub"] = $true }
                }
            }
            foreach ($k in $keySet.Keys) {
                $destName = [System.IO.Path]::GetFileName($k)
                Copy-Item -Path $k -Destination (Join-Path $tempDir $destName) -Force
                $copiedCount++
            }
        }

        if (Test-Path $TargetPath) { Remove-Item $TargetPath -Force }
        [System.IO.Compression.ZipFile]::CreateFromDirectory($tempDir, $TargetPath)
        Write-AppLog "Export balicku ($copiedCount souboru) ulozen do: $TargetPath"
        [System.Windows.Forms.MessageBox]::Show("Zaloha ($copiedCount souboru) byla uspesne vytvorena:`r`n$TargetPath", "Export dokoncen", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Information)
    } catch {
        Write-AppLog "CHYBA exportu: $($_.Exception.Message)"
        [System.Windows.Forms.MessageBox]::Show("Chyba pri exportu: $($_.Exception.Message)", "Chyba", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error)
    } finally {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Import-SshSettings([string]$SourcePath) {
    if (-not (Test-Path $SourcePath)) { return }
    $sshDir = Join-Path $env:USERPROFILE ".ssh"
    if (-not (Test-Path $sshDir)) {
        New-Item -ItemType Directory -Path $sshDir -Force | Out-Null
    }

    $cfgPath = Get-SshConfigPath
    if (Test-Path $cfgPath) {
        $bakName = "config.bak_" + (Get-Date).ToString("yyyyMMdd_HHmmss")
        Copy-Item -Path $cfgPath -Destination (Join-Path $sshDir $bakName) -Force
    }

    $ext = [System.IO.Path]::GetExtension($SourcePath).ToLower()
    if ($ext -eq ".zip") {
        $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("Pontish_Import_" + [Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
        try {
            [System.IO.Compression.ZipFile]::ExtractToDirectory($SourcePath, $tempDir)
            $importedFiles = 0
            Get-ChildItem -Path $tempDir -File -Recurse | ForEach-Object {
                if ($_.Name.ToLower() -ne "askpass.exe") {
                    $dest = Join-Path $sshDir $_.Name
                    Copy-Item -Path $_.FullName -Destination $dest -Force
                    Repair-SshFilePermissions $dest
                    $importedFiles++
                }
            }
            Write-AppLog "Import ze ZIP dokoncen ($importedFiles souboru, prava opravena)."
        } catch {
            Write-AppLog "CHYBA importu ZIP: $($_.Exception.Message)"
            [System.Windows.Forms.MessageBox]::Show("Chyba pri importu ZIP: $($_.Exception.Message)", "Chyba", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error)
            return
        } finally {
            Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    } else {
        Copy-Item -Path $SourcePath -Destination $cfgPath -Force
        Repair-SshFilePermissions $cfgPath
        Write-AppLog "Import konfigurace (~/.ssh/config) dokoncen, prava opravena."
    }

    Read-SshTunnels
    Build-CardsUI
    Build-ConnectionsUI
    Update-TabButtons
    Update-TunnelStates
    [System.Windows.Forms.MessageBox]::Show("Nastaveni bylo uspesne naimportovano a prava opravena.", "Import dokoncen", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Information)
}

function Show-ExportImportDialog {
    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = (T "ExpImpTitle")
    $dlg.Size = New-Object System.Drawing.Size(490, 340)
    $dlg.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $dlg.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterParent
    $dlg.MaximizeBox = $false
    $dlg.MinimizeBox = $false
    $dlg.BackColor = $BgCard
    $dlg.ForeColor = $TextPrimary
    $dlg.Font = New-Object System.Drawing.Font("Segoe UI", 9.5)
    $dlg.Icon = $script:Form.Icon

    $lblExpTitle = New-Object System.Windows.Forms.Label
    $lblExpTitle.Text = (T "ExpTitle")
    $lblExpTitle.Font = New-Object System.Drawing.Font("Segoe UI", 10.5, [System.Drawing.FontStyle]::Bold)
    $lblExpTitle.Location = New-Object System.Drawing.Point(20, 16)
    $lblExpTitle.AutoSize = $true

    $rbZip = New-Object System.Windows.Forms.RadioButton
    $rbZip.Text = (T "ExpZip")
    $rbZip.Checked = $true
    $rbZip.Location = New-Object System.Drawing.Point(24, 46)
    $rbZip.AutoSize = $true

    $chkAllKeys = New-Object System.Windows.Forms.CheckBox
    $chkAllKeys.Text = (T "ExpAllKeys")
    $chkAllKeys.Checked = $true
    $chkAllKeys.ForeColor = $TextMuted
    $chkAllKeys.Location = New-Object System.Drawing.Point(44, 72)
    $chkAllKeys.AutoSize = $true

    $rbConfigOnly = New-Object System.Windows.Forms.RadioButton
    $rbConfigOnly.Text = (T "ExpConfigOnly")
    $rbConfigOnly.Location = New-Object System.Drawing.Point(24, 100)
    $rbConfigOnly.AutoSize = $true

    $rbZip.Add_CheckedChanged({ $chkAllKeys.Enabled = $rbZip.Checked })

    $btnExport = New-StyledButton (T "BtnExport") $BlueAccent 150 34
    $btnExport.Location = New-Object System.Drawing.Point(24, 132)
    $btnExport.Add_Click({
        $sfd = New-Object System.Windows.Forms.SaveFileDialog
        $dateTag = (Get-Date).ToString("yyyy-MM-dd")
        if ($rbZip.Checked) {
            $sfd.Filter = "ZIP (*.zip)|*.zip"
            $sfd.FileName = "Pontish-zaloha-$dateTag.zip"
        } else {
            $sfd.Filter = "SSH config (*.txt;*.conf)|*.txt;*.conf|*.*|*.*"
            $sfd.FileName = "ssh-config-$dateTag.txt"
        }
        if ($sfd.ShowDialog($dlg) -eq [System.Windows.Forms.DialogResult]::OK) {
            Export-SshSettings $rbZip.Checked $chkAllKeys.Checked $sfd.FileName
        }
    })

    $sep = New-Object System.Windows.Forms.Label
    $sep.BorderStyle = [System.Windows.Forms.BorderStyle]::Fixed3D
    $sep.Location = New-Object System.Drawing.Point(20, 180)
    $sep.Size = New-Object System.Drawing.Size(435, 2)

    $lblImpTitle = New-Object System.Windows.Forms.Label
    $lblImpTitle.Text = (T "ImpTitle")
    $lblImpTitle.Font = New-Object System.Drawing.Font("Segoe UI", 10.5, [System.Drawing.FontStyle]::Bold)
    $lblImpTitle.Location = New-Object System.Drawing.Point(20, 194)
    $lblImpTitle.AutoSize = $true

    $lblImpInfo = New-Object System.Windows.Forms.Label
    $lblImpInfo.Text = (T "ImpInfo")
    $lblImpInfo.ForeColor = $TextMuted
    $lblImpInfo.Location = New-Object System.Drawing.Point(24, 218)
    $lblImpInfo.AutoSize = $true

    $btnImport = New-StyledButton (T "BtnImport") $GreenActive 195 34
    $btnImport.Location = New-Object System.Drawing.Point(24, 260)
    $btnImport.Add_Click({
        $ofd = New-Object System.Windows.Forms.OpenFileDialog
        $ofd.Filter = "Pontish (*.zip;config;*.txt;*.conf)|*.zip;config;*.txt;*.conf|*.*|*.*"
        if ($ofd.ShowDialog($dlg) -eq [System.Windows.Forms.DialogResult]::OK) {
            Import-SshSettings $ofd.FileName
        }
    })

    $btnClose = New-StyledButton (T "BtnClose") $GrayOff 100 34
    $btnClose.Location = New-Object System.Drawing.Point(355, 260)
    $btnClose.DialogResult = [System.Windows.Forms.DialogResult]::Cancel

    $dlg.Controls.AddRange(@($lblExpTitle, $rbZip, $chkAllKeys, $rbConfigOnly, $btnExport, $sep, $lblImpTitle, $lblImpInfo, $btnImport, $btnClose))
    [void]$dlg.ShowDialog($script:Form)
}

function Read-SshTunnels {
    $cfgPath = Get-SshConfigPath
    if (-not (Test-Path $cfgPath)) {
        Write-AppLog "CHYBA: Soubor $cfgPath nebyl nalezen."
        return
    }

    $oldMap = @{}
    foreach ($old in $script:Tunnels) {
        $oldMap[$old.HostAlias] = $old
    }

    $parsed = @()
    $parsedConns = @()
    $lines = Get-Content -Path $cfgPath -Encoding UTF8
    $current = $null
    $inWildcard = $false
    $wildcardUser = ""
    $wildcardIdentity = ""
    $pendingComment = $null
    $lastSectionHeader = $null
    $logDir = Get-AppLogDir

    foreach ($rawLine in $lines) {
        $line = $rawLine.Trim()
        if ([string]::IsNullOrWhiteSpace($line)) {
            $pendingComment = $null
            continue
        }

        if ($line.StartsWith("#")) {
            $commentText = $line.TrimStart('#').Trim()
            if ($commentText.StartsWith("===") -and $commentText.EndsWith("===")) {
                $lastSectionHeader = $commentText.Trim('=').Trim()
            } else {
                $pendingComment = $commentText
            }
            continue
        }

        $parts = $line -split '\s+'
        if ($parts.Count -lt 2) { continue }

        $key = $parts[0]
        $val = $line.Substring($key.Length).Trim()

        if ($key -ieq "Host") {
            if ($null -ne $current) {
                if ($current.Forwards.Count -gt 0) {
                    $parsed += $current
                }
                if (-not [string]::IsNullOrWhiteSpace($current.HostName)) {
                    $parsedConns += $current
                }
            }
            if ($val -eq "*") {
                $current = $null
                $inWildcard = $true
            } else {
                $inWildcard = $false
                $grp = if ($lastSectionHeader) { $lastSectionHeader } elseif ($pendingComment) { $pendingComment } else { "" }
                $safeAlias = ($val -replace '[^a-zA-Z0-9_.-]', '_')
                $current = [PSCustomObject]@{
                    HostAlias         = $val
                    GroupComment      = $grp
                    HostName          = ""
                    Port              = 22
                    User              = $wildcardUser
                    IdentityFile      = $wildcardIdentity
                    Forwards          = @()
                    RunningProcess    = $null
                    IsConnecting      = $false
                    ConnectStartTime  = [DateTime]::MinValue
                    UserRequestedRun  = $false
                    WasConnected      = $false
                    ReconnectAttempts = 0
                    NextReconnectTime = [DateTime]::MinValue
                    ErrorLogPath      = (Join-Path $logDir "ssh-$safeAlias.err.log")
                }
            }
            $pendingComment = $null
        }
        elseif ($inWildcard) {
            if ($key -ieq "User") { $wildcardUser = $val }
            elseif ($key -ieq "IdentityFile") { $wildcardIdentity = $val }
        }
        elseif ($null -ne $current) {
            if ($key -ieq "HostName") { $current.HostName = $val }
            elseif ($key -ieq "User") { $current.User = $val }
            elseif ($key -ieq "Port") { $current.Port = [int]$val }
            elseif ($key -ieq "IdentityFile") { $current.IdentityFile = $val }
            elseif ($key -ieq "LocalForward" -and $parts.Count -ge 3) {
                $lport = 0
                if ([int]::TryParse($parts[1], [ref]$lport)) {
                    $remote = $parts[2]
                    $label = "Port $lport"
                    $url = $null

                    if (-not [string]::IsNullOrWhiteSpace($pendingComment)) {
                        if ($pendingComment -match '(https?://[^\s]+)') {
                            $url = $matches[1]
                        }
                        $arrowParts = $pendingComment -split '->'
                        if ($arrowParts.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($arrowParts[0])) {
                            $label = $arrowParts[0].Trim()
                        } else {
                            $label = $pendingComment
                        }
                    }

                    $current.Forwards += [PSCustomObject]@{
                        LocalPort    = $lport
                        RemoteTarget = $remote
                        Label        = $label
                        Url          = $url
                        IsListening  = $false
                    }
                }
                $pendingComment = $null
            }
        }
    }

    if ($null -ne $current) {
        if ($current.Forwards.Count -gt 0) {
            $parsed += $current
        }
        if (-not [string]::IsNullOrWhiteSpace($current.HostName)) {
            $parsedConns += $current
        }
    }

    foreach ($t in $parsed) {
        if ($oldMap.ContainsKey($t.HostAlias)) {
            $t.RunningProcess    = $oldMap[$t.HostAlias].RunningProcess
            $t.IsConnecting      = $oldMap[$t.HostAlias].IsConnecting
            $t.ConnectStartTime  = $oldMap[$t.HostAlias].ConnectStartTime
            $t.UserRequestedRun  = $oldMap[$t.HostAlias].UserRequestedRun
            $t.WasConnected      = $oldMap[$t.HostAlias].WasConnected
            $t.ReconnectAttempts = $oldMap[$t.HostAlias].ReconnectAttempts
            $t.NextReconnectTime = $oldMap[$t.HostAlias].NextReconnectTime
        }
    }

    $script:Tunnels = $parsed
    # Seradit tak, aby klasicka pripojeni bez LocalForward byla nahore
    $script:Connections = @($parsedConns | Where-Object { $_.Forwards.Count -eq 0 }) + @($parsedConns | Where-Object { $_.Forwards.Count -gt 0 })
}

function Get-ActiveLocalTcpPorts {
    # Win32-OpenSSH vytvari porty ve stavu Bound (RemotePort = 0), ktere GetActiveTcpListeners() nevidi.
    # Proto kontrolujeme jak GetActiveTcpListeners(), tak Get-NetTCPConnection s RemotePort -eq 0.
    $map = @{}
    try {
        $props = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties()
        foreach ($ep in $props.GetActiveTcpListeners()) {
            $map[[int]$ep.Port] = $true
        }
    } catch {}
    try {
        $conns = Get-NetTCPConnection -ErrorAction SilentlyContinue | Where-Object { $_.RemotePort -eq 0 }
        foreach ($c in $conns) {
            $map[[int]$c.LocalPort] = $true
        }
    } catch {}
    return $map
}

function Stop-ExternalSshOnPorts([int[]]$Ports) {
    if (-not $Ports -or $Ports.Count -eq 0) { return }
    try {
        $conns = Get-NetTCPConnection -ErrorAction SilentlyContinue | Where-Object { $Ports -contains [int]$_.LocalPort -and $_.RemotePort -eq 0 }
        $pids = @{}
        foreach ($c in $conns) {
            if ($c.OwningProcess -gt 0) {
                $pids[[int]$c.OwningProcess] = $true
            }
        }
        foreach ($pidVal in $pids.Keys) {
            $proc = Get-Process -Id $pidVal -ErrorAction SilentlyContinue
            if ($null -ne $proc -and $proc.ProcessName -match 'ssh|cmd') {
                Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            }
        }
    } catch {}
}

function Start-SshTunnel($Tunnel, [bool]$IsAutoReconnect = $false) {
    $allListening = ($Tunnel.Forwards.Count -gt 0) -and (($Tunnel.Forwards | Where-Object { -not $_.IsListening }).Count -eq 0)
    if ($allListening) {
        Write-AppLog "Most '$($Tunnel.HostAlias)' jiz bezi."
        return
    }

    if (-not $IsAutoReconnect) {
        $Tunnel.ReconnectAttempts = 0
    }

    $cfgPath = Get-SshConfigPath
    if (-not (Test-Path $cfgPath)) {
        $Tunnel.UserRequestedRun = $false
        $Tunnel.IsConnecting = $false
        Write-AppLog "CHYBA '$($Tunnel.HostAlias)': Soubor '$cfgPath' nebyl nalezen."
        return
    }

    if ([string]::IsNullOrWhiteSpace($Tunnel.User)) {
        $Tunnel.UserRequestedRun = $false
        $Tunnel.IsConnecting = $false
        Write-AppLog "CHYBA '$($Tunnel.HostAlias)': V ~/.ssh/config chybi 'User'. Pripojeni zablokovano kvuli ochrane pred banem."
        return
    }

    $resolvedKey = Resolve-SshPath $Tunnel.IdentityFile
    if (-not [string]::IsNullOrWhiteSpace($resolvedKey) -and -not (Test-Path $resolvedKey)) {
        $Tunnel.UserRequestedRun = $false
        $Tunnel.IsConnecting = $false
        Write-AppLog "CHYBA '$($Tunnel.HostAlias)': SSH klic '$resolvedKey' na tomto PC neexistuje! Pripojeni zablokovano."
        return
    }

    Repair-SshFilePermissions $cfgPath
    if (-not [string]::IsNullOrWhiteSpace($resolvedKey)) {
        Repair-SshFilePermissions $resolvedKey
    }

    $passToUse = $null
    if ($script:SavedPasswords.ContainsKey($Tunnel.HostAlias)) {
        $passToUse = $script:SavedPasswords[$Tunnel.HostAlias]
    } elseif (-not $IsAutoReconnect) {
        $dlgRes = Show-PasswordDialog $Tunnel
        if (-not $dlgRes.Confirmed) {
            Write-AppLog "Pripojeni mostu '$($Tunnel.HostAlias)' bylo zruseno uzivatelem."
            return
        }
        $passToUse = $dlgRes.Password
        if ($dlgRes.Remember) {
            $script:SavedPasswords[$Tunnel.HostAlias] = $passToUse
        }
    } else {
        Write-AppLog "Nelze automaticky obnovit '$($Tunnel.HostAlias)' bez ulozeneho hesla."
        return
    }

    $Tunnel.UserRequestedRun = $true
    $Tunnel.IsConnecting = $true
    $Tunnel.WasConnected = $false
    $Tunnel.ConnectStartTime = Get-Date
    Write-AppLog "Pripojuji SSH most '$($Tunnel.HostAlias)' ($($Tunnel.User)@$($Tunnel.HostName):$($Tunnel.Port))..."

    try {
        if (Test-Path $Tunnel.ErrorLogPath) {
            Remove-Item $Tunnel.ErrorLogPath -Force -ErrorAction SilentlyContinue
        }

        # OpenSSH ve Windows vyzaduje pro SSH_ASKPASS binarni .exe bez mezer v ceste
        # askpass.exe v ~/.ssh/ zarucuje cestu bez mezer a diakritiky
        $sshDir = Join-Path $env:USERPROFILE ".ssh"
        $askPassExe = Join-Path $sshDir "askpass.exe"
        if (-not (Test-Path $askPassExe)) {
            $askPassSource = Join-Path $PSScriptRoot "askpass.exe"
            if (Test-Path $askPassSource) {
                Copy-Item -Path $askPassSource -Destination $askPassExe -Force -ErrorAction SilentlyContinue
            }
        }

        $env:PONTISH_PASS = $passToUse
        $env:SSH_MOSTY_PASS = $passToUse
        $env:SSH_ASKPASS = $askPassExe
        $env:SSH_ASKPASS_REQUIRE = "force"
        $env:DISPLAY = "localhost:0.0"

        $sshArgs = "-N -F `"$cfgPath`" -l `"$($Tunnel.User)`" -p $($Tunnel.Port)"
        if (-not [string]::IsNullOrWhiteSpace($resolvedKey)) {
            $sshArgs += " -i `"$resolvedKey`" -o IdentitiesOnly=yes"
        }
        $sshArgs += " -o NumberOfPasswordPrompts=1 -o StrictHostKeyChecking=accept-new -o ServerAliveInterval=30 -o ServerAliveCountMax=3 -o ExitOnForwardFailure=yes $($Tunnel.HostAlias)"

        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = "cmd.exe"
        $psi.Arguments = "/c ssh.exe $sshArgs 2> `"$($Tunnel.ErrorLogPath)`""
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true

        $proc = [System.Diagnostics.Process]::Start($psi)
        $Tunnel.RunningProcess = $proc

        $env:SSH_MOSTY_PASS = ""
    } catch {
        $env:SSH_MOSTY_PASS = ""
        $Tunnel.IsConnecting = $false
        $Tunnel.UserRequestedRun = $false
        Write-AppLog "Chyba pri spousteni '$($Tunnel.HostAlias)': $($_.Exception.Message)"
    }

    Update-TunnelStates
}

function Stop-SshTunnel($Tunnel) {
    $Tunnel.UserRequestedRun = $false
    $Tunnel.IsConnecting = $false
    $Tunnel.WasConnected = $false
    Write-AppLog "Zastavuji SSH most '$($Tunnel.HostAlias)'..."

    try {
        if ($null -ne $Tunnel.RunningProcess -and -not $Tunnel.RunningProcess.HasExited) {
            $Tunnel.RunningProcess.Kill()
            $Tunnel.RunningProcess.WaitForExit(1000)
        }
        $Tunnel.RunningProcess = $null

        $ports = @($Tunnel.Forwards | ForEach-Object { [int]$_.LocalPort })
        Stop-ExternalSshOnPorts $ports
    } catch {
        Write-AppLog "Chyba pri zastavovani '$($Tunnel.HostAlias)': $($_.Exception.Message)"
    }

    Update-TunnelStates
    Write-AppLog "Most '$($Tunnel.HostAlias)' byl odpojen."
}

function Switch-SshTunnel($Tunnel) {
    $anyActive = ($Tunnel.Forwards | Where-Object { $_.IsListening }).Count -gt 0
    if ($anyActive -or $Tunnel.IsConnecting -or ($null -ne $Tunnel.RunningProcess -and -not $Tunnel.RunningProcess.HasExited)) {
        Stop-SshTunnel $Tunnel
    } else {
        Start-SshTunnel $Tunnel $false
    }
}

function Start-AllTunnels {
    foreach ($t in $script:Tunnels) {
        $allListening = ($t.Forwards.Count -gt 0) -and (($t.Forwards | Where-Object { -not $_.IsListening }).Count -eq 0)
        if (-not $allListening -and -not $t.IsConnecting) {
            Start-SshTunnel $t $false
        }
    }
}

function Stop-AllTunnels {
    foreach ($t in $script:Tunnels) {
        $anyActive = ($t.Forwards | Where-Object { $_.IsListening }).Count -gt 0
        if ($anyActive -or $t.IsConnecting -or $null -ne $t.RunningProcess) {
            Stop-SshTunnel $t
        }
    }
}

function New-StyledButton([string]$Text, [System.Drawing.Color]$BgColor, [int]$Width, [int]$Height) {
    $btn = New-Object System.Windows.Forms.Button
    $btn.Text = $Text
    $btn.Size = New-Object System.Drawing.Size($Width, $Height)
    $btn.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $btn.FlatAppearance.BorderSize = 1
    $btn.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(120, 255, 255, 255)
    $btn.BackColor = $BgColor
    $btn.ForeColor = [System.Drawing.Color]::White
    $btn.Font = New-Object System.Drawing.Font("Segoe UI", 9.5, [System.Drawing.FontStyle]::Bold)
    $btn.Cursor = [System.Windows.Forms.Cursors]::Hand
    return $btn
}

function New-DbIconBitmap([int]$Size = 18) {
    $bmp = New-Object System.Drawing.Bitmap($Size, $Size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(230, 196, 181, 253), 1.6)
    $fill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(70, 167, 139, 250))

    $x = 2
    $w = $Size - 5
    # Horni elipsa valce (databaze)
    $g.FillEllipse($fill, $x, 2, $w, 5)
    $g.DrawEllipse($pen, $x, 2, $w, 5)
    # Bocni steny valce
    $g.DrawLine($pen, $x, 4, $x, ($Size - 4))
    $g.DrawLine($pen, ($x + $w), 4, ($x + $w), ($Size - 4))
    # Stredni predel valce
    $g.DrawArc($pen, $x, 6, $w, 5, 0, 180)
    # Spodni dno valce
    $g.DrawArc($pen, $x, ($Size - 7), $w, 5, 0, 180)

    $fill.Dispose()
    $pen.Dispose()
    $g.Dispose()
    return $bmp
}

function Build-CardsUI {
    $script:CardsPanel.SuspendLayout()
    $script:CardsPanel.Controls.Clear()
    $script:CardControls = @{}

    $cardWidth = 710

    foreach ($t in $script:Tunnels) {
        $card = New-Object System.Windows.Forms.Panel
        $card.Width = $cardWidth
        $card.BackColor = $BgCard
        $card.Margin = New-Object System.Windows.Forms.Padding(0, 0, 0, 16)

        $header = New-Object System.Windows.Forms.Panel
        $header.Location = New-Object System.Drawing.Point(0, 0)
        $header.Size = New-Object System.Drawing.Size($cardWidth, 64)
        $header.BackColor = $BgCardHeader

        $btnToggle = New-StyledButton "$([char]0x25B6) $(T 'BtnStartBridge')" $GreenActive 165 38
        $btnToggle.Location = New-Object System.Drawing.Point(($cardWidth - 180), 13)
        $btnToggle.Tag = $t
        $btnToggle.Add_Click({
            param($sender, $e)
            Switch-SshTunnel $sender.Tag
        })

        $badge = New-Object System.Windows.Forms.Label
        $badge.Text = (T "BadgeStopped")
        $badge.Font = New-Object System.Drawing.Font("Segoe UI", 9, [System.Drawing.FontStyle]::Bold)
        $badge.ForeColor = [System.Drawing.Color]::White
        $badge.BackColor = $GrayOff
        $badge.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
        $badge.Size = New-Object System.Drawing.Size(130, 30)
        $badge.Location = New-Object System.Drawing.Point(($cardWidth - 322), 17)

        $lblAlias = New-Object System.Windows.Forms.Label
        $lblAlias.Text = $t.HostAlias
        $lblAlias.Font = New-Object System.Drawing.Font("Segoe UI", 13, [System.Drawing.FontStyle]::Bold)
        $lblAlias.ForeColor = $TextPrimary
        $lblAlias.AutoSize = $true
        $lblAlias.Location = New-Object System.Drawing.Point(16, 8)

        $subText = "$($t.User)@$($t.HostName):$($t.Port)"
        if (-not [string]::IsNullOrWhiteSpace($t.GroupComment)) {
            $subText += "  |  $($t.GroupComment)"
        }
        $lblSub = New-Object System.Windows.Forms.Label
        $lblSub.Text = $subText
        $lblSub.Font = New-Object System.Drawing.Font("Segoe UI", 9)
        $lblSub.ForeColor = $TextMuted
        $lblSub.AutoSize = $true
        $lblSub.Location = New-Object System.Drawing.Point(18, 36)

        $header.Controls.Add($lblAlias)
        $header.Controls.Add($lblSub)
        $header.Controls.Add($badge)
        $header.Controls.Add($btnToggle)
        $card.Controls.Add($header)

        $rowY = 74
        $dots = @()

        foreach ($fw in $t.Forwards) {
            $row = New-Object System.Windows.Forms.Panel
            $row.Location = New-Object System.Drawing.Point(16, $rowY)
            $row.Size = New-Object System.Drawing.Size(($cardWidth - 32), 36)
            $row.BackColor = $BgRow

            $dot = New-Object System.Windows.Forms.Label
            $dot.Text = [char]0x25CF
            $dot.Font = New-Object System.Drawing.Font("Segoe UI", 11, [System.Drawing.FontStyle]::Bold)
            $dot.ForeColor = $RedInactive
            $dot.AutoSize = $true
            $dot.Location = New-Object System.Drawing.Point(10, 6)
            $dots += $dot

            $lblFwName = New-Object System.Windows.Forms.Label
            $lblFwName.Text = $fw.Label
            $lblFwName.Font = New-Object System.Drawing.Font("Segoe UI", 9.5, [System.Drawing.FontStyle]::Bold)
            $lblFwName.ForeColor = $TextPrimary
            $lblFwName.AutoSize = $true
            $lblFwName.Location = New-Object System.Drawing.Point(34, 8)

            $lblPorts = New-Object System.Windows.Forms.Label
            $lblPorts.Text = "localhost:$($fw.LocalPort)  ->  $($fw.RemoteTarget)"
            $lblPorts.Font = New-Object System.Drawing.Font("Consolas", 9.5)
            $lblPorts.ForeColor = $TextMuted
            $lblPorts.AutoSize = $true
            $lblPorts.Location = New-Object System.Drawing.Point(190, 9)

            $row.Controls.Add($dot)
            $row.Controls.Add($lblFwName)
            $row.Controls.Add($lblPorts)

            if (-not [string]::IsNullOrWhiteSpace($fw.Url)) {
                $btnOpen = New-StyledButton "$(T 'BtnOpenBrowser') $([char]0x2197)" $BlueAccent 175 28
                $btnOpen.Font = New-Object System.Drawing.Font("Segoe UI", 8.5, [System.Drawing.FontStyle]::Bold)
                $btnOpen.Location = New-Object System.Drawing.Point(($row.Width - 185), 4)
                $btnOpen.Tag = $fw.Url
                $btnOpen.Add_Click({
                    param($sender, $e)
                    Start-Process $sender.Tag
                })
                $row.Controls.Add($btnOpen)
            } else {
                $dbBg = [System.Drawing.Color]::FromArgb(56, 48, 84)
                $btnDb = New-StyledButton (T "BtnDbCopy") $dbBg 175 28
                $btnDb.Font = New-Object System.Drawing.Font("Segoe UI", 8.5, [System.Drawing.FontStyle]::Bold)
                $btnDb.ForeColor = [System.Drawing.Color]::FromArgb(221, 214, 254)
                $btnDb.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(140, 139, 92, 246)
                $btnDb.Image = New-DbIconBitmap 18
                $btnDb.ImageAlign = [System.Drawing.ContentAlignment]::MiddleLeft
                $btnDb.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
                $btnDb.Location = New-Object System.Drawing.Point(($row.Width - 185), 4)
                $btnDb.Tag = "localhost:$($fw.LocalPort)"
                $btnDb.Add_Click({
                    param($sender, $e)
                    try {
                        [System.Windows.Forms.Clipboard]::SetText([string]$sender.Tag)
                        Write-AppLog "Adresa '$($sender.Tag)' zkopirovana do schranky (pro DB program)."
                    } catch {}
                })
                $row.Controls.Add($btnDb)
            }

            $card.Controls.Add($row)
            $rowY += 42
        }

        $card.Height = $rowY + 10

        $script:CardControls[$t.HostAlias] = [PSCustomObject]@{
            Card   = $card
            Badge  = $badge
            Button = $btnToggle
            Dots   = $dots
        }

        $script:CardsPanel.Controls.Add($card)
    }

    $script:CardsPanel.ResumeLayout()
}

$script:TermProcess = $null
$script:TermHwnd = [IntPtr]::Zero
$script:TermConn = $null

function Close-EmbeddedTerminal {
    if ($script:TermHwnd -ne [IntPtr]::Zero) {
        try { [PontishTermEmbed]::CloseWindow($script:TermHwnd) } catch {}
    }
    if ($null -ne $script:TermProcess) {
        try {
            if (-not $script:TermProcess.HasExited) {
                $script:TermProcess.Kill()
            }
        } catch {}
        $script:TermProcess = $null
    }
    $script:TermHwnd = [IntPtr]::Zero
    $script:TermConn = $null
    if ($null -ne $script:LblTermEmpty) { $script:LblTermEmpty.Visible = $true }
    if ($null -ne $script:LblTermStatus) { $script:LblTermStatus.Text = (T "TabTerminal") }
    if ($null -ne $script:BtnCloseTerm) { $script:BtnCloseTerm.Enabled = $false }
    if ($null -ne $script:BtnExtTerm) { $script:BtnExtTerm.Enabled = $false }
}

function Open-SshTerminal($Conn, [bool]$External = $false) {
    $cfgPath = Get-SshConfigPath
    Write-AppLog (T "LogOpenTerminal" @($Conn.HostAlias))

    if ($External) {
        try {
            $wtCmd = Get-Command "wt.exe" -ErrorAction SilentlyContinue
            if ($null -ne $wtCmd) {
                Start-Process -FilePath $wtCmd.Source -ArgumentList "new-tab --title `"SSH: $($Conn.HostAlias)`" ssh.exe -F `"$cfgPath`" $($Conn.HostAlias)"
            } else {
                Start-Process -FilePath "cmd.exe" -ArgumentList "/k `"title SSH: $($Conn.HostAlias) && ssh.exe -F `"$cfgPath`" $($Conn.HostAlias)`""
            }
        } catch {
            Write-AppLog "CHYBA terminalu '$($Conn.HostAlias)': $($_.Exception.Message)"
        }
        return
    }

    try {
        Close-EmbeddedTerminal
        Switch-Tab "terminal"
        $script:TermConn = $Conn
        $script:LblTermEmpty.Visible = $false
        $script:LblTermStatus.Text = (T "TermActive" @("$($Conn.HostAlias) ($($Conn.User)@$($Conn.HostName))"))
        $script:BtnCloseTerm.Enabled = $true
        $script:BtnExtTerm.Enabled = $true

        $beforeConsoles = [PontishTermEmbed]::GetConsoleWindows()

        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = "conhost.exe"
        $psi.Arguments = "ssh.exe -F `"$cfgPath`" $($Conn.HostAlias)"
        $psi.UseShellExecute = $false

        $proc = [System.Diagnostics.Process]::Start($psi)
        $script:TermProcess = $proc

        $foundHwnd = [IntPtr]::Zero
        $waited = 0
        while ($foundHwnd -eq [IntPtr]::Zero -and -not $proc.HasExited -and $waited -lt 4000) {
            Start-Sleep -Milliseconds 50
            [System.Windows.Forms.Application]::DoEvents()
            $foundHwnd = [PontishTermEmbed]::FindNewConsoleWindow($beforeConsoles)
            $waited += 50
        }

        if ($foundHwnd -ne [IntPtr]::Zero) {
            $script:TermHwnd = $foundHwnd
            [PontishTermEmbed]::Embed($script:TermHwnd, $script:TermHostPanel.Handle, $script:TermHostPanel.ClientSize.Width, $script:TermHostPanel.ClientSize.Height)
            [PontishTermEmbed]::SetForegroundWindow($script:TermHwnd) | Out-Null
        } else {
            Write-AppLog "VAROVANI: Nepodarilo se najit okno konzole pro '$($Conn.HostAlias)'."
        }
    } catch {
        Write-AppLog "CHYBA terminalu '$($Conn.HostAlias)': $($_.Exception.Message)"
    }
}

function Build-ConnectionsUI {
    $script:ConnectionsPanel.SuspendLayout()
    $script:ConnectionsPanel.Controls.Clear()

    $cardWidth = 710

    if ($script:Connections.Count -eq 0) {
        $lblEmpty = New-Object System.Windows.Forms.Label
        $lblEmpty.Text = (T "NoConnections")
        $lblEmpty.Font = New-Object System.Drawing.Font("Segoe UI", 10)
        $lblEmpty.ForeColor = $TextMuted
        $lblEmpty.AutoSize = $true
        $lblEmpty.Margin = New-Object System.Windows.Forms.Padding(8, 16, 0, 0)
        $script:ConnectionsPanel.Controls.Add($lblEmpty)
        $script:ConnectionsPanel.ResumeLayout()
        return
    }

    foreach ($c in $script:Connections) {
        $card = New-Object System.Windows.Forms.Panel
        $card.Width = $cardWidth
        $card.Height = 76
        $card.BackColor = $BgCard
        $card.Margin = New-Object System.Windows.Forms.Padding(0, 0, 0, 12)

        $header = New-Object System.Windows.Forms.Panel
        $header.Dock = [System.Windows.Forms.DockStyle]::Fill
        $header.BackColor = $BgCardHeader

        $btnTerm = New-StyledButton ">_ $(T 'BtnOpenTerminal')" $BlueAccent 175 38
        $btnTerm.Location = New-Object System.Drawing.Point(($cardWidth - 190), 19)
        $btnTerm.Tag = $c
        $btnTerm.Add_Click({
            param($sender, $e)
            Open-SshTerminal $sender.Tag
        })

        $isBridge = ($c.Forwards.Count -gt 0)
        $badge = New-Object System.Windows.Forms.Label
        $badge.Text = if ($isBridge) { (T "BadgeSshBridge") } else { (T "BadgeSshOnly") }
        $badge.Font = New-Object System.Drawing.Font("Segoe UI", 8.5, [System.Drawing.FontStyle]::Bold)
        $badge.ForeColor = [System.Drawing.Color]::White
        $badge.BackColor = if ($isBridge) { [System.Drawing.Color]::FromArgb(88, 91, 112) } else { [System.Drawing.Color]::FromArgb(42, 110, 82) }
        $badge.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
        $badge.Size = New-Object System.Drawing.Size(120, 28)
        $badge.Location = New-Object System.Drawing.Point(($cardWidth - 322), 24)

        $lblAlias = New-Object System.Windows.Forms.Label
        $lblAlias.Text = $c.HostAlias
        $lblAlias.Font = New-Object System.Drawing.Font("Segoe UI", 13, [System.Drawing.FontStyle]::Bold)
        $lblAlias.ForeColor = $TextPrimary
        $lblAlias.AutoSize = $true
        $lblAlias.Location = New-Object System.Drawing.Point(16, 12)

        $subText = "$($c.User)@$($c.HostName):$($c.Port)"
        if (-not [string]::IsNullOrWhiteSpace($c.IdentityFile)) {
            $subText += "  |  $($c.IdentityFile)"
        }
        if (-not [string]::IsNullOrWhiteSpace($c.GroupComment)) {
            $subText += "  |  $($c.GroupComment)"
        }
        $lblSub = New-Object System.Windows.Forms.Label
        $lblSub.Text = $subText
        $lblSub.Font = New-Object System.Drawing.Font("Segoe UI", 9)
        $lblSub.ForeColor = $TextMuted
        $lblSub.AutoSize = $true
        $lblSub.Location = New-Object System.Drawing.Point(18, 42)

        $header.Controls.Add($lblAlias)
        $header.Controls.Add($lblSub)
        $header.Controls.Add($badge)
        $header.Controls.Add($btnTerm)
        $card.Controls.Add($header)

        $script:ConnectionsPanel.Controls.Add($card)
    }

    $script:ConnectionsPanel.ResumeLayout()
}

function Update-TunnelStates {
    $listeningPorts = Get-ActiveLocalTcpPorts
    $activeTunnelsCount = 0

    foreach ($t in $script:Tunnels) {
        if ($t.NextReconnectTime -ne [DateTime]::MinValue -and (Get-Date) -ge $t.NextReconnectTime) {
            $t.NextReconnectTime = [DateTime]::MinValue
            if ($t.UserRequestedRun -and $script:ChkAutoReconnect.Checked) {
                $t.ReconnectAttempts++
                Write-AppLog "Most '$($t.HostAlias)' vypadl. Pokus o obnoveni $($t.ReconnectAttempts)/3..."
                Start-SshTunnel $t $true
                continue
            }
        }

        $ui = $script:CardControls[$t.HostAlias]
        $listenCount = 0

        for ($i = 0; $i -lt $t.Forwards.Count; $i++) {
            $fw = $t.Forwards[$i]
            $fw.IsListening = [bool]$listeningPorts.ContainsKey([int]$fw.LocalPort)
            if ($fw.IsListening) { $listenCount++ }

            if ($null -ne $ui -and $i -lt $ui.Dots.Count) {
                $ui.Dots[$i].ForeColor = if ($fw.IsListening) { $GreenActive } else { $RedInactive }
            }
        }

        $totalCount = $t.Forwards.Count
        $isFull = ($totalCount -gt 0) -and ($listenCount -eq $totalCount)
        $isPartial = ($listenCount -gt 0) -and ($listenCount -lt $totalCount)

        if ($null -ne $t.RunningProcess -and $t.RunningProcess.HasExited) {
            $exitCode = $t.RunningProcess.ExitCode
            $t.RunningProcess = $null
            $t.IsConnecting = $false

            # Pokud porty naslouchaji, most ve skutecnosti bezi (napr. jiz existujici ssh proces)
            if (-not $isFull -and -not $isPartial) {
                $errMsg = ""
                if (Test-Path $t.ErrorLogPath) {
                    try {
                        $errMsg = (Get-Content $t.ErrorLogPath -Raw -ErrorAction SilentlyContinue).Trim()
                    } catch {}
                }

                if ($t.UserRequestedRun) {
                    $isAuthError = $errMsg -match "Permission denied|Authentication failed|bad passphrase|incorrect passphrase|Too many authentication failures|Host key verification failed"
                    if ($isAuthError) {
                        $t.UserRequestedRun = $false
                        [void]$script:SavedPasswords.Remove($t.HostAlias)
                        Write-AppLog "CHYBA '$($t.HostAlias)': Overeni selhalo ($errMsg). Automaticke opakovani zastaveno kvuli ochrane pred banem."
                    } elseif ($t.WasConnected -and $script:ChkAutoReconnect.Checked) {
                        if ($t.ReconnectAttempts -lt 3) {
                            $t.NextReconnectTime = (Get-Date).AddSeconds(5)
                            Write-AppLog "Most '$($t.HostAlias)' vypadl (kod $exitCode). Zkusim obnovit za 5 s..."
                        } else {
                            $t.UserRequestedRun = $false
                            Write-AppLog "CHYBA '$($t.HostAlias)': Prekrocen limit 3 pokusu o obnoveni. Zkontrolujte spojeni."
                        }
                    } else {
                        $t.UserRequestedRun = $false
                        if (-not [string]::IsNullOrWhiteSpace($errMsg)) {
                            Write-AppLog "CHYBA '$($t.HostAlias)': $errMsg"
                        } else {
                            Write-AppLog "Most '$($t.HostAlias)' byl ukoncen (kod $exitCode)."
                        }
                    }
                }
            }
        }

        if ($isFull) {
            if (-not $t.WasConnected) {
                $t.WasConnected = $true
                $t.IsConnecting = $false
                $t.ReconnectAttempts = 0
                Write-AppLog "Most '$($t.HostAlias)' je AKTIVNI ($listenCount/$totalCount portu nasloucha)."
            }
            $activeTunnelsCount++
        } elseif ($t.IsConnecting -and ((Get-Date) - $t.ConnectStartTime).TotalSeconds -gt 25) {
            $t.IsConnecting = $false
        }

        if ($null -ne $ui) {
            if ($isFull) {
                $ui.Badge.Text = (T "BadgeRunning" @($listenCount, $totalCount))
                $ui.Badge.BackColor = $GreenActive
                $ui.Button.Text = "$([char]0x25A0) $(T 'BtnStopBridge')"
                $ui.Button.BackColor = $RedInactive
            } elseif ($t.IsConnecting) {
                $ui.Badge.Text = (T "BadgeConnecting")
                $ui.Badge.BackColor = $OrangePending
                $ui.Button.Text = "$([char]0x2715) $(T 'BtnCancel')"
                $ui.Button.BackColor = $RedInactive
            } elseif ($isPartial) {
                $ui.Badge.Text = (T "BadgePartial" @($listenCount, $totalCount))
                $ui.Badge.BackColor = $OrangePending
                $ui.Button.Text = "$([char]0x25A0) $(T 'BtnStopBridge')"
                $ui.Button.BackColor = $RedInactive
            } else {
                $ui.Badge.Text = (T "BadgeStopped")
                $ui.Badge.BackColor = $GrayOff
                $ui.Button.Text = "$([char]0x25B6) $(T 'BtnStartBridge')"
                $ui.Button.BackColor = $GreenActive
            }
        }
    }

    $script:LblHeaderSummary.Text = (T "HeaderSummary" @($activeTunnelsCount, $script:Tunnels.Count))

    $anyActive = $activeTunnelsCount -gt 0
    $newIcon = New-StatusIcon $anyActive
    $script:Form.Icon = $newIcon
    $script:TrayIcon.Icon = $newIcon
    $script:TrayIcon.Text = (T "TrayText" @($activeTunnelsCount, $script:Tunnels.Count))

    $script:TrayMenu.Items.Clear()
    $itemOpen = New-Object System.Windows.Forms.ToolStripMenuItem((T "TrayOpen"))
    $itemOpen.Font = New-Object System.Drawing.Font($itemOpen.Font, [System.Drawing.FontStyle]::Bold)
    $itemOpen.Add_Click({
        $script:Form.Show()
        $script:Form.WindowState = [System.Windows.Forms.FormWindowState]::Normal
        $script:Form.Activate()
    })
    [void]$script:TrayMenu.Items.Add($itemOpen)
    [void]$script:TrayMenu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))

    foreach ($t in $script:Tunnels) {
        $allListening = ($t.Forwards.Count -gt 0) -and (($t.Forwards | Where-Object { -not $_.IsListening }).Count -eq 0)
        $stateTxt = if ($allListening) { (T "TrayRunHint" @($t.HostAlias)) } else { (T "TrayStopHint" @($t.HostAlias)) }
        $tItem = New-Object System.Windows.Forms.ToolStripMenuItem($stateTxt)
        $tItem.Tag = $t
        $tItem.Add_Click({
            param($sender, $e)
            Switch-SshTunnel $sender.Tag
        })
        [void]$script:TrayMenu.Items.Add($tItem)
    }

    [void]$script:TrayMenu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
    $itemStartAll = New-Object System.Windows.Forms.ToolStripMenuItem((T "TrayStartAll"))
    $itemStartAll.Add_Click({ Start-AllTunnels })
    [void]$script:TrayMenu.Items.Add($itemStartAll)

    $itemStopAll = New-Object System.Windows.Forms.ToolStripMenuItem((T "TrayStopAll"))
    $itemStopAll.Add_Click({ Stop-AllTunnels })
    [void]$script:TrayMenu.Items.Add($itemStopAll)

    [void]$script:TrayMenu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
    $itemExit = New-Object System.Windows.Forms.ToolStripMenuItem((T "TrayExit"))
    $itemExit.Add_Click({
        $script:AllowClose = $true
        Stop-AllTunnels
        $script:TrayIcon.Visible = $false
        $script:Form.Close()
    })
    [void]$script:TrayMenu.Items.Add($itemExit)
}

# Hlavni okno
$script:Form = New-Object System.Windows.Forms.Form
$script:Form.AutoScaleMode = [System.Windows.Forms.AutoScaleMode]::Dpi
$script:Form.Text = (T "WindowTitle" @($script:AppVersion))
$script:Form.Size = New-Object System.Drawing.Size(770, 745)
$script:Form.MinimumSize = New-Object System.Drawing.Size(760, 580)
$script:Form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
$script:Form.BackColor = $BgDark
$script:Form.ForeColor = $TextPrimary
$script:Form.Font = New-Object System.Drawing.Font("Segoe UI", 9.5)
if (Test-Path $IcoPath) {
    try { $script:Form.Icon = New-Object System.Drawing.Icon($IcoPath) } catch { $script:Form.Icon = New-StatusIcon $false }
} else {
    $script:Form.Icon = New-StatusIcon $false
}

# Hlavni horni menu (MenuStrip)
$menuStrip = New-Object System.Windows.Forms.MenuStrip
$menuStrip.BackColor = $BgTop
$menuStrip.ForeColor = $TextPrimary
$menuStrip.Padding = New-Object System.Windows.Forms.Padding(8, 2, 0, 2)
try {
    $menuStrip.Renderer = New-Object System.Windows.Forms.ToolStripProfessionalRenderer((New-Object PontishDarkMenuTable))
} catch {}

$miFile = New-Object System.Windows.Forms.ToolStripMenuItem((T "MenuFile"))
$miFile.ForeColor = $TextPrimary
$miFileReload = New-Object System.Windows.Forms.ToolStripMenuItem((T "MenuReload"))
$miFileReload.ForeColor = $TextPrimary
$miFileReload.Add_Click({
    Write-AppLog "Znovu nacitam ~/.ssh/config..."
    Read-SshTunnels
    Build-CardsUI
    Build-ConnectionsUI
    Update-TabButtons
    Update-TunnelStates
})
$miFileEdit = New-Object System.Windows.Forms.ToolStripMenuItem((T "MenuEditConfig"))
$miFileEdit.ForeColor = $TextPrimary
$miFileEdit.Add_Click({
    $cfg = Get-SshConfigPath
    if (Test-Path $cfg) { Start-Process "notepad.exe" -ArgumentList "`"$cfg`"" }
})
$miFileExport = New-Object System.Windows.Forms.ToolStripMenuItem((T "MenuExportImport"))
$miFileExport.ForeColor = $TextPrimary
$miFileExport.Add_Click({ Show-ExportImportDialog })
$miFileExit = New-Object System.Windows.Forms.ToolStripMenuItem((T "MenuExit"))
$miFileExit.ForeColor = $RedInactive
$miFileExit.Add_Click({
    $script:AllowClose = $true
    Stop-AllTunnels
    $script:TrayIcon.Visible = $false
    $script:Form.Close()
})
[void]$miFile.DropDownItems.AddRange(@($miFileReload, $miFileEdit, $miFileExport, (New-Object System.Windows.Forms.ToolStripSeparator), $miFileExit))

$miBridges = New-Object System.Windows.Forms.ToolStripMenuItem((T "MenuBridges"))
$miBridges.ForeColor = $TextPrimary
$miBridgesStartAll = New-Object System.Windows.Forms.ToolStripMenuItem((T "MenuStartAll"))
$miBridgesStartAll.ForeColor = $TextPrimary
$miBridgesStartAll.Add_Click({ Start-AllTunnels })
$miBridgesStopAll = New-Object System.Windows.Forms.ToolStripMenuItem((T "MenuStopAll"))
$miBridgesStopAll.ForeColor = $TextPrimary
$miBridgesStopAll.Add_Click({ Stop-AllTunnels })
$miBridgesForget = New-Object System.Windows.Forms.ToolStripMenuItem((T "MenuForgetPass"))
$miBridgesForget.ForeColor = $OrangePending
$miBridgesForget.Add_Click({
    $script:SavedPasswords.Clear()
    Write-AppLog "Ulozena hesla v pameti byla vymazana."
})
[void]$miBridges.DropDownItems.AddRange(@($miBridgesStartAll, $miBridgesStopAll, (New-Object System.Windows.Forms.ToolStripSeparator), $miBridgesForget))

$miLang = New-Object System.Windows.Forms.ToolStripMenuItem((T "MenuLanguage"))
$miLang.ForeColor = $TextPrimary
$script:LangMenuItems = @{}
foreach ($code in ($script:I18n.Keys | Sort-Object)) {
    $langName = if ($script:I18n[$code].ContainsKey("_LanguageName")) { $script:I18n[$code]["_LanguageName"] } else { $code.ToUpper() }
    $item = New-Object System.Windows.Forms.ToolStripMenuItem($langName)
    $item.ForeColor = $TextPrimary
    $item.Tag = $code
    $item.Checked = ($script:CurrentLang -eq $code)
    $item.Add_Click({
        param($sender, $e)
        Apply-Language ([string]$sender.Tag)
    })
    $script:LangMenuItems[$code] = $item
    [void]$miLang.DropDownItems.Add($item)
}

[void]$menuStrip.Items.AddRange(@($miFile, $miBridges, $miLang))
$script:Form.MainMenuStrip = $menuStrip

# Horni panel
$topBar = New-Object System.Windows.Forms.Panel
$topBar.Dock = [System.Windows.Forms.DockStyle]::Top
$topBar.Size = New-Object System.Drawing.Size(750, 72)
$topBar.BackColor = $BgTop

$lblTitle = New-Object System.Windows.Forms.Label
$lblTitle.Text = "Pontish v$($script:AppVersion)"
$lblTitle.Font = New-Object System.Drawing.Font("Segoe UI", 15, [System.Drawing.FontStyle]::Bold)
$lblTitle.ForeColor = $TextPrimary
$lblTitle.AutoSize = $true
$lblTitle.Location = New-Object System.Drawing.Point(18, 10)

$script:LblHeaderSummary = New-Object System.Windows.Forms.Label
$script:LblHeaderSummary.Text = "..."
$script:LblHeaderSummary.Font = New-Object System.Drawing.Font("Segoe UI", 9)
$script:LblHeaderSummary.ForeColor = $TextMuted
$script:LblHeaderSummary.AutoSize = $true
$script:LblHeaderSummary.Location = New-Object System.Drawing.Point(20, 42)

$btnStartAll = New-StyledButton "$([char]0x25B6) $(T 'BtnStartAll')" $GreenActive 130 36
$btnStartAll.Location = New-Object System.Drawing.Point(335, 18)
$btnStartAll.Add_Click({ Start-AllTunnels })

$btnStopAll = New-StyledButton "$([char]0x25A0) $(T 'BtnStopAll')" $RedInactive 130 36
$btnStopAll.Location = New-Object System.Drawing.Point(475, 18)
$btnStopAll.Add_Click({ Stop-AllTunnels })

$btnReload = New-StyledButton "$([char]0x21BB) $(T 'BtnReload')" $GrayOff 120 36
$btnReload.Location = New-Object System.Drawing.Point(615, 18)
$btnReload.Add_Click({
    Write-AppLog "Znovu nacitam ~/.ssh/config..."
    Read-SshTunnels
    Build-CardsUI
    Build-ConnectionsUI
    Update-TabButtons
    Update-TunnelStates
})

$topBar.Controls.Add($lblTitle)
$topBar.Controls.Add($script:LblHeaderSummary)
$topBar.Controls.Add($btnStartAll)
$topBar.Controls.Add($btnStopAll)
$topBar.Controls.Add($btnReload)

# Spodni panel
$bottomPanel = New-Object System.Windows.Forms.Panel
$bottomPanel.Dock = [System.Windows.Forms.DockStyle]::Bottom
$bottomPanel.Size = New-Object System.Drawing.Size(750, 150)
$bottomPanel.BackColor = $BgBottom

$script:ChkAutoReconnect = New-Object System.Windows.Forms.CheckBox
$script:ChkAutoReconnect.Text = (T "ChkAutoReconnect")
$script:ChkAutoReconnect.Checked = $true
$script:ChkAutoReconnect.ForeColor = $TextMuted
$script:ChkAutoReconnect.AutoSize = $true
$script:ChkAutoReconnect.Location = New-Object System.Drawing.Point(16, 6)

$lnkClearPass = New-Object System.Windows.Forms.LinkLabel
$lnkClearPass.Text = (T "LnkForgetPass")
$lnkClearPass.LinkColor = $OrangePending
$lnkClearPass.AutoSize = $true
$lnkClearPass.Location = New-Object System.Drawing.Point(205, 7)
$lnkClearPass.Add_Click({
    $script:SavedPasswords.Clear()
    Write-AppLog "Ulozena hesla v pameti byla vymazana."
})

$lnkExportImport = New-Object System.Windows.Forms.LinkLabel
$lnkExportImport.Text = (T "LnkExportImport")
$lnkExportImport.LinkColor = [System.Drawing.Color]::FromArgb(166, 227, 161)
$lnkExportImport.AutoSize = $true
$lnkExportImport.Location = New-Object System.Drawing.Point(330, 7)
$lnkExportImport.Add_Click({
    Show-ExportImportDialog
})

$lnkEdit = New-Object System.Windows.Forms.LinkLabel
$lnkEdit.Text = (T "LnkEditConfig")
$lnkEdit.LinkColor = [System.Drawing.Color]::FromArgb(137, 180, 250)
$lnkEdit.AutoSize = $true
$lnkEdit.Location = New-Object System.Drawing.Point(445, 7)
$lnkEdit.Add_Click({
    $cfg = Get-SshConfigPath
    if (Test-Path $cfg) { Start-Process "notepad.exe" -ArgumentList "`"$cfg`"" }
})

$lnkQuit = New-Object System.Windows.Forms.LinkLabel
$lnkQuit.Text = (T "LnkQuit")
$lnkQuit.LinkColor = $RedInactive
$lnkQuit.AutoSize = $true
$lnkQuit.Location = New-Object System.Drawing.Point(590, 7)
$lnkQuit.Add_Click({
    $script:AllowClose = $true
    Stop-AllTunnels
    $script:TrayIcon.Visible = $false
    $script:Form.Close()
})

$script:LogBox = New-Object System.Windows.Forms.TextBox
$script:LogBox.Multiline = $true
$script:LogBox.ReadOnly = $true
$script:LogBox.ScrollBars = [System.Windows.Forms.ScrollBars]::Vertical
$script:LogBox.BackColor = $BgLog
$script:LogBox.ForeColor = [System.Drawing.Color]::FromArgb(186, 194, 222)
$script:LogBox.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
$script:LogBox.Font = New-Object System.Drawing.Font("Consolas", 9)
$script:LogBox.Location = New-Object System.Drawing.Point(16, 30)
$script:LogBox.Size = New-Object System.Drawing.Size(710, 108)

$bottomPanel.Controls.Add($script:ChkAutoReconnect)
$bottomPanel.Controls.Add($lnkClearPass)
$bottomPanel.Controls.Add($lnkExportImport)
$bottomPanel.Controls.Add($lnkEdit)
$bottomPanel.Controls.Add($lnkQuit)
$bottomPanel.Controls.Add($script:LogBox)

# Zalozkovy panel (Mosty | Pripojeni / Terminal)
$tabBar = New-Object System.Windows.Forms.Panel
$tabBar.Dock = [System.Windows.Forms.DockStyle]::Top
$tabBar.Size = New-Object System.Drawing.Size(750, 38)
$tabBar.BackColor = $BgBottom

$script:BtnTabBridges = New-StyledButton "" $BlueAccent 185 30
$script:BtnTabBridges.Location = New-Object System.Drawing.Point(16, 4)
$script:BtnTabBridges.Add_Click({ Switch-Tab "bridges" })

$script:BtnTabConnections = New-StyledButton "" $BgCard 220 30
$script:BtnTabConnections.Location = New-Object System.Drawing.Point(209, 4)
$script:BtnTabConnections.ForeColor = $TextMuted
$script:BtnTabConnections.Add_Click({ Switch-Tab "connections" })

$script:BtnTabTerminal = New-StyledButton "" $BgCard 150 30
$script:BtnTabTerminal.Location = New-Object System.Drawing.Point(437, 4)
$script:BtnTabTerminal.ForeColor = $TextMuted
$script:BtnTabTerminal.Add_Click({ Switch-Tab "terminal" })

$tabBar.Controls.Add($script:BtnTabBridges)
$tabBar.Controls.Add($script:BtnTabConnections)
$tabBar.Controls.Add($script:BtnTabTerminal)

# Panel karet (Mosty)
$script:CardsPanel = New-Object System.Windows.Forms.FlowLayoutPanel
$script:CardsPanel.Dock = [System.Windows.Forms.DockStyle]::Fill
$script:CardsPanel.AutoScroll = $true
$script:CardsPanel.FlowDirection = [System.Windows.Forms.FlowDirection]::TopDown
$script:CardsPanel.WrapContents = $false
$script:CardsPanel.Padding = New-Object System.Windows.Forms.Padding(16, 14, 16, 14)
$script:CardsPanel.BackColor = $BgDark

# Panel pripojeni (Seznam serveru)
$script:ConnectionsPanel = New-Object System.Windows.Forms.FlowLayoutPanel
$script:ConnectionsPanel.Dock = [System.Windows.Forms.DockStyle]::Fill
$script:ConnectionsPanel.AutoScroll = $true
$script:ConnectionsPanel.FlowDirection = [System.Windows.Forms.FlowDirection]::TopDown
$script:ConnectionsPanel.WrapContents = $false
$script:ConnectionsPanel.Padding = New-Object System.Windows.Forms.Padding(16, 14, 16, 14)
$script:ConnectionsPanel.BackColor = $BgDark
$script:ConnectionsPanel.Visible = $false

# Panel vestaveneho terminalu
$script:TerminalPanel = New-Object System.Windows.Forms.Panel
$script:TerminalPanel.Dock = [System.Windows.Forms.DockStyle]::Fill
$script:TerminalPanel.BackColor = [System.Drawing.Color]::FromArgb(12, 12, 16)
$script:TerminalPanel.Visible = $false

$termToolbar = New-Object System.Windows.Forms.Panel
$termToolbar.Dock = [System.Windows.Forms.DockStyle]::Top
$termToolbar.Size = New-Object System.Drawing.Size(750, 40)
$termToolbar.BackColor = $BgCardHeader

$script:LblTermStatus = New-Object System.Windows.Forms.Label
$script:LblTermStatus.Text = (T "TabTerminal")
$script:LblTermStatus.Font = New-Object System.Drawing.Font("Segoe UI", 9.5, [System.Drawing.FontStyle]::Bold)
$script:LblTermStatus.ForeColor = $TextPrimary
$script:LblTermStatus.AutoSize = $true
$script:LblTermStatus.Location = New-Object System.Drawing.Point(16, 11)

$script:BtnCloseTerm = New-StyledButton "$([char]0x2715) $(T 'BtnCloseTerm')" $RedInactive 140 28
$script:BtnCloseTerm.Location = New-Object System.Drawing.Point(600, 6)
$script:BtnCloseTerm.Anchor = [System.Windows.Forms.AnchorStyles]::Top -bor [System.Windows.Forms.AnchorStyles]::Right
$script:BtnCloseTerm.Enabled = $false
$script:BtnCloseTerm.Add_Click({
    Close-EmbeddedTerminal
    $script:BtnExtTerm.Enabled = $false
})

$script:BtnExtTerm = New-StyledButton "$([char]0x2197) $(T 'BtnExternalTerm')" $GrayOff 135 28
$script:BtnExtTerm.Location = New-Object System.Drawing.Point(455, 6)
$script:BtnExtTerm.Anchor = [System.Windows.Forms.AnchorStyles]::Top -bor [System.Windows.Forms.AnchorStyles]::Right
$script:BtnExtTerm.Enabled = $false
$script:BtnExtTerm.Add_Click({
    if ($null -ne $script:TermConn) {
        Open-SshTerminal $script:TermConn $true
    }
})

$termToolbar.Controls.Add($script:LblTermStatus)
$termToolbar.Controls.Add($script:BtnExtTerm)
$termToolbar.Controls.Add($script:BtnCloseTerm)

$script:TermHostPanel = New-Object System.Windows.Forms.Panel
$script:TermHostPanel.Dock = [System.Windows.Forms.DockStyle]::Fill
$script:TermHostPanel.BackColor = [System.Drawing.Color]::FromArgb(12, 12, 16)
$script:TermHostPanel.Add_Resize({
    if ($script:TermHwnd -ne [IntPtr]::Zero -and $null -ne $script:TermProcess -and -not $script:TermProcess.HasExited) {
        [PontishTermEmbed]::MoveWindow($script:TermHwnd, 0, 0, $script:TermHostPanel.ClientSize.Width, $script:TermHostPanel.ClientSize.Height, $true) | Out-Null
    }
})

$script:LblTermEmpty = New-Object System.Windows.Forms.Label
$script:LblTermEmpty.Text = (T "TermNone")
$script:LblTermEmpty.Font = New-Object System.Drawing.Font("Segoe UI", 10)
$script:LblTermEmpty.ForeColor = $TextMuted
$script:LblTermEmpty.AutoSize = $true
$script:LblTermEmpty.Location = New-Object System.Drawing.Point(24, 24)
$script:TermHostPanel.Controls.Add($script:LblTermEmpty)

$script:TerminalPanel.Controls.Add($script:TermHostPanel)
$script:TerminalPanel.Controls.Add($termToolbar)

$script:Form.Controls.Add($script:CardsPanel)
$script:Form.Controls.Add($script:ConnectionsPanel)
$script:Form.Controls.Add($script:TerminalPanel)
$script:Form.Controls.Add($bottomPanel)
$script:Form.Controls.Add($tabBar)
$script:Form.Controls.Add($topBar)
$script:Form.Controls.Add($menuStrip)

function Update-TabButtons {
    $script:BtnTabBridges.Text = (T "TabBridges" @($script:Tunnels.Count))
    $script:BtnTabConnections.Text = (T "TabConnections" @($script:Connections.Count))
    $script:BtnTabTerminal.Text = ">_ $(T 'TabTerminal')"

    foreach ($pair in @(
        @{ Key = "bridges";     Btn = $script:BtnTabBridges },
        @{ Key = "connections"; Btn = $script:BtnTabConnections },
        @{ Key = "terminal";    Btn = $script:BtnTabTerminal }
    )) {
        if ($script:ActiveTab -eq $pair.Key) {
            $pair.Btn.BackColor = $BlueAccent
            $pair.Btn.ForeColor = [System.Drawing.Color]::White
        } else {
            $pair.Btn.BackColor = $BgCard
            $pair.Btn.ForeColor = $TextMuted
        }
    }
}

function Switch-Tab([string]$TabName) {
    $script:ActiveTab = $TabName
    $script:CardsPanel.Visible = ($TabName -eq "bridges")
    $script:ConnectionsPanel.Visible = ($TabName -eq "connections")
    $script:TerminalPanel.Visible = ($TabName -eq "terminal")
    Update-TabButtons
    if ($TabName -eq "terminal" -and $script:TermHwnd -ne [IntPtr]::Zero -and $null -ne $script:TermProcess -and -not $script:TermProcess.HasExited) {
        [PontishTermEmbed]::MoveWindow($script:TermHwnd, 0, 0, $script:TermHostPanel.ClientSize.Width, $script:TermHostPanel.ClientSize.Height, $true) | Out-Null
        [PontishTermEmbed]::SetForegroundWindow($script:TermHwnd) | Out-Null
    }
}

function Apply-Language([string]$Lang) {
    $script:CurrentLang = $Lang
    Save-AppSettings

    foreach ($k in $script:LangMenuItems.Keys) {
        $script:LangMenuItems[$k].Checked = ($k -eq $Lang)
    }

    $script:Form.Text = (T "WindowTitle" @($script:AppVersion))
    $miFile.Text = (T "MenuFile")
    $miFileReload.Text = (T "MenuReload")
    $miFileEdit.Text = (T "MenuEditConfig")
    $miFileExport.Text = (T "MenuExportImport")
    $miFileExit.Text = (T "MenuExit")
    $miBridges.Text = (T "MenuBridges")
    $miBridgesStartAll.Text = (T "MenuStartAll")
    $miBridgesStopAll.Text = (T "MenuStopAll")
    $miBridgesForget.Text = (T "MenuForgetPass")
    $miLang.Text = (T "MenuLanguage")

    $btnStartAll.Text = "$([char]0x25B6) $(T 'BtnStartAll')"
    $btnStopAll.Text = "$([char]0x25A0) $(T 'BtnStopAll')"
    $btnReload.Text = "$([char]0x21BB) $(T 'BtnReload')"

    $script:ChkAutoReconnect.Text = (T "ChkAutoReconnect")
    $lnkClearPass.Text = (T "LnkForgetPass")
    $lnkExportImport.Text = (T "LnkExportImport")
    $lnkEdit.Text = (T "LnkEditConfig")
    $lnkQuit.Text = (T "LnkQuit")

    $script:LblTermEmpty.Text = (T "TermNone")
    $script:BtnCloseTerm.Text = "$([char]0x2715) $(T 'BtnCloseTerm')"
    $script:BtnExtTerm.Text = "$([char]0x2197) $(T 'BtnExternalTerm')"
    if ($null -ne $script:TermConn) {
        $script:LblTermStatus.Text = (T "TermActive" @("$($script:TermConn.HostAlias) ($($script:TermConn.User)@$($script:TermConn.HostName))"))
    } else {
        $script:LblTermStatus.Text = (T "TabTerminal")
    }

    Build-CardsUI
    Build-ConnectionsUI
    Update-TabButtons
    Update-TunnelStates
    Write-AppLog (T "LangSwitched")
}

# System Tray
$script:TrayMenu = New-Object System.Windows.Forms.ContextMenuStrip
$script:TrayIcon = New-Object System.Windows.Forms.NotifyIcon
$script:TrayIcon.Text = "Pontish"
$script:TrayIcon.Icon = New-StatusIcon $false
$script:TrayIcon.ContextMenuStrip = $script:TrayMenu
$script:TrayIcon.Visible = $true
$script:TrayIcon.Add_DoubleClick({
    $script:Form.Show()
    $script:Form.WindowState = [System.Windows.Forms.FormWindowState]::Normal
    $script:Form.Activate()
})

$script:Form.Add_FormClosing({
    param($sender, $e)
    if (-not $script:AllowClose -and $e.CloseReason -eq [System.Windows.Forms.CloseReason]::UserClosing) {
        $e.Cancel = $true
        $script:Form.Hide()
        $script:TrayIcon.ShowBalloonTip(2000, (T "TrayBalloonTitle"), (T "TrayBalloonBody"), [System.Windows.Forms.ToolTipIcon]::Info)
    } else {
        Close-EmbeddedTerminal
    }
})

Read-SshTunnels
Build-CardsUI
Build-ConnectionsUI
Update-TabButtons
Write-AppLog (T "AppReady")
Update-TunnelStates

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 2000
$timer.Add_Tick({
    Update-TunnelStates
    if ($null -ne $script:TermProcess -and $script:TermProcess.HasExited) {
        Close-EmbeddedTerminal
        $script:BtnExtTerm.Enabled = $false
    }
})
$timer.Start()

$script:Form.Add_Shown({
    $script:Form.Activate()
})

[void][System.Windows.Forms.Application]::Run($script:Form)
