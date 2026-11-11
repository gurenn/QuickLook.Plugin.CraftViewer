using System;

namespace QuickLook.Plugin.CraftViewer.Formats
{
    /// <summary>
    /// PNG 签名 + IHDR 尺寸校验（纯 BCL，不读像素数据、不分配像素缓冲）。
    /// WPF 实际解码在 Rendering/PngDecoder.cs；本类是探针与 Reader 共用的预检。
    /// </summary>
    public static class PngSniffer
    {
        /// <summary>PNG 文件签名（8 字节）。</summary>
        public static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>8 签名 + 4 长度 + 4 "IHDR" + 13 数据 = 29；IHDR 至少需 8+4+4+4=20 字节可读宽高。</summary>
        private const int MinHeaderBytes = 8 + 4 + 4 + 4;

        /// <summary>仅判断签名（不校验尺寸）。</summary>
        public static bool HasPngSignature(byte[] data)
        {
            if (data == null || data.Length < Signature.Length) return false;
            for (int i = 0; i < Signature.Length; i++)
                if (data[i] != Signature[i]) return false;
            return true;
        }

        /// <summary>
        /// 验签并读 IHDR 宽高。非合法 PNG 抛 BadPng；宽高超限（A11）抛 LimitExceeded。
        /// </summary>
        public static void ValidatePng(byte[] data, out int width, out int height)
        {
            width = 0;
            height = 0;

            if (!HasPngSignature(data))
                throw new CraftFormatException(CraftErrorCategory.BadPng,
                    "[BadPng] 预览数据不是合法 PNG（签名不符）");
            if (data.Length < MinHeaderBytes)
                throw new CraftFormatException(CraftErrorCategory.BadPng,
                    "[BadPng] PNG 数据过短（缺少 IHDR 头）");

            // chunk: [length(4, BE)][type(4)="IHDR"][data...]
            long chunkLen = ReadUInt32BE(data, 8);
            if (chunkLen < 13 || data[12] != (byte)'I' || data[13] != (byte)'H' ||
                data[14] != (byte)'D' || data[15] != (byte)'R')
                throw new CraftFormatException(CraftErrorCategory.BadPng,
                    "[BadPng] PNG 首个数据块不是 IHDR");

            uint w = ReadUInt32BE(data, 16);
            uint h = ReadUInt32BE(data, 20);
            if (w == 0 || h == 0)
                throw new CraftFormatException(CraftErrorCategory.BadPng,
                    "[BadPng] PNG 尺寸非法（0 像素）");

            if (w > (uint)CraftLimits.MaxPngWidth || h > (uint)CraftLimits.MaxPngHeight)
                throw new CraftFormatException(CraftErrorCategory.LimitExceeded,
                    string.Format("[LimitExceeded] PNG 尺寸 {0}×{1} 超过单边上限 {2}×{3}",
                        w, h, CraftLimits.MaxPngWidth, CraftLimits.MaxPngHeight));

            long pixels = (long)w * (long)h; // long 计算，防 int 溢出
            if (pixels > CraftLimits.MaxPngPixels)
                throw new CraftFormatException(CraftErrorCategory.LimitExceeded,
                    string.Format("[LimitExceeded] PNG 像素数 {0:N0} 超过上限 {1:N0}", pixels, CraftLimits.MaxPngPixels));

            width = (int)w;
            height = (int)h;
        }

        private static uint ReadUInt32BE(byte[] b, int offset)
        {
            return ((uint)b[offset] << 24) | ((uint)b[offset + 1] << 16) |
                   ((uint)b[offset + 2] << 8) | b[offset + 3];
        }
    }
}
