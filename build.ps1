$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $here 'src\Drawer.cs'
$output = Join-Path $here 'Drawer.exe'

# 找 Windows 自带的 C# 编译器
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $csc)) { throw '找不到 Windows 自带的 C# 编译器 (csc.exe)。' }

function Find-Dll([string[]]$candidates, [string]$name) {
    foreach ($path in $candidates) { if (Test-Path -LiteralPath $path) { return $path } }
    throw ('找不到 ' + $name + '，无法编译。')
}

$win = $env:WINDIR
$core = Find-Dll @(
    "$win\Microsoft.NET\assembly\GAC_64\PresentationCore\v4.0_4.0.0.0__31bf3856ad364e35\PresentationCore.dll",
    "$win\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationCore.dll",
    "$win\Microsoft.NET\Framework\v4.0.30319\WPF\PresentationCore.dll") 'PresentationCore.dll'
$framework = Find-Dll @(
    "$win\Microsoft.NET\assembly\GAC_MSIL\PresentationFramework\v4.0_4.0.0.0__31bf3856ad364e35\PresentationFramework.dll",
    "$win\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationFramework.dll",
    "$win\Microsoft.NET\Framework\v4.0.30319\WPF\PresentationFramework.dll") 'PresentationFramework.dll'
$base = Find-Dll @(
    "$win\Microsoft.NET\assembly\GAC_MSIL\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll",
    "$win\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll",
    "$win\Microsoft.NET\Framework\v4.0.30319\WPF\WindowsBase.dll") 'WindowsBase.dll'
$xaml = Find-Dll @(
    "$win\Microsoft.NET\assembly\GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll",
    "$win\Microsoft.NET\Framework64\v4.0.30319\System.Xaml.dll",
    "$win\Microsoft.NET\Framework\v4.0.30319\System.Xaml.dll") 'System.Xaml.dll'

# 如果上一版还在运行，先关掉，不然 exe 会被占用
Get-Process -Name 'Drawer' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

Write-Host '正在编译...'
& $csc /nologo /target:winexe /optimize+ /codepage:65001 "/out:$output" `
    "/reference:$core" "/reference:$framework" "/reference:$base" "/reference:$xaml" `
    /reference:System.dll /reference:System.Core.dll `
    $source
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host '编译失败了。请记录上面的红字或错误信息，以便排查问题。' -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host '编译成功！正在启动抽屉：把鼠标移到屏幕最左边试试。' -ForegroundColor Green
Write-Host '退出方法：在抽屉上右键，选“退出”。'
Start-Process -FilePath $output
