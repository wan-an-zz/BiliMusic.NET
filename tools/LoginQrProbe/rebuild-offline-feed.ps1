<#
.SYNOPSIS
    从本机 NuGet 缓存重建 LoginQrProbe 使用的离线包源。

.DESCRIPTION
    当前开发环境的 nuget.org 不可达，`dotnet run` 无法联网还原。
    本脚本把 %USERPROFILE%\.nuget\packages 下已缓存的 .nupkg 汇集到 .nuget-offline-feed/，
    使探针完全离线可用（该目录已被 .gitignore 忽略，不入库）。

.EXAMPLE
    pwsh -File tools\LoginQrProbe\rebuild-offline-feed.ps1
#>
[CmdletBinding()]
param(
    [string] $CacheRoot = (Join-Path $env:USERPROFILE '.nuget\packages'),
    [string] $FeedDir = (Join-Path $PSScriptRoot '.nuget-offline-feed')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $CacheRoot)) {
    throw "找不到 NuGet 缓存目录：$CacheRoot"
}

New-Item -ItemType Directory -Force -Path $FeedDir | Out-Null

$copied = 0
foreach ($package in @('microsoft.extensions.logging.abstractions')) {
    $packageDir = Join-Path $CacheRoot $package
    if (-not (Test-Path $packageDir)) {
        Write-Warning "缓存中缺少包目录，跳过：$package"
        continue
    }

    Get-ChildItem $packageDir -Recurse -Filter *.nupkg |
        Where-Object { $_.Name -notmatch '\.symbols\.nupkg$' } |
        ForEach-Object {
            Copy-Item $_.FullName -Destination $FeedDir -Force
            $copied++
        }
}

Write-Host "已汇集 $copied 个 .nupkg 到 $FeedDir"
