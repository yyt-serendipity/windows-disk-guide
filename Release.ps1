#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$dist = Join-Path $PSScriptRoot 'dist'
$null = [IO.Directory]::CreateDirectory($dist)
if ([IO.File]::GetAttributes($dist) -band [IO.FileAttributes]::ReparsePoint) {
    throw 'Choose a checkout with a regular dist directory.'
}

function Assert-PlainFile([string]$path) {
    $item = Get-Item -LiteralPath $path -Force
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "A regular file is required: $path"
    }
}

function Install-Artifact([string]$sourceFile, [string]$destinationFile) {
    if ([IO.File]::Exists($destinationFile)) {
        Assert-PlainFile $destinationFile
        $previous = $sourceFile + '.previous'
        [IO.File]::Replace($sourceFile, $destinationFile, $previous)
        [IO.File]::Delete($previous)
    } else {
        [IO.File]::Move($sourceFile, $destinationFile)
    }
}

# Keep concurrent packagers from publishing unrelated ZIP/checksum pairs.
$lock = [IO.FileStream]::new((Join-Path $dist '.release.lock'), [IO.FileMode]::CreateNew,
    [IO.FileAccess]::Write, [IO.FileShare]::None, 4096, [IO.FileOptions]::DeleteOnClose)
$stage = Join-Path $dist ('.release-' + [Guid]::NewGuid().ToString('N'))
$zipTemp = $stage + '.zip'
$sumTemp = $stage + '.txt'
try {
    $readme = Join-Path $PSScriptRoot 'README.md'
    Assert-PlainFile $readme
    & (Join-Path $PSScriptRoot '源码\Build.ps1') -Check
    $exe = Join-Path $PSScriptRoot '磁盘清理助手.exe'
    Assert-PlainFile $exe
    $version = [Reflection.AssemblyName]::GetAssemblyName($exe).Version.ToString(3)
    $zipName = "DiskGuide-$version-windows.zip"
    $payload = @($exe, $readme)
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # The Framework ZIP writer checks the Encoding.UTF8 singleton when setting the UTF-8 filename bit.
    $zip = [IO.Compression.ZipFile]::Open($zipTemp, [IO.Compression.ZipArchiveMode]::Create, [Text.Encoding]::UTF8)
    try {
        foreach ($file in $payload) {
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file, [IO.Path]::GetFileName($file),
                [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally {
        $zip.Dispose()
    }

    # Independently read the finished ZIP, checking exact entry names and bytes.
    $zip = [IO.Compression.ZipFile]::OpenRead($zipTemp)
    try {
        if ($zip.Entries.Count -ne $payload.Count) { throw 'Unexpected ZIP entries.' }
        foreach ($file in $payload) {
            $entry = $zip.GetEntry([IO.Path]::GetFileName($file))
            if (-not $entry) { throw "Missing ZIP entry: $file" }
            $stream = $entry.Open()
            try { $packed = (Get-FileHash -Algorithm SHA256 -InputStream $stream).Hash }
            finally { $stream.Dispose() }
            if ($packed -ne (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash) {
                throw "ZIP integrity check failed for: $file"
            }
        }
    } finally {
        $zip.Dispose()
    }
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipTemp).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($sumTemp, ($hash + '  ' + $zipName + [char]10), [Text.Encoding]::ASCII)
    Install-Artifact $zipTemp (Join-Path $dist $zipName)
    Install-Artifact $sumTemp (Join-Path $dist 'SHA256SUMS.txt')
    Write-Output "Verified: $zipName  SHA-256 $hash"
} finally {
    foreach ($p in @($zipTemp, $sumTemp)) { if ([IO.File]::Exists($p)) { [IO.File]::Delete($p) } }
    $lock.Dispose()
}
