using System;

namespace QuickLook.Plugin.CraftViewer.Formats
{
    /// <summary>
    /// 解析/解码失败类别（设计 §3）。决定错误面板文案。
    /// </summary>
    public enum CraftErrorCategory
    {
        BadGzip,
        BadJson,
        JsonTooLarge,
        WrongFormat,
        FutureVersion,
        BlobOutOfRange,
        DecompressionBomb,
        LimitExceeded,
        MissingManifest,
        FutureManifestVersion,
        UnsupportedZipFeature,
        PathTraversal,
        BadPng,
        CliUnavailable,
        CliFailed,
        IoError
    }

    /// <summary>
    /// Formats/Rendering 层统一抛出的解析异常：Category 决定错误面板文案。
    /// </summary>
    public sealed class CraftFormatException : Exception
    {
        /// <summary>错误类别，供面板与探针断言。</summary>
        public CraftErrorCategory Category { get; }

        public CraftFormatException(CraftErrorCategory category, string readableReason, Exception inner = null)
            : base(readableReason, inner)
        {
            Category = category;
        }
    }
}
