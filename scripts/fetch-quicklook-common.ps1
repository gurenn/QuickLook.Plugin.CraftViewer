<#
.SYNOPSIS
    从 QL-Win/QuickLook 上游拉取与实机宿主版本（v4.5.0）匹配的 QuickLook.Common 完整源码。
.DESCRIPTION
    锚定流程（全部走 https://api.github.com，raw.githubusercontent.com 在本机不可靠）：
      1. 解析宿主 tag（v4.5.0 -> 4.5.0 -> releases API 兜底）得到 QuickLook 主仓库 commit；
      2. 读该 commit 树中 QuickLook.Common 子模块项（type=submodule）得到子模块 commit SHA；
      3. 用该 SHA 在 QL-Win/QuickLook.Common 上 git trees(recursive) 列出 blob，
         逐个经 git/blobs API 下载（base64），落盘到 <根>\QuickLook\Common。
    禁止使用 master HEAD（上游已挂存档通告）。失败时保留明确报错，绝不静默编造源码。
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\fetch-quicklook-common.ps1
#>
param(
    [string]$HostRepo = 'QL-Win/QuickLook',
    [string]$CommonRepo = 'QL-Win/QuickLook.Common',
    [string[]]$HostTags = @('4.5.0', 'v4.5.0'),
    [string]$DestRoot = '',
    # 可选缓存参数（重复运行时直接传入上次成功值，避免再次请求上游）
    [string]$HostCommit = '',
    [string]$CommonTreeSha = ''
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root = (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($DestRoot)) { $DestRoot = Join-Path $Root 'QuickLook.Common' }

$Headers = @{
    'User-Agent' = 'QuickLook-CraftViewer-Fetch/0.1'
    'Accept'     = 'application/vnd.github+json'
}

function Invoke-GitHubApi {
    param([Parameter(Mandatory)] [string]$Url, [switch]$AllowNotFound)
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            return Invoke-RestMethod -Uri $Url -Headers $Headers -TimeoutSec 60
        }
        catch {
            $status = 0
            if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
            # 404/422 均表示 ref 不存在（GitHub 对未知 ref 返回 422）
            if ($status -eq 404 -or $status -eq 422) {
                if ($AllowNotFound) { return $null }
                throw "GitHub API 404/422：$Url"
            }
            if ($attempt -eq 3) { throw "GitHub API 调用失败（$Url）：$($_.Exception.Message)" }
            Start-Sleep -Seconds (2 * $attempt)
        }
    }
}

# ---- 步骤 0：目标目录（可恢复：保留既有文件，命中相同 SHA 的 blob 时跳过下载） ----
if (-not (Test-Path -LiteralPath $DestRoot)) {
    New-Item -ItemType Directory -Path $DestRoot -Force | Out-Null
}

# ---- 步骤 1：解析宿主版本对应的 QuickLook 主仓库 commit ----
$usedTag = '4.5.0(缓存)'
if ([string]::IsNullOrWhiteSpace($HostCommit)) {
    $usedTag = $null
    foreach ($tag in $HostTags) {
        $c = Invoke-GitHubApi -Url "https://api.github.com/repos/$HostRepo/commits/$tag" -AllowNotFound
        if ($null -ne $c) { $hostCommit = $c.sha; $usedTag = $tag; break }
    }
    if ($null -eq $hostCommit) {
        $rel = Invoke-GitHubApi -Url "https://api.github.com/repos/$HostRepo/releases?per_page=30"
        $r = $rel | Where-Object { $_.tag_name -match '^v?4\.5(\.|$)' } | Select-Object -First 1
        if ($null -ne $r) {
            $c = Invoke-GitHubApi -Url "https://api.github.com/repos/$HostRepo/commits/$($r.tag_name)" -AllowNotFound
            if ($null -ne $c) { $hostCommit = $c.sha; $usedTag = $r.tag_name }
        }
    }
    if ($null -eq $hostCommit) {
        Write-Error '无法解析 QuickLook 宿主 v4.5.0 对应 commit（tag v4.5.0 / 4.5.0 / releases 均未命中）。请人工确认宿主版本后重试。'
    }
}
Write-Host "[1/4] 宿主 tag '$usedTag' -> commit $hostCommit"

