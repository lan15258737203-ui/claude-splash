# Claude splash animation - one-line online installer.
# Paste into Windows PowerShell:
#   [Net.ServicePointManager]::SecurityProtocol=[Net.ServicePointManager]::SecurityProtocol -bor 3072; irm https://raw.githubusercontent.com/lan15258737203-ui/claude-splash/main/online-install.ps1 | iex
# Downloads the repo ZIP, copies it to D:\Claude... (or the user folder when there is no D: disk), then runs install.ps1.
# This file stays pure ASCII (Chinese text is \u-escaped and decoded by T) so it works whatever encoding irm picks.
# No "exit" anywhere: it runs inside the user's PowerShell window via iex, and exit would close that window.
& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072
    function T([string]$s) { [regex]::Unescape($s) }

    $zipUrl = 'https://github.com/lan15258737203-ui/claude-splash/archive/refs/heads/main.zip'
    if ($env:CLAUDESPLASH_DEST) { $dest = $env:CLAUDESPLASH_DEST }
    else {
        $d = [IO.DriveInfo]::GetDrives() | Where-Object { $_.Name -eq 'D:\' -and $_.DriveType -eq 'Fixed' -and $_.IsReady }
        $base = if ($d) { 'D:\' } else { $env:USERPROFILE }
        $dest = Join-Path $base (T 'Claude\u542f\u52a8\u52a8\u753b')
    }

    Write-Host ''
    Write-Host (T '\u6b63\u5728\u4e0b\u8f7d Claude \u542f\u52a8\u52a8\u753b\uff08\u7ea6 30MB\uff0c\u7f51\u6162\u8981\u7b49\u4e00\u4e24\u5206\u949f\uff09\u2026\u2026') -ForegroundColor Cyan
    $tmp = Join-Path $env:TEMP ('claude-splash-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        $zip = Join-Path $tmp 'main.zip'
        try { Invoke-WebRequest -Uri $zipUrl -OutFile $zip -UseBasicParsing }
        catch {
            Write-Host ((T '\u4e0b\u8f7d\u5931\u8d25\uff1a') + $_.Exception.Message) -ForegroundColor Red
            Write-Host (T '\u591a\u534a\u662f\u8fde\u4e0d\u4e0a GitHub\uff1a\u5f00\u4e00\u4e0b\u52a0\u901f\u5668/VPN \u518d\u7c98\u8d34\u4e00\u6b21\uff1b\u6216\u8005\u6253\u5f00\u4ed3\u5e93\u7f51\u9875\uff0c\u70b9\u7eff\u8272 Code \u518d\u70b9 Download ZIP \u624b\u52a8\u4e0b\u8f7d\u3002') -ForegroundColor Yellow
            return
        }
        Expand-Archive -LiteralPath $zip -DestinationPath $tmp -Force
        $src = Join-Path $tmp 'claude-splash-main'

        # An older copy in the same folder may be playing (or showing the wallpaper): close it so its exe can be replaced
        Get-Process ClaudeSplash -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($dest + '\', [StringComparison]::OrdinalIgnoreCase) } |
            Stop-Process -Force
        Start-Sleep -Milliseconds 500

        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Copy-Item -Path (Join-Path $src '*') -Destination $dest -Recurse -Force
        Write-Host ((T '\u5df2\u653e\u5230\uff1a') + $dest) -ForegroundColor Green
    }
    catch {
        Write-Host ((T '\u51fa\u9519\u4e86\uff1a') + $_.Exception.Message) -ForegroundColor Red
        return
    }
    finally { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }

    # install.ps1 has Chinese text and must run as a file (-File reads its BOM); Bypass because Windows blocks .ps1 files by default
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $dest 'install.ps1'))
    if ($env:CLAUDESPLASH_DESKTOP) { $a += @('-Desktop', $env:CLAUDESPLASH_DESKTOP) }
    if ($env:CLAUDESPLASH_NOPROMPT) { $a += '-NoPrompt' }
    & powershell.exe @a
}
