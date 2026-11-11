using System;
using System.IO;
using System.Threading;
using QuickLook.Plugin.CraftViewer.Formats;
using QuickLook.Plugin.CraftViewer.Rendering;

namespace QuickLook.Plugin.CraftViewer
{
    /// <summary>
    /// 降级链编排（设计 §3）：内嵌 → CLI → 元数据。输入 CraftKind 由调用方（Plugin）传入。
    /// 全程不抛 CraftFormatException/IO 异常：统一转为
    /// PreviewResult{ Source=MetadataOnly, MetadataSummary=可读原因 }；
    /// OperationCanceledException 原样向上传递（由 Plugin 静默处理）。
    /// </summary>
    public static class CraftPreviewLoader
    {
        public static PreviewResult Load(string path, CraftKind kind, CliConfig cfg, CancellationToken token)
        {
            PreviewResult partial = null;
            try
            {
                token.ThrowIfCancellationRequested();

                // ---- 第一级：内嵌预览（Reader 抛 CraftFormatException = 解析失败） ----
                partial = kind == CraftKind.PhotoCraft
                    ? PcraftReader.ReadPreview(path, token)
                    : VectorCraftReader.ReadPreview(path, token);

                // 择优策略：内嵌预览已达到渲染上限（如 .pcraft 的全分辨率 full.png）→ 直接用内嵌；
                // 内嵌偏低（如 .vectorcraft 的 256px）或没有内嵌 → CLI 实时渲染（上限 maxRenderSize）。
                // preferRendered=false = 恒内嵌优先（原 P0 行为）。CLI 未配置/失败自动回落内嵌。
                bool prefer = cfg == null || cfg.PreferRendered;
                int cap = cfg != null && cfg.MaxRenderSize >= 0 ? cfg.MaxRenderSize : CraftLimits.DefaultRenderMaxSide;
                bool embeddedUsable = partial.PngBytes != null;
                bool embeddedSharp = embeddedUsable && (cap <= 0 ||
                    Math.Max(partial.PixelWidth, partial.PixelHeight) >= cap);
                if (!prefer || embeddedSharp)
                {
                    if (embeddedUsable)
                        return partial; // 内嵌已最优或内嵌优先
                }

                bool cliTried = CliRenderer.TryRender(kind, path, cfg, out PreviewResult rendered,
                    out CliRenderer.Outcome outcome, token);
#if DEBUG_TRACE
                Plugin.Diag("load", System.IO.Path.GetFileName(path) +
                    " prefer=" + prefer + " cap=" + cap + " embUsable=" + embeddedUsable +
                    " embSharp=" + embeddedSharp + " cliTried=" + cliTried +
                    (cliTried ? "" : " outcome=" + outcome +
                        " cfgNull=" + (cfg == null) +
                        " vcPath=[" + (cfg != null ? cfg.VectorcraftCliPath : "<null-cfg>") + "]" +
                        " vcExists=" + System.IO.File.Exists(cfg != null ? cfg.VectorcraftCliPath : null)));
#endif
                if (cliTried)
                    return MergeRendered(partial, rendered);

                if (embeddedUsable)
                {
                    // 有内嵌预览可用：CLI 失败则回落内嵌（附一条说明），未配置则静默用内嵌
                    if (outcome == CliRenderer.Outcome.CliFailed)
                        partial.AddWarning("高保真渲染 CLI 执行失败或超时，已改用内嵌预览。");
                    return partial;
                }

                // ---- 无图可显 → 元数据降级 ----
                string note = outcome == CliRenderer.Outcome.CliFailed
                    ? "高保真渲染 CLI 执行失败或超时，仅显示元数据。"
                    : "未配置高保真渲染 CLI（在插件目录 cli-config.json 设置 vectorcraftCliPath / photocraftCliPath），仅显示元数据。";
                return BuildFallback(kind, path, partial, note);
            }
            catch (OperationCanceledException)
            {
                throw; // 取消不吞，上层静默
            }
            catch (CraftFormatException cfe)
            {
                // A3/A7 等解析类错误 → 元数据面板带可读原因（错误文案格式 [类别] 原因，§7.2）
                return BuildFallback(kind, path, partial, cfe.Message);
            }
            catch (Exception ex)
            {
                return BuildFallback(kind, path, partial, "[IoError] " + ex.Message);
            }
        }

        // ------------------------------------------------------------------

        /// <summary>把 CLI 渲染结果与 Reader 已解析的信封信息合并。</summary>
        private static PreviewResult MergeRendered(PreviewResult partial, PreviewResult rendered)
        {
            if (partial == null) return rendered;
            rendered.FormatVersion = rendered.FormatVersion > 0 ? rendered.FormatVersion : partial.FormatVersion;
            rendered.Generator = rendered.Generator ?? partial.Generator;
            rendered.FileName = rendered.FileName ?? partial.FileName;
            foreach (var w in partial.Warnings) rendered.AddWarning(w);
            return rendered;
        }

        /// <summary>构造 MetadataOnly 降级结果：保留已解析信息 + 降级说明。</summary>
        private static PreviewResult BuildFallback(CraftKind kind, string path,
            PreviewResult partial, string note)
        {
            var r = new PreviewResult
            {
                Kind = partial?.Kind ?? kind,
                FormatVersion = partial?.FormatVersion ?? 0,
                Source = PreviewSource.MetadataOnly,
                Generator = partial?.Generator,
                FileName = partial?.FileName ?? Path.GetFileName(path)
            };
            if (partial != null)
                foreach (var w in partial.Warnings)
                    r.AddWarning(w);
            r.AddWarning(note);

            string baseSummary = partial?.MetadataSummary;
            if (string.IsNullOrEmpty(baseSummary))
            {
                string kindName = r.Kind == CraftKind.PhotoCraft ? "PhotoCraft 项目包" : "VectorCraft 文档";
                baseSummary = kindName + (r.FormatVersion > 0 ? "（格式 v" + r.FormatVersion + "）" : string.Empty);
                if (!string.IsNullOrEmpty(r.Generator))
                    baseSummary += Environment.NewLine + "生成器：" + r.Generator;
            }
            // 降级说明只放 Warnings 一处（QA 建议#1：不再重复拼进摘要）
            r.MetadataSummary = baseSummary;
            return r;
        }
    }
}
