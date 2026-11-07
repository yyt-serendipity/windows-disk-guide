#requires -Version 5.1
param([switch]$Check, [string]$PreviewDirectory)
$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -LiteralPath $PSScriptRoot
$framework = @('Framework64','Framework') | ForEach-Object {
    Join-Path $env:WINDIR "Microsoft.NET\$_\v4.0.30319"
} | Where-Object {
    [IO.File]::Exists((Join-Path $_ 'csc.exe')) -and
    [IO.File]::Exists((Join-Path $_ 'WPF\PresentationFramework.dll'))
} | Select-Object -First 1
$release = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -Name Release -ErrorAction SilentlyContinue).Release
if (-not $framework -or $release -lt 528040) {
    throw 'Install .NET Framework 4.8 or later with its Windows desktop components before building.'
}
$compiler = Join-Path $framework 'csc.exe'
$stage = Join-Path $projectDir ('.build-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($stage)
$appFile = '磁盘清理助手.exe'
$binary = Join-Path $stage $appFile
$testBin = Join-Path $stage 'Check.exe'
$iconPath = Join-Path $PSScriptRoot 'Assets\app-icon.ico'
try {
    $common = @('/nologo','/optimize+','/warn:4','/codepage:65001','/platform:anycpu',
        ('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),('/win32icon:' + $iconPath),
        ('/resource:' + (Join-Path $PSScriptRoot 'Window.xaml') + ',Window.xaml'),
        ('/resource:' + (Join-Path $PSScriptRoot 'Assets\app-icon.png') + ',AppIcon.png'),
        ('/r:' + (Join-Path $framework 'WPF\PresentationFramework.dll')),('/r:' + (Join-Path $framework 'WPF\PresentationCore.dll')),
        ('/r:' + (Join-Path $framework 'WPF\WindowsBase.dll')),('/r:' + (Join-Path $framework 'System.Xaml.dll')),
        (Join-Path $PSScriptRoot 'App.cs'),(Join-Path $PSScriptRoot 'Catalog.cs'),(Join-Path $PSScriptRoot 'Cleaner.cs'),
        (Join-Path $PSScriptRoot 'Usage.cs'),(Join-Path $PSScriptRoot 'Browser.cs'),
        (Join-Path $PSScriptRoot 'StartupInventory.cs'),(Join-Path $PSScriptRoot 'SoftwareInventory.cs'),(Join-Path $PSScriptRoot 'Management.cs'),
        (Join-Path $PSScriptRoot 'AppSpace.cs'),(Join-Path $PSScriptRoot 'Space.cs'),(Join-Path $PSScriptRoot 'FilePreview.cs'),
        (Join-Path $PSScriptRoot 'Preserve.cs'),(Join-Path $PSScriptRoot 'ScanWork.cs'),(Join-Path $PSScriptRoot 'CleanupReview.cs'),
        (Join-Path $PSScriptRoot 'AppIcons.cs'),(Join-Path $PSScriptRoot 'Activity.cs'),(Join-Path $PSScriptRoot 'Home.cs'))
    & $compiler '/target:winexe' '/main:DiskGuide.Program' ('/out:' + $binary) @common
    if ($LASTEXITCODE -ne 0) {throw 'Build failed.'}
    if ($Check) {
        & $compiler '/target:exe' '/main:DiskGuide.Check' ('/out:' + $testBin) ('/r:' + (Join-Path $framework 'WPF\UIAutomationProvider.dll')) ('/r:' + (Join-Path $framework 'WPF\UIAutomationTypes.dll')) @common (Join-Path $PSScriptRoot 'Check.cs')
        if ($LASTEXITCODE -ne 0) {throw 'Test build failed.'}
        & $testBin $PreviewDirectory
        if ($LASTEXITCODE -ne 0) {throw 'Checks failed.'}
    }
    $out = Join-Path $projectDir $appFile
    if ([IO.File]::Exists($out)) {
        $backup = Join-Path $stage 'previous.exe'
        [IO.File]::Replace($binary,$out,$backup)
        try {[IO.File]::Delete($backup)}
        catch [UnauthorizedAccessException] {Write-Warning "New build installed. The previous executable is still in use; retained at $backup"}
        catch [IO.IOException] {Write-Warning "New build installed. Could not remove the previous executable; retained at $backup"}
    } else {[IO.File]::Move($binary,$out)}
    Get-Item -LiteralPath $out | Select-Object FullName,Length
} finally {
    foreach ($p in @($testBin,$binary)) {if ([IO.File]::Exists($p)) {[IO.File]::Delete($p)}}
    if ([IO.Directory]::Exists($stage) -and [IO.Directory]::GetFileSystemEntries($stage).Length -eq 0) {[IO.Directory]::Delete($stage)}
}
