namespace QuickLook.Plugin.CraftViewer.Formats
{
    /// <summary>
    /// 资源上限常量表（设计 §7.4）。全部集中在本文件，QA 按此出对抗样本。
    /// </summary>
    public static class CraftLimits
    {
        /// <summary>A5 解压炸弹上限：gzip 累计输出超过即中止。</summary>
        public const int MaxGzipOutputBytes = 256 * 1024 * 1024;

        /// <summary>A6 超大 JSON：顶层信封扫描预算。</summary>
        public const int MaxEnvelopeScanBytes = 32 * 1024 * 1024;

        /// <summary>A6 超深嵌套：JSON 深度上限。</summary>
        public const int MaxJsonDepth = 64;

        /// <summary>A6（pcraft 侧）：manifest.json 字节上限。</summary>
        public const int MaxManifestBytes = 4 * 1024 * 1024;

        /// <summary>A11 超大 PNG：单边上限。</summary>
        public const int MaxPngWidth = 16384;
        public const int MaxPngHeight = 16384;

        /// <summary>A11：总像素上限（30000² = 9×10⁸ 被拒）。</summary>
        public const long MaxPngPixels = 32_000_000;

        /// <summary>CanHandle 读入预算。</summary>
        public const int SniffHeaderBytes = 64 * 1024;

        /// <summary>P1-1：外部 CLI 渲染默认超时（config 可改）。</summary>
        public const int CliTimeoutSeconds = 30;

        /// <summary>CLI 渲染输出单边默认上限（config maxRenderSize 可改，0 = 不限制）。</summary>
        public const int DefaultRenderMaxSide = 512;

        /// <summary>未压缩 vectorcraft 头部缓冲 = 扫描预算 + 少量余量（容纳 marker 等）。</summary>
        public const int UncompressedHeadSlackBytes = 1024 * 1024;

        /// <summary>pcraft 预览 PNG 条目/文件大小上限（合法预览远小于此）。</summary>
        public const long MaxPreviewEntryBytes = 64L * 1024 * 1024;
    }
}
