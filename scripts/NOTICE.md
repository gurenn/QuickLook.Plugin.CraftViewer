# NOTICE —— QuickLook.Plugin.CraftViewer 依赖与许可证说明

版本：0.1.0　　更新：随 T05 打包阶段维护

## 运行时依赖（随包分发）

| 依赖 | 版本策略 | 许可证 | 说明 |
|---|---|---|---|
| QuickLook.Common.dll | 与实机宿主 QuickLook v4.5.0 对应的子模块 commit 同源构建 | GPL-3.0（引用自 QL-Win/QuickLook.Common） | 宿主同进程已加载同名程序集时优先复用宿主实例；包内副本仅兜底。源码经 `scripts/fetch-quicklook-common.ps1` 从上游拉取（锚定宿主 tag，非 master HEAD） |

## 构建期依赖（不进包）

| 依赖 | 版本 | 用途 |
|---|---|---|
| Microsoft.NETFramework.ReferenceAssemblies | 1.0.3（PrivateAssets=all） | 本机无 net462 目标包时提供参考程序集 |
| System.IO.Compression / System.IO.Compression.FileSystem | net462 BCL 程序集 | pcraft ZIP 读取；框架自带，CopyLocal=false 语义，不随包分发 |
| .NET SDK（仅构建期宿主） | 8.0.404，安装于 `D:\Developer\WorkBuddy_Workspace\dotnet-sdk`（workspace 本地，无需管理员） | 实测 VS2022 BuildTools 的 MSBuild 目录树中不存在任何 MSBuild SDK（`Sdks/` 缺失）且无 WinFX/WPF 旧式 targets，无法构建 SDK 风格 + UseWPF 工程；构建脚本 `build.ps1` 因此使用该本地 SDK 内嵌的同一 MSBuild 引擎（`dotnet.exe msbuild /t:Restore /t:Build`）。该 SDK 仅作构建工具链，不进入任何产物，运行时仍是 .NET Framework 4.8 |

## 明确不引入

- **Newtonsoft.Json**：宿主使用 13.0.4，为避免版本冲突，插件内置自研最小 JSON 信封解析器（`Formats/MinimalJson.cs`），不依赖任何第三方 JSON 库。

## 外部 CLI（可选，用户自备，不随包分发）

- `vectorcraft-cli convert <in> <out.png>`
- `photocraft-cli convert <in> <out.png>`
- 由 `Rendering/CliRenderer.cs` 以独立进程调用（UseShellExecute=false、超时 30s 可配置、Cleanup 终止进程并删除临时 PNG）。未配置时插件降级为"仅元数据"面板。

## 格式规范来源（只读实现依据，不含代码复制）

- VectorCraft / PhotoCraft / craft-libs 上游仓库（MIT OR Apache-2.0）的格式 crate 源码级规范：信封常量（`VERSION=4`、`BLOB_MAGIC="\n\0VCBLOBS\0"`）、pcraft manifest 与预览白名单（`composite/full.png` > `composite/preview.png` > `thumb.png`）。
- QuickLook 宿主与 HelloWorld 模板：IViewer/ContextObject 契约与 .qlplugin 打包方式（GPL-3.0 / MIT）。插件代码为原创实现，仅引用接口。

## QuickLook.Common 源码版本记录

`QuickLook.Common/_FETCHED_COMMIT.txt` 记录每次 fetch 的仓库、commit SHA、宿主 tag 与时间，交付时随文档归档。

fetch 脚本对 vendored `QuickLook.Common.csproj` 做了三条**构建配置适配**（不改任何 .cs 源码）：
1. 移除 `<Compile Include="..\GitVersion.cs">`（宿主仓库构建脚本生成文件，独立工程树中不存在）；
2. 移除 `PreBuildEvent`（依赖 `$(SolutionDir)` 与宿主 `Scripts/update-version.ps1`）；
3. `Sdk="Microsoft.NET.Sdk.WindowsDesktop"` → `Sdk="Microsoft.NET.Sdk"`（WindowsDesktop SDK 自 .NET 5 起并入后者，UseWPF/UseWindowsForms 语义不变）。
