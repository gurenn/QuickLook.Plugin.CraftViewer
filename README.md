# QuickLook.Plugin.CraftViewer

[QuickLook](https://github.com/QL-Win/QuickLook) 空格预览插件：预览 **VectorCraft**（github.com/storytold/vectorcraft）的 `.vectorcraft` 矢量图项目文件与 **PhotoCraft**（github.com/storytold/photocraft）的 `.pcraft` 图像项目包。

A QuickLook plugin for previewing **VectorCraft** `.vectorcraft` (vector drawing) and **PhotoCraft** `.pcraft` (image project) files.

## 背景 / Background

[ArtCraft](https://github.com/storytold) 是 storytelling 创作软件系列（VectorCraft 矢量绘图、PhotoCraft 图像编辑等），均采用各自的项目文件格式且上游未公开独立的格式文档。本插件面向 QuickLook 用户，填补这两个格式在 Windows 文件管理器中"空格即览"的空缺。

**当前仅支持 VectorCraft（`.vectorcraft`）与 PhotoCraft（`.pcraft`）** 两个格式的预览。如需支持 ArtCraft 系列其他软件的格式（或其他小众格式），欢迎在 [Issues](https://github.com/gurenn/QuickLook.Plugin.CraftViewer/issues) 中提出需求。

## 功能特性 / Features

- 🖼️ **内嵌预览快路径**（纯 C#，零外部依赖）：读取 `.vectorcraft` 内嵌 PNG 预览（v1–v4 信封、drawcraft 兼容、gzip 与 v4 二进制 blob 尾部）、`.pcraft` 白名单预览（`composite/full.png` → `composite/preview.png` → `thumb.png`）
- 🔍 **CLI 高保真渲染兜底**：内嵌图缺失或分辨率偏低时，调用 [vectorcraft-cli](https://github.com/storytold/vectorcraft) / [photocraft-cli](https://github.com/storytold/photocraft) 实时渲染（默认上限 512px，可配置）
- 🛡️ **对抗性安全设计**：有界解压（256MB）、JSON 扫描预算、blob 越界校验、ZIP CRC/加密/ZIP64 检查、路径白名单精确匹配、渲染子进程超时 Kill、取消语义贯穿——坏文件绝不拖垮宿主
- 🧪 **35 项格式探针回归**：`probe/` 含合成样本生成器（26 正常样本 + 11 类对抗样本）

## 安装 / Install

1. 从 [Releases](https://github.com/gurenn/QuickLook.Plugin.CraftViewer/releases) 下载 `QuickLook.Plugin.CraftViewer.qlplugin`
2. 保持 QuickLook 正在运行，选中该文件按空格，点击 **Install**，重启 QuickLook

Download the `.qlplugin`, select it in Explorer, press <kbd>Space</kbd> with QuickLook running, click **Install**, then restart QuickLook.

## 可选：CLI 高保真渲染 / Optional: high-fidelity rendering

构建上游 CLI 并把路径写入插件目录的 `cli-config.json`：

```json
{
  "vectorcraftCliPath": "D:\\path\\to\\vectorcraft-cli.exe",
  "photocraftCliPath": "D:\\path\\to\\photocraft-cli.exe",
  "maxRenderSize": 512,
  "preferRendered": true,
  "timeoutSeconds": 30
}
```

| 配置项 | 说明 |
|---|---|
| `vectorcraftCliPath` / `photocraftCliPath` | CLI 全路径；留空 = 优雅降级为内嵌预览/元数据 |
| `maxRenderSize` | CLI 渲染输出单边上限（像素），`0` = 不限制 |
| `preferRendered` | `true`：内嵌预览不足上限时优先 CLI 实时渲染；`false`：恒内嵌优先 |
| `timeoutSeconds` | 单次渲染超时（默认 30） |

## 构建 / Build

需要 .NET SDK 8+（内含 MSBuild 引擎，目标框架为 net462，运行于 .NET Framework 4.8）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Configuration Release
powershell -ExecutionPolicy Bypass -File scripts\pack.ps1   # 产出 dist\QuickLook.Plugin.CraftViewer.qlplugin
```

`QuickLook.Common/` 为 vendored 源码（QuickLook v4.5.0 tag，commit `b13df028`），可用 `scripts/fetch-quicklook-common.ps1` 重新锚定。

## 结构 / Layout

```
QuickLook.Common/    vendored 宿主公共库（上游 tag 4.5.0）
plugin/              插件本体（Formats 纯 BCL 解析层 + Rendering WPF 层 + IViewer 生命周期）
probe/               格式探针与合成样本生成器（35 项回归）
scripts/             build / pack / deploy / fetch-quicklook-common
```

## 许可 / License

[GPL-3.0](LICENSE)——本插件随附 `QuickLook.Common`（QuickLook 项目，GPL-3.0）。
GPL-3.0, consistent with QuickLook and its vendored QuickLook.Common.
