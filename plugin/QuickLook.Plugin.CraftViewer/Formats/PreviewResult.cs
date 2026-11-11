using System;
using System.Collections.Generic;
using System.IO;

namespace QuickLook.Plugin.CraftViewer.Formats
{
    /// <summary>
    /// 统一预览结果模型（设计 §3）。Reader 产出；Loader 补充降级链；Panel 消费。
    /// </summary>
    public enum PreviewSource
    {
        Embedded,
        Rendered,
        MetadataOnly
    }

    public sealed class PreviewResult
    {
        private readonly List<string> _warnings = new List<string>();

        /// <summary>VectorCraft / PhotoCraft。</summary>
        public CraftKind Kind { get; set; }

        /// <summary>外层文件版本（vectorcraft 1-4 / pcraft 1；0 = 未知）。</summary>
        public int FormatVersion { get; set; }

        /// <summary>内嵌/渲染出的 PNG 字节；null 表示进入 MetadataOnly。</summary>
        public byte[] PngBytes { get; set; }

        public int PixelWidth { get; set; }
        public int PixelHeight { get; set; }

        /// <summary>P1-2 角标：内嵌 / 高保真渲染 / 仅元数据。</summary>
        public PreviewSource Source { get; set; }

        /// <summary>信封 generator 字段。</summary>
        public string Generator { get; set; }

        /// <summary>解析告警（如 CRC 修复、降级原因）。</summary>
        public IReadOnlyList<string> Warnings => _warnings;

        /// <summary>CLI 渲染临时文件（%TEMP%\CraftViewer_{guid}.png），Cleanup 删除。</summary>
        public string TempPng { get; set; }

        /// <summary>MetadataOnly 面板正文。</summary>
        public string MetadataSummary { get; set; }

        /// <summary>源文件名（供 BuildTitle 使用）。</summary>
        public string FileName { get; set; }

        public void AddWarning(string warning) { _warnings.Add(warning); }

        /// <summary>标题："文件名 · v4 · 256×180 · 内嵌预览"。</summary>
        public string BuildTitle()
        {
            var parts = new List<string>(4);
            if (!string.IsNullOrEmpty(FileName)) parts.Add(FileName);
            if (FormatVersion > 0) parts.Add("v" + FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (PngBytes != null && PixelWidth > 0 && PixelHeight > 0)
                parts.Add(PixelWidth.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\u00d7" +
                          PixelHeight.ToString(System.Globalization.CultureInfo.InvariantCulture));
            switch (Source)
            {
                case PreviewSource.Embedded: parts.Add("内嵌预览"); break;
                case PreviewSource.Rendered: parts.Add("高保真渲染"); break;
                case PreviewSource.MetadataOnly: parts.Add("仅元数据"); break;
            }
            return string.Join(" · ", parts);
        }

        /// <summary>构造"无内嵌预览"占位结果（正常情况，不是错误）。</summary>
        internal static PreviewResult NoPreview(CraftKind kind, int version, string generator,
            string fileName, string compressionNote)
        {
            var r = new PreviewResult
            {
                Kind = kind,
                FormatVersion = version,
                Source = PreviewSource.MetadataOnly,
                Generator = generator,
                FileName = fileName
            };
            var lines = new List<string>
            {
                (kind == CraftKind.PhotoCraft ? "PhotoCraft 项目包" : "VectorCraft 文档") +
                (version > 0 ? "（格式 v" + version + "）" : string.Empty),
                "无内嵌预览图" + (string.IsNullOrEmpty(compressionNote) ? string.Empty : " · " + compressionNote),
                string.IsNullOrEmpty(generator) ? null : "生成器：" + generator,
                "未配置高保真渲染 CLI（cli-config.json），仅显示元数据。"
            };
            lines.RemoveAll(string.IsNullOrEmpty);
            r.MetadataSummary = string.Join(Environment.NewLine, lines);
            return r;
        }
    }
}
