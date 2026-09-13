<#
.SYNOPSIS
    从本机 NuGet 缓存重建测试项目使用的离线包源。

.DESCRIPTION
    当前开发环境的 nuget.org 不可达（TLS 被拦截），`dotnet test` 无法联网还原测试依赖。
    本脚本把 %USERPROFILE%\.nuget\packages 下已缓存的 .nupkg 汇集到 .nuget-offline-feed/，
    使 `dotnet test` 完全离线可用（该目录已被 .gitignore 忽略，不入库）。

    联网环境下不需要运行本脚本：删掉 BiliMusicDotNet.Tests\NuGet.config 即走官方源。

.EXAMPLE
    pwsh -File BiliMusicDotNet.Tests\rebuild-offline-feed.ps1
#>
[CmdletBinding()]
param(
    # NuGet 缓存根目录，默认取用户 profile 下的标准位置。
    [string] $CacheRoot = (Join-Path $env:USERPROFILE '.nuget\packages'),

    # 输出目录，默认是脚本同级的 .nuget-offline-feed。
    [string] $FeedDir = (Join-Path $PSScriptRoot '.nuget-offline-feed')
)

$ErrorActionPreference = 'Stop'

$packages = @(
    'xunit'
    'xunit.core'
    'xunit.assert'
    'xunit.analyzers'
    'xunit.abstractions'
    'xunit.extensibility.core'
    'xunit.extensibility.execution'
    'xunit.runner.visualstudio'
    'microsoft.net.test.sdk'
    'microsoft.testplatform.testhost'
    'microsoft.codecoverage'
)

if (-not (Test-Path $CacheRoot)) {
    throw "找不到 NuGet 缓存目录：$CacheRoot"
}

New-Item -ItemType Directory -Force -Path $FeedDir | Out-Null

$copied = 0
foreach ($package in $packages) {
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
