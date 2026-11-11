<#
.SYNOPSIS
    统一构建入口：QuickLook.Common -> 插件 -> 探针，逐工程 Restore+Build，任一失败即非零退出。
.DESCRIPTION
    实测环境结论（与原裁决偏差，见交付说明）：
    本机 VS2022 BuildTools 的 MSBuild 目录树中不存在任何 MSBuild SDK（Sdks/ 目录缺失，
    NuGetSdkResolver 无法解析 Microsoft.NET.Sdk——该 SDK 不是 NuGet 包），亦无 WinFX/WPF
    旧式 targets，SDK 风格 + UseWPF 工程无法用它构建。
    因此本脚本使用 workspace 本地 .NET SDK（D:\Developer\WorkBuddy_Workspace\dotnet-sdk，
    经 dotnet-install.ps1 安装，无需管理员）内嵌的同一 MSBuild 引擎：
        dotnet.exe msbuild <proj> /t:Restore /t:Build
    若该 SDK 不存在则报错退出，绝不静默切换到不可用的 BuildTools MSBuild。
.PARAMETER Configuration
    Debug（默认）或 Release。
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Configuration Release
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

# 本地 .NET SDK（内嵌 MSBuild 引擎）；安装方式见 scripts/NOTICE.md 与交付说明
$LocalDotNet = 'D:\Developer\WorkBuddy_Workspace\dotnet-sdk\dotnet.exe'
if (-not (Test-Path -LiteralPath $LocalDotNet)) {
    Write-Error (@"
未找到本地 .NET SDK：$LocalDotNet
本机 BuildTools MSBuild 无任何 MSBuild SDK（Sdks/ 缺失）且无 WinFX targets，无法构建 SDK 风格 WPF 工程。
请先安装到 workspace 本地（无需管理员）：
  & `$env:TEMP\dotnet-install.ps1 -Channel 8.0 -Version 8.0.404 -InstallDir D:\Developer\WorkBuddy_Workspace\dotnet-sdk
"@)
}

$Root = Split-Path -Parent $PSScriptRoot
$Projects = @(
    (Join-Path $Root 'QuickLook.Common\QuickLook.Common.csproj'),
    (Join-Path $Root 'plugin\QuickLook.Plugin.CraftViewer\QuickLook.Plugin.CraftViewer.csproj'),
    (Join-Path $Root 'probe\ProbeCraftFormats.csproj')
)

foreach ($proj in $Projects) {
    if (-not (Test-Path -LiteralPath $proj)) {
        Write-Error "工程文件缺失：$proj（若为 QuickLook.Common，请先运行 scripts\fetch-quicklook-common.ps1）"
    }
}

$failed = $false
foreach ($proj in $Projects) {
    Write-Host "==== Restore: $proj" -ForegroundColor Cyan
    & $LocalDotNet msbuild $proj /t:Restore /p:RestorePackagesConfig=true /v:m /nologo
    if ($LASTEXITCODE -ne 0) { $failed = $true; Write-Host "Restore 失败：$proj" -ForegroundColor Red; break }

    Write-Host "==== Build [$Configuration]: $proj" -ForegroundColor Cyan
    & $LocalDotNet msbuild $proj /t:Build "/p:Configuration=$Configuration" /v:m /nologo
    if ($LASTEXITCODE -ne 0) { $failed = $true; Write-Host "Build 失败：$proj" -ForegroundColor Red; break }
}

if ($failed) { exit 1 }

Write-Host ''
Write-Host '全部工程构建成功。' -ForegroundColor Green
Write-Host ("插件产物：{0}" -f (Join-Path $Root 'plugin\QuickLook.Plugin.CraftViewer\bin'))
Write-Host ("探针产物：{0}" -f (Join-Path $Root 'probe\bin\Release\ProbeCraftFormats.exe'))
exit 0
