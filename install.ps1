# Claude 启动动画：装到这台电脑（在桌面建「Claude」图标，指向本文件夹里的 ClaudeSplash.exe）
# 由「安装到这台电脑.bat」调用；挪了文件夹位置，再运行一次就行
$dir = $PSScriptRoot
$exe = Join-Path $dir 'ClaudeSplash.exe'
Write-Host ''
Write-Host '=== Claude 启动动画 · 安装 ===' -ForegroundColor Cyan
Write-Host "文件夹：$dir"
Write-Host ''

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host '找不到 ClaudeSplash.exe：请确认这个脚本和 ClaudeSplash.exe 在同一个文件夹里。' -ForegroundColor Red
    exit 1
}
if ($dir -like "$([Environment]::GetFolderPath('UserProfile'))\Downloads*") {
    Write-Host '提醒：文件夹现在在「下载」里，以后清理下载会把动画删掉，桌面图标就失效了。' -ForegroundColor Yellow
    Write-Host '      建议先把整个文件夹挪到固定的地方（比如 D:\Claude启动动画），再双击安装。' -ForegroundColor Yellow
    Write-Host ''
}

# 1. 检查 Claude 桌面版（动画同时会在后台打开它）
$pkg = $null
try { $pkg = Get-AppxPackage -Name Claude -ErrorAction Stop | Select-Object -First 1 } catch { }
if ($pkg) {
    Write-Host "[1/3] 已找到 Claude 桌面版 $($pkg.Version)" -ForegroundColor Green
} else {
    Write-Host '[1/3] 这台电脑没找到 Claude 桌面版：动画能播，但播完打不开 Claude。' -ForegroundColor Yellow
    Write-Host '      请先到 claude.ai/download 装好 Claude，再双击安装一次。' -ForegroundColor Yellow
}

# 2. 去掉「从网上下载的文件」标记，不然双击会被 SmartScreen 拦
Get-ChildItem -LiteralPath $dir -Recurse -File | Unblock-File
Write-Host '[2/3] 已去掉下载标记' -ForegroundColor Green

# 3. 桌面建「Claude」图标；桌面原来的 Claude 图标先在本文件夹留一份底
$desktop = [Environment]::GetFolderPath('Desktop')
$lnk = Join-Path $desktop 'Claude.lnk'
$sh = New-Object -ComObject WScript.Shell
if (Test-Path -LiteralPath $lnk) {
    $old = $sh.CreateShortcut($lnk)
    if ($old.TargetPath -notlike '*ClaudeSplash.exe') {
        $bak = Join-Path $dir '原版Claude快捷方式(这台电脑).lnk'
        Copy-Item -LiteralPath $lnk -Destination $bak -Force
        Write-Host "      桌面原来的 Claude 图标已留底：$bak"
    }
}
$s = $sh.CreateShortcut($lnk)
$s.TargetPath = $exe
$s.WorkingDirectory = $dir
$s.IconLocation = "$exe,0"
$s.Description = 'Claude（带启动动画）'
$s.Save()
Write-Host "[3/3] 桌面图标建好了：$lnk" -ForegroundColor Green

$pub = Join-Path $env:PUBLIC 'Desktop\Claude.lnk'
if (Test-Path -LiteralPath $pub) {
    Write-Host ''
    Write-Host '提醒：公共桌面上还有一个 Claude 原版图标（装 Claude 时建的），桌面上会看到两个「Claude」。' -ForegroundColor Yellow
    Write-Host '      不想要就在桌面上右键删掉那个原版的（会要管理员确认）。' -ForegroundColor Yellow
}

Write-Host ''
Write-Host '装好了！以后双击桌面「Claude」就会先播动画。' -ForegroundColor Cyan
$ans = Read-Host '现在试播一下动画吗？（不会打开 Claude，播完按任意键关掉）输入 Y 回车试播，直接回车结束'
if ($ans -match '^[Yy]') { Start-Process -FilePath $exe -ArgumentList '--preview' -WorkingDirectory $dir }