# ---- 步骤 2：识别 QuickLook.Common 在宿主 tag 的形态（子模块 / 内联普通目录） ----
$blobs = $null
$sourceNote = ''
if (-not [string]::IsNullOrWhiteSpace($CommonTreeSha)) {
    $subSha = $CommonTreeSha
    Write-Host "[2/4] 使用缓存的内联目录 tree：$subSha"
    $tree = Invoke-GitHubApi -Url "https://api.github.com/repos/$HostRepo/git/trees/$subSha`?recursive=1"
    if ($tree.truncated) { Write-Error 'git trees 响应被截断（truncated=true），需改用分页策略，脚本终止。' }
    $blobs = @($tree.tree | Where-Object { $_.type -eq 'blob' })
    $blobRepo = $HostRepo
    $sourceNote = "inline-tree@$subSha"
}
else {
    $contents = Invoke-GitHubApi -Url "https://api.github.com/repos/$HostRepo/contents/QuickLook.Common?ref=$hostCommit"
    if ($null -eq $contents) { Write-Error "主仓库 $HostRepo@$usedTag 不存在 QuickLook.Common 目录。" }
    $contents = @($contents)

    $subIsSubmodule = ($contents | Where-Object { $_.type -eq 'submodule' } | Select-Object -First 1)
    if ($null -ne $subIsSubmodule) {
        # 形态 A：子模块 -> 在 QuickLook.Common 独立仓库按子模块 SHA 取树
        $subSha = $subIsSubmodule.sha
        Write-Host "[2/4] QuickLook.Common 子模块 commit：$subSha"
        $tree = Invoke-GitHubApi -Url "https://api.github.com/repos/$CommonRepo/git/trees/$subSha`?recursive=1"
        if ($tree.truncated) { Write-Error 'git trees 响应被截断（truncated=true），需改用分页策略，脚本终止。' }
        $blobs = @($tree.tree | Where-Object { $_.type -eq 'blob' })
        $blobRepo = $CommonRepo
        $sourceNote = "submodule@$subSha"
    }
    else {
        # 形态 B：上游已将 QuickLook.Common 内联为主仓库普通目录 -> 从宿主根树取该目录 tree SHA
        $rootTree = Invoke-GitHubApi -Url "https://api.github.com/repos/$HostRepo/git/trees/$hostCommit"
        $dirEntry = $rootTree.tree | Where-Object { $_.path -eq 'QuickLook.Common' -and $_.type -eq 'tree' } | Select-Object -First 1
        if ($null -eq $dirEntry) { Write-Error '宿主根树中未找到 QuickLook.Common 目录项，需人工处理。' }
        $subSha = $dirEntry.sha
        Write-Host "[2/4] QuickLook.Common 为内联目录（tree $subSha），改从主仓库子树拉取"
        $tree = Invoke-GitHubApi -Url "https://api.github.com/repos/$HostRepo/git/trees/$subSha`?recursive=1"
        if ($tree.truncated) { Write-Error 'git trees 响应被截断（truncated=true），需改用分页策略，脚本终止。' }
        $blobs = @($tree.tree | Where-Object { $_.type -eq 'blob' })
        $blobRepo = $HostRepo
        $sourceNote = "inline-tree@$subSha"
    }
}
Write-Host "[3/4] 待下载 blob 数：$($blobs.Count)"

