<#
.SYNOPSIS
    Release 产物 -> dist\QuickLook.Plugin.CraftViewer.qlplugin（ZIP 改扩展名，排除 *.pdb/*.xml）。
.DESCRIPTION
    包根必须含：QuickLook.Plugin.Metadata.config、QuickLook.Plugin.CraftViewer.dll、
    QuickLook.Common.dll、cli-config.json。产物校验：解包检查 Metadata Namespace 以
    QuickLook.Plugin. 开头（设计 §6.2）。
#>
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$BinDir = Join-Path $Root "plugin\QuickLook.Plugin.CraftViewer\bin\$Configuration"
$DistDir = Join-Path $Root 'dist'
$PluginPath = Join-Path $DistDir 'QuickLook.Plugin.CraftViewer.qlplugin'

if (-not (Test-Path -LiteralPath $BinDir)) {
    Write-Error "未找到构建产物目录：$BinDir（请先运行 scripts\build.ps1 -Configuration $Configuration）"
}

$required = @(
    'QuickLook.Plugin.Metadata.config',
    'QuickLook.Plugin.CraftViewer.dll',
    'QuickLook.Common.dll',
    'cli-config.json'
)
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $BinDir $_)) })
if ($missing.Count -gt 0) {
    Write-Error ("包根缺少必需文件：{0}" -f ($missing -join ', '))
}

if (Test-Path -LiteralPath $DistDir) { Remove-Item -LiteralPath $DistDir -Recurse -Force -Confirm:$false }
New-Item -ItemType Directory -Path $DistDir -Force | Out-Null

# Compress-Archive 不保留通配排除，先用干净 staging
$staging = Join-Path $DistDir '_staging'
New-Item -ItemType Directory -Path $staging -Force | Out-Null
Copy-Item -Path (Join-Path $BinDir '*') -Destination $staging -Recurse -Force
Get-ChildItem -LiteralPath $staging -Recurse -File |
    Where-Object { $_.Extension -in '.pdb', '.xml' } |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -Confirm:$false }

Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $PluginPath -Force
Remove-Item -LiteralPath $staging -Recurse -Force -Confirm:$false

# 产物校验：Metadata.config 在包根且 Namespace 以 QuickLook.Plugin. 开头
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($PluginPath)
try {
    $metaEntry = $zip.Entries | Where-Object { $_.FullName -eq 'QuickLook.Plugin.Metadata.config' } | Select-Object -First 1
    if ($null -eq $metaEntry) { Write-Error '校验失败：包根不含 QuickLook.Plugin.Metadata.config' }
    $reader = New-Object IO.StreamReader($metaEntry.Open(), [Text.Encoding]::UTF8)
    $metaText = $reader.ReadToEnd(); $reader.Dispose()
    if ($metaText -notmatch '<Namespace>\s*(QuickLook\.Plugin\.[^<]+)\s*</Namespace>') {
        Write-Error '校验失败：Metadata Namespace 不以 QuickLook.Plugin. 开头'
    }
    Write-Host ("校验通过：Namespace={0}" -f $Matches[1])
    Write-Host ("包内条目数：{0}" -f $zip.Entries.Count)
}
finally { $zip.Dispose() }

$size = (Get-Item -LiteralPath $PluginPath).Length
Write-Host "OK：$PluginPath（$size 字节）" -ForegroundColor Green
exit 0
