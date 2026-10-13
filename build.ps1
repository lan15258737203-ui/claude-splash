# Rebuild ClaudeSplash.exe with the C# compiler that ships with Windows (.NET Framework 4)
$fw  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$wpf = "$fw\WPF"
Set-Location $PSScriptRoot
& "$fw\csc.exe" /nologo /target:winexe /platform:x64 /optimize /codepage:65001 /nowarn:618 `
    /win32icon:claude.ico /out:ClaudeSplash.exe `
    /r:"$wpf\PresentationFramework.dll" /r:"$wpf\PresentationCore.dll" /r:"$wpf\WindowsBase.dll" /r:"$fw\System.Xaml.dll" `
    /r:"$fw\System.Windows.Forms.dll" /r:"$fw\System.Drawing.dll" `
    ClaudeSplash.cs
exit $LASTEXITCODE