# ---- 步骤 4：逐个下载并落盘（已存在且非空的文件跳过，节约 API 配额） ----
$downloaded = 0
$skipped = 0
foreach ($item in $blobs) {
    $dest = Join-Path $DestRoot ($item.path -replace '/', '\')
    if ((Test-Path -LiteralPath $dest) -and ((Get-Item -LiteralPath $dest).Length -gt 0)) {
        $skipped++
        continue
    }
    $blob = Invoke-GitHubApi -Url "https://api.github.com/repos/$blobRepo/git/blobs/$($item.sha)"
    if ($blob.encoding -ne 'base64') {
        Write-Error "blob $($item.path) 编码非 base64（$($blob.encoding)），脚本终止。"
    }
    $bytes = [Convert]::FromBase64String(($blob.content -replace '\s', ''))
    $dest = Join-Path $DestRoot ($item.path -replace '/', '\')
    $parent = Split-Path -Parent $dest
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    [IO.File]::WriteAllBytes($dest, $bytes)
    $downloaded++
}
Write-Host "[4/4] 新下载 $downloaded 个，跳过已有 $skipped 个 -> $DestRoot"

# ---- 步骤 5：构建适配（仅改 csproj 构建配置，不改任何 .cs 源码，逐条记录） ----
$patchNotes = @()
$csprojPath = Join-Path $DestRoot 'QuickLook.Common.csproj'
$cs = [IO.File]::ReadAllText($csprojPath, [Text.Encoding]::UTF8)
# 5a. 去除 ..\GitVersion.cs 编译项：该文件由宿主仓库构建脚本生成，独立工程树中不存在
if ($cs -match 'GitVersion\.cs') {
    $cs = $cs -replace '(?s)\s*<ItemGroup>\s*<Compile Include="\.\.\\GitVersion\.cs">.*?</ItemGroup>', ''
    $patchNotes += 'removed <Compile Include="..\GitVersion.cs"> ItemGroup（宿主仓库构建期生成文件）'
}
# 5b. 去除 PreBuildEvent：依赖 $(SolutionDir) 与宿主 Scripts/update-version.ps1，独立构建不适用
if ($cs -match '<PreBuildEvent>') {
    $cs = $cs -replace '(?s)\s*<PropertyGroup>\s*<PreBuildEvent>.*?</PreBuildEvent>\s*</PropertyGroup>', ''
    $patchNotes += 'removed PreBuildEvent（依赖宿主 SolutionDir 脚本）'
}
# 5c. Sdk="Microsoft.NET.Sdk.WindowsDesktop" -> "Microsoft.NET.Sdk"：
#     WindowsDesktop SDK 自 .NET 5 起并入 Microsoft.NET.Sdk（UseWPF/UseWindowsForms 照常生效），
#     且本机 BuildTools 无 WindowsDesktop SDK 目录，由 global.json 按版本从 NuGet 解析
if ($cs -match 'Microsoft\.NET\.Sdk\.WindowsDesktop') {
    $cs = $cs.Replace('Sdk="Microsoft.NET.Sdk.WindowsDesktop"', 'Sdk="Microsoft.NET.Sdk"')
    $patchNotes += 'Sdk 属性 Microsoft.NET.Sdk.WindowsDesktop -> Microsoft.NET.Sdk'
}
[IO.File]::WriteAllText($csprojPath, $cs, [Text.UTF8Encoding]::new($true))
$patchNotes | ForEach-Object { Write-Host "构建适配：$_" -ForegroundColor Yellow }

# ---- 完成条件校验（T01 自检④） ----
$required = @(
    'QuickLook.Common.csproj',
    'Plugin\IViewer.cs',
    'Plugin\ContextObject.cs',
    'Plugin\Themes.cs'
)
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $DestRoot $_)) })
if ($missing.Count -gt 0) {
    Write-Error ("关键文件缺失：{0}——脚本失败，未编造任何源码。" -f ($missing -join ', '))
}

Set-Content -LiteralPath (Join-Path $DestRoot '_FETCHED_COMMIT.txt') `
    -Value "repo=$blobRepo`nsource=$sourceNote`ntreeSha=$subSha`nhostRepo=$HostRepo`nhostTag=$usedTag`nhostCommit=$hostCommit`nfetchedAt=$(Get-Date -Format o)`nbuildPatches=$($patchNotes -join '; ')" -Encoding UTF8

Write-Host ''
Write-Host "OK：QuickLook.Common @$subSha（对齐宿主 $usedTag）已就绪，T01 自检④ 通过。" -ForegroundColor Green
exit 0
