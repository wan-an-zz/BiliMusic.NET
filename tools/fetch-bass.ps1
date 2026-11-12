[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repoRoot = Split-Path -Parent $PSScriptRoot
$cacheRoot = Join-Path $PSScriptRoot '.cache/bass'
$extractRoot = Join-Path $cacheRoot 'extracted'
New-Item -ItemType Directory -Force -Path $cacheRoot, $extractRoot | Out-Null

$androidAbis = @('arm64-v8a', 'armeabi-v7a', 'x86', 'x86_64')

$windowsTargets = @(
    (Join-Path $repoRoot 'tools/MusicProbe/native/win-x64/bass.dll'),
    (Join-Path $repoRoot 'BiliMusicDotNet.Desktop/native/win-x64/bass.dll')
)

$androidTargets = foreach ($abi in $androidAbis) {
    $dir = Join-Path $repoRoot "BiliMusicDotNet.Android/native/android/$abi"
    (Join-Path $dir 'libbass.so')
    (Join-Path $dir 'libbass_ssl.so')
}

$allTargets = @($windowsTargets) + @($androidTargets)
$missing = @($allTargets | Where-Object { -not (Test-Path $_) })

if (-not $Force -and $missing.Count -eq 0) {
    Write-Host '原生库已就位，跳过下载（需要重新下载时加 -Force）。'
    $allTargets | ForEach-Object { Write-Host ("  {0} ({1:N0} 字节)" -f $_, (Get-Item $_).Length) }
    exit 0
}

function Get-Archive {
    param([string]$Name, [string]$Url)

    $path = Join-Path $cacheRoot $Name
    if ($Force -or -not (Test-Path $path)) {
        Write-Host "下载 $Url"
        Invoke-WebRequest -Uri $Url -OutFile $path -UseBasicParsing
    }

    $destination = Join-Path $extractRoot ([IO.Path]::GetFileNameWithoutExtension($Name))
    if ($Force -and (Test-Path $destination)) {
        Remove-Item -Recurse -Force $destination
    }

    if (-not (Test-Path $destination)) {
        Write-Host "解压 $Name"
        Expand-Archive -Path $path -DestinationPath $destination -Force
    }

    return $destination
}

function Find-NativeFile {
    param(
        [string]$Root,
        [string]$Filter,
        [string]$Abi,
        [switch]$PreferX64
    )

    $files = @(Get-ChildItem -Path $Root -Recurse -File -Filter $Filter)
    if ($files.Count -eq 0) {
        return $null
    }

    if ($PreferX64) {
        $x64 = $files | Where-Object { $_.FullName -match '[\\/]x64[\\/]' } | Select-Object -First 1
        if ($x64) {
            return $x64.FullName
        }
    }

    if ($Abi) {
        $matched = $files | Where-Object { $_.FullName -match "[\\/]$([regex]::Escape($Abi))[\\/]" } | Select-Object -First 1
        if ($matched) {
            return $matched.FullName
        }
    }

    return $files[0].FullName
}

function Install-NativeFile {
    param([string]$Source, [string]$Destination)

    if (-not (Test-Path $Source)) {
        throw "找不到原生库：$Source"
    }

    $directory = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    Copy-Item -Path $Source -Destination $Destination -Force
    Write-Host ("  写入 {0} ({1:N0} 字节)" -f $Destination, (Get-Item $Destination).Length)
}

Write-Host '== 获取 BASS 原生库 =='
$windowsRoot = Get-Archive -Name 'bass24.zip' -Url 'https://www.un4seen.com/files/bass24.zip'
$bassDll = Find-NativeFile -Root $windowsRoot -Filter 'bass.dll' -PreferX64
if (-not $bassDll) {
    throw 'bass24.zip 中没有找到 bass.dll。'
}

foreach ($target in $windowsTargets) {
    Install-NativeFile -Source $bassDll -Destination $target
}

$androidRoot = Get-Archive -Name 'bass24-android.zip' -Url 'https://www.un4seen.com/files/bass24-android.zip'
$sslRoot = Get-Archive -Name 'bass_ssl-android.zip' -Url 'https://www.un4seen.com/files/bass_ssl-android.zip'

foreach ($abi in $androidAbis) {
    $bass = Find-NativeFile -Root $androidRoot -Filter 'libbass.so' -Abi $abi
    $ssl = Find-NativeFile -Root $sslRoot -Filter 'libbass_ssl.so' -Abi $abi
    if (-not $bass -or -not $ssl) {
        Write-Warning "跳过 ABI $abi：libbass.so=$(if ($bass) { '有' } else { '缺' })，libbass_ssl.so=$(if ($ssl) { '有' } else { '缺' })"
        continue
    }

    $directory = Join-Path $repoRoot "BiliMusicDotNet.Android/native/android/$abi"
    Install-NativeFile -Source $bass -Destination (Join-Path $directory 'libbass.so')
    Install-NativeFile -Source $ssl -Destination (Join-Path $directory 'libbass_ssl.so')
}

Write-Host ''
Write-Host '完成。原生库目录已被 .gitignore 忽略，不要提交二进制文件。'
