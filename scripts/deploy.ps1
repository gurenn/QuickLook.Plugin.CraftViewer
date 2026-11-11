<#
.SYNOPSIS
    便携版实机部署脚本：快照 / 部署 / 重启宿主 / 回滚（设计 §1#6、§6.3，T05 由 QA 联调）。
.DESCRIPTION
    -Snapshot            生成插件目录清单+SHA256 快照（部署前后均可，支持回滚对照）
    -Deploy              Stop-Process QuickLook（用户已授权）-> 覆盖插件目录 -> 重启宿主
    -Rollback            删除插件目录 -> 重启宿主 -> .vectorcraft/.pcraft 恢复 InfoPanel 基线
    部署前比对实机 QuickLook.Common.dll 与插件构建所用版本的文件版本，不一致输出醒目告警但不阻塞。
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1 -Deploy
#>
param(
    [switch]$Snapshot,
    [switch]$Deploy,
    [switch]$Rollback,
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$QuickLookExe = 'D:\Program Files\QuickLook\QuickLook.exe',
    [string]$UserPluginRoot = 'D:\Program Files\QuickLook\UserData\QuickLook.Plugin',
    [string]$PluginDirName = 'QuickLook.Plugin.CraftViewer'
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$BinDir = Join-Path $Root "plugin\QuickLook.Plugin.CraftViewer\bin\$Configuration"
$TargetDir = Join-Path $UserPluginRoot $PluginDirName
$SnapshotDir = Join-Path $Root 'dist\snapshots'
$LogPath = Join-Path $SnapshotDir 'deploy.log'

function Write-Log([string]$msg) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
    Write-Host $line
    Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
}

function New-Snapshot([string]$label) {
    New-Item -ItemType Directory -Path $SnapshotDir -Force | Out-Null
    $file = Join-Path $SnapshotDir ("snapshot_{0}_{1}.csv" -f $label, (Get-Date -Format 'yyyyMMdd_HHmmss'))
    if (Test-Path -LiteralPath $TargetDir) {
        Get-ChildItem -LiteralPath $TargetDir -Recurse -File | ForEach-Object {
            $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
            [PSCustomObject]@{
                RelativePath = $_.FullName.Substring($TargetDir.Length + 1)
                Length       = $_.Length
                SHA256       = $hash.Hash
            }
        } | Export-Csv -LiteralPath $file -NoTypeInformation -Encoding UTF8
    }
    else {
        [PSCustomObject]@{ RelativePath = '<目录不存在>'; Length = 0; SHA256 = '' } |
            Export-Csv -LiteralPath $file -NoTypeInformation -Encoding UTF8
    }
    Write-Log "快照[$label] -> $file"
    return $file
}

function Stop-QuickLook {
    $p = Get-Process -Name QuickLook -ErrorAction SilentlyContinue
    if ($p) {
        Write-Log "停止 QuickLook（PID: $($p.Id -join ',')）——用户已授权"
        $p | Stop-Process -Force -Confirm:$false
        Start-Sleep -Seconds 2
    }
    else { Write-Log 'QuickLook 未在运行' }
}

function Start-QuickLook {
    if (Test-Path -LiteralPath $QuickLookExe) {
        Write-Log "启动 QuickLook：$QuickLookExe"
        Start-Process -FilePath $QuickLookExe
    }
    else { Write-Log "警告：未找到 $QuickLookExe，请手动启动宿主" }
}

New-Item -ItemType Directory -Path $SnapshotDir -Force | Out-Null

if ($Snapshot) { New-Snapshot 'manual' | Out-Null; exit 0 }

if ($Rollback) {
    Write-Log '==== 回滚开始'
    Stop-QuickLook
    if (Test-Path -LiteralPath $TargetDir) {
        Remove-Item -LiteralPath $TargetDir -Recurse -Force -Confirm:$false
        Write-Log "已删除插件目录：$TargetDir"
    }
    Start-QuickLook
    New-Snapshot 'after-rollback' | Out-Null
    Write-Log '==== 回滚完成（.vectorcraft/.pcraft 恢复 InfoPanel 基线）'
    exit 0
}

if ($Deploy) {
    if (-not (Test-Path -LiteralPath $BinDir)) {
        Write-Error "未找到构建产物：$BinDir（请先 build.ps1 -Configuration $Configuration）"
    }

    # 版本比对告警（不阻塞）
    $hostCommon = Join-Path (Split-Path -Parent $QuickLookExe) 'QuickLook.Common.dll'
    $builtCommon = Join-Path $BinDir 'QuickLook.Common.dll'
    if ((Test-Path -LiteralPath $hostCommon) -and (Test-Path -LiteralPath $builtCommon)) {
        $hv = (Get-Item -LiteralPath $hostCommon).VersionInfo.FileVersion
        $bv = (Get-Item -LiteralPath $builtCommon).VersionInfo.FileVersion
        $bpv = (Get-Item -LiteralPath $builtCommon).VersionInfo.ProductVersion
        # vendored QuickLook.Common 无 GitVersion stub，FileVersion=0.0.0.0；
        # 此时回退用 ProductVersion 中嵌入的宿主版本串（如 "0.0.0.04.5.0.0" 含 4.5.0.0）比对
        if ($bv -match '^0(\.0)+$' -and $bpv -and $bpv.Contains($hv)) {
            Write-Log ("QuickLook.Common 版本一致（vendored ProductVersion 含宿主版本 {0}）" -f $hv)
        }
        elseif ($hv -ne $bv) {
            Write-Host ("警告：QuickLook.Common 版本不一致！宿主={0} 插件携带={1}（人工确认后再继续）" -f $hv, $bv) -ForegroundColor Yellow
            Write-Log "警告：QuickLook.Common 版本不一致（宿主=$hv / 插件=$bv）"
        }
        else { Write-Log "QuickLook.Common 版本一致：$hv" }
    }

    Write-Log '==== 部署开始'
    New-Snapshot 'before-deploy' | Out-Null
    Stop-QuickLook

    # 保留用户现场配置：cli-config.json 属于部署现场而非构建产物（QA 建议#4 关联教训）
    $savedConfig = $null
    if (Test-Path -LiteralPath $TargetDir) {
        $cfgPath = Join-Path $TargetDir 'cli-config.json'
        if (Test-Path -LiteralPath $cfgPath) {
            $savedConfig = Get-Content -LiteralPath $cfgPath -Raw
            Write-Log '已保留现有 cli-config.json'
        }
        Remove-Item -LiteralPath $TargetDir -Recurse -Force -Confirm:$false
    }
    New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
    Copy-Item -Path (Join-Path $BinDir '*') -Destination $TargetDir -Recurse -Force
    if ($savedConfig) {
        Set-Content -LiteralPath (Join-Path $TargetDir 'cli-config.json') -Value $savedConfig -Encoding UTF8
    }
    Write-Log "已复制产物 -> $TargetDir"

    Start-QuickLook
    New-Snapshot 'after-deploy' | Out-Null
    Write-Log '==== 部署完成'
    exit 0
}

Write-Host '用法：deploy.ps1 -Snapshot | -Deploy | -Rollback [-Configuration Release]'
exit 2
