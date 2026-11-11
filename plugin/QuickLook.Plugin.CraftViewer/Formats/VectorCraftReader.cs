using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace QuickLook.Plugin.CraftViewer.Formats
{
    /// <summary>
    /// .vectorcraft 读取器（JSON 信封 + 可选 gzip + 可选 v4 blob 尾部）。
    /// 全部失败路径以 CraftFormatException(Category, 可读中文原因) 抛出，
    /// 由 CraftPreviewLoader 捕获转降级；本类不吞错误（探针需要断言 Category）。
    /// </summary>
    public static class VectorCraftReader
    {
        /// <summary>gzip 魔数 1F 8B。</summary>
        private static readonly byte[] GzipMagic = { 0x1F, 0x8B };

        /// <summary>BLOB_MAGIC "\n\0VCBLOBS\0"（craft-libs vectorcraft-format，v4 起尾部标识）。</summary>
        public static readonly byte[] BlobMagic = { 0x0A, 0x00, 0x56, 0x43, 0x42, 0x4C, 0x4F, 0x42, 0x53, 0x00 };

        /// <summary>format 校验值集合（§7.9）：含旧名 drawcraft。</summary>
        private static readonly HashSet<string> ValidFormats = new HashSet<string>(StringComparer.Ordinal)
        {
            "vectorcraft", "drawcraft"
        };

        public const int MinVersion = 1;
        public const int MaxVersion = 4;

        public static bool IsValidFormatName(string format) => format != null && ValidFormats.Contains(format);

        /// <summary>
        /// 信封读取：共享读打开 → 1F8B 则有界 gzip 解压（超 MaxGzipOutputBytes → DecompressionBomb）
        /// → 定位 BLOB_MAGIC 拆 JSON 头/尾部 → MinimalJson 取 format/version/generator/preview
        /// → 校验 format 与 1&lt;=version&lt;=4 → preview：base64 "data" 或 "blob":[offset,length]
        /// （offset 相对 marker 之后，越界/溢出 → BlobOutOfRange）→ PngSniffer 验签与尺寸。
        /// </summary>
        public static PreviewResult ReadPreview(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            using FileStream fs = OpenShared(path);
            long fileLen = fs.Length;

            bool isGzip = ReadGzipMagic(fs);
            fs.Seek(0, SeekOrigin.Begin);

            JsonEnvelope env;
            byte[] decompressed = null;   // gzip 路径：完整解压缓冲（有界）
            long tailStart = -1;          // 尾部起点（相对当前数据坐标系：gzip=解压流，否则=文件流）
            long dataLen;

            if (isGzip)
            {
                decompressed = DecompressBounded(fs, token);
                dataLen = decompressed.Length;
                env = MinimalJson.ScanEnvelope(decompressed, tolerant: false);
                RequireComplete(env, dataLen);

                if (env.PreviewBlobOffset.HasValue)
                {
                    int idx = IndexOfBytes(decompressed, BlobMagic, env.EndIndex);
                    if (idx < 0)
                        throw new CraftFormatException(CraftErrorCategory.BlobOutOfRange,
                            "[BlobOutOfRange] 声明了 blob 预览但文件缺少 BLOB_MAGIC 尾部标识");
                    tailStart = idx + BlobMagic.Length;
                }
            }
            else
            {
                // 未压缩：有界读头部（不把数百 MB 的 A6 文件整体载入内存）
                long headCap = (long)CraftLimits.MaxEnvelopeScanBytes + CraftLimits.UncompressedHeadSlackBytes;
                long headLen = Math.Min(fileLen, headCap);
                byte[] head = ReadExactly(fs, headLen);
                env = MinimalJson.ScanEnvelope(head, tolerant: false);
                RequireComplete(env, headLen);

                if (env.PreviewBlobOffset.HasValue)
                {
                    int idx = IndexOfBytes(head, BlobMagic, env.EndIndex);
                    if (idx >= 0)
                    {
                        tailStart = idx + BlobMagic.Length;
                    }
                    else if (fileLen > headLen)
                    {
                        // marker 可能位于大尾部内：流式带重叠继续搜索（不整体载入）
                        tailStart = SearchMarkerInStream(fs, headLen, head, token);
                    }
                }
                dataLen = fileLen;
            }

            // ---- format / version 校验（§7.9） ----
            if (!env.HasFormat || !ValidFormats.Contains(env.Format))
                throw new CraftFormatException(CraftErrorCategory.WrongFormat,
                    "[WrongFormat] 不是 VectorCraft 文件（format=" + (env.HasFormat ? env.Format : "缺失") + "）");
            if (!env.HasVersion)
                throw new CraftFormatException(CraftErrorCategory.WrongFormat,
                    "[WrongFormat] 信封缺少 version 字段");
            if (env.Version < MinVersion)
                throw new CraftFormatException(CraftErrorCategory.WrongFormat,
                    "[WrongFormat] 版本号非法（version=" + env.Version + "）");
            if (env.Version > MaxVersion)
                // A3：未来版本不误报损坏，也不强行渲染
                throw new CraftFormatException(CraftErrorCategory.FutureVersion,
                    "[FutureVersion] 版本过新（文件 v" + env.Version + "，插件支持 ≤" + MaxVersion + "）");

            var result = new PreviewResult
            {
                Kind = CraftKind.VectorCraft,
                FormatVersion = (int)env.Version,
                Generator = env.Generator,
                FileName = Path.GetFileName(path)
            };

            // ---- preview 提取 ----
            if (env.PreviewDataBase64 != null)
            {
                byte[] png = DecodeBase64(env.PreviewDataBase64);
                PngSniffer.ValidatePng(png, out int w, out int h);
                result.PngBytes = png;
                result.PixelWidth = w;
                result.PixelHeight = h;
                result.Source = PreviewSource.Embedded;
                if (isGzip) result.AddWarning("文件为 gzip 压缩信封，已解压读取");
                return result;
            }

            if (env.PreviewBlobOffset.HasValue)
            {
                long offset = env.PreviewBlobOffset.Value;
                long length = env.PreviewBlobLength ?? -1;
                long tailLen = tailStart >= 0 ? dataLen - tailStart : -1;

                // §7.5 blob 校验点：long 运算防溢出，任一不满足抛 BlobOutOfRange（A4）
                if (tailLen < 0 || offset < 0 || length < 0 ||
                    offset > tailLen || length > tailLen - offset)
                    throw new CraftFormatException(CraftErrorCategory.BlobOutOfRange,
                        string.Format(CultureInfo.InvariantCulture,
                            "[BlobOutOfRange] blob 预览越界（offset={0}, length={1}, 尾部={2}）",
                            offset, length, tailLen < 0 ? 0 : tailLen));

                byte[] png;
                if (isGzip)
                {
                    png = new byte[length];
                    Buffer.BlockCopy(decompressed, (int)(tailStart + offset), png, 0, (int)length);
                }
                else
                {
                    fs.Seek(tailStart + offset, SeekOrigin.Begin);
                    png = ReadExactly(fs, length);
                }

                PngSniffer.ValidatePng(png, out int w, out int h);
                result.PngBytes = png;
                result.PixelWidth = w;
                result.PixelHeight = h;
                result.Source = PreviewSource.Embedded;
                if (isGzip) result.AddWarning("文件为 gzip 压缩信封，已解压读取");
                return result;
            }

            // 无 preview 是正常情况（v1/v2/v4 均可缺省），返回 MetadataOnly 占位而非错误
            string note = isGzip ? "gzip 压缩信封" : null;
            return PreviewResult.NoPreview(CraftKind.VectorCraft, result.FormatVersion,
                env.Generator, result.FileName, note);
        }

        // ------------------------------------------------------------------
        // 内部工具
        // ------------------------------------------------------------------

        internal static FileStream OpenShared(string path)
        {
            // §7.3：FileShare.ReadWrite | FileShare.Delete（编辑器保存期间可读，A14）
            return new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }

        private static bool ReadGzipMagic(FileStream fs)
        {
            if (fs.Length < 2) return false;
            return fs.ReadByte() == GzipMagic[0] && fs.ReadByte() == GzipMagic[1];
        }

        /// <summary>有界解压：64KB 块累计计数，超 MaxGzipOutputBytes 即中止（A5）。</summary>
        private static byte[] DecompressBounded(FileStream fs, CancellationToken token)
        {
            try
            {
                using var gz = new GZipStream(fs, CompressionMode.Decompress, leaveOpen: true);
                using var ms = new MemoryStream();
                var buf = new byte[64 * 1024];
                long total = 0;
                int n;
                while ((n = gz.Read(buf, 0, buf.Length)) > 0)
                {
                    total += n;
                    if (total > CraftLimits.MaxGzipOutputBytes)
                        throw new CraftFormatException(CraftErrorCategory.DecompressionBomb,
                            "[DecompressionBomb] 解压输出超过 " + CraftLimits.MaxGzipOutputBytes / (1024 * 1024) + "MB 上限，中止读取");
                    ms.Write(buf, 0, n);
                    token.ThrowIfCancellationRequested();
                }
                return ms.ToArray();
            }
            catch (InvalidDataException ex)
            {
                throw new CraftFormatException(CraftErrorCategory.BadGzip,
                    "[BadGzip] gzip 解压失败（数据损坏或非 gzip 内容）", ex);
            }
        }

        /// <summary>信封必须完整闭合；头预算耗尽仍不闭合 = 超限（A6）。</summary>
        private static void RequireComplete(JsonEnvelope env, long scannedLen)
        {
            if (env.Complete) return;
            if (scannedLen > (long)CraftLimits.MaxEnvelopeScanBytes)
                throw new CraftFormatException(CraftErrorCategory.LimitExceeded,
                    "[LimitExceeded] 顶层 JSON 超过 " + CraftLimits.MaxEnvelopeScanBytes / (1024 * 1024) + "MB 扫描上限");
            throw new CraftFormatException(CraftErrorCategory.BadJson,
                "[BadJson] 顶层 JSON 不完整（缺少闭合括号）");
        }

        private static byte[] DecodeBase64(string s)
        {
            try
            {
                return Convert.FromBase64String(s);
            }
            catch (FormatException ex)
            {
                throw new CraftFormatException(CraftErrorCategory.BadJson,
                    "[BadJson] preview.data 不是合法 base64", ex);
            }
        }

        /// <summary>读精确字节数（流提前结束抛 IoError）。</summary>
        private static byte[] ReadExactly(FileStream fs, long count)
        {
            if (count < 0 || count > int.MaxValue)
                throw new CraftFormatException(CraftErrorCategory.LimitExceeded,
                    "[LimitExceeded] 请求读取的字节数非法：" + count);
            var data = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = fs.Read(data, read, (int)(count - read));
                if (n <= 0)
                    throw new CraftFormatException(CraftErrorCategory.IoError,
                        "[IoError] 文件在读取过程中被截断（期望 " + count + " 字节，实得 " + read + "）");
                read += n;
            }
            return data;
        }

        /// <summary>在流上分块带重叠搜索 BLOB_MAGIC，返回尾部起点（绝对流位置）；未找到返回 -1。</summary>
        private static long SearchMarkerInStream(FileStream fs, long fromPos, byte[] head, CancellationToken token)
        {
            var carry = new byte[BlobMagic.Length - 1];
            Buffer.BlockCopy(head, head.Length - carry.Length, carry, 0, carry.Length);
            long pos = fromPos;
            var buf = new byte[64 * 1024];

            while (true)
            {
                token.ThrowIfCancellationRequested();
                int n = fs.Read(buf, 0, buf.Length);
                if (n <= 0) return -1;

                var combined = new byte[carry.Length + n];
                Buffer.BlockCopy(carry, 0, combined, 0, carry.Length);
                Buffer.BlockCopy(buf, 0, combined, carry.Length, n);

                int idx = IndexOfBytes(combined, BlobMagic, 0);
                if (idx >= 0)
                {
                    long markerPos = pos - carry.Length + idx;
                    return markerPos + BlobMagic.Length;
                }

                Buffer.BlockCopy(combined, combined.Length - carry.Length, carry, 0, carry.Length);
                pos += n;
            }
        }

        /// <summary>朴素字节序列查找（marker 稀有，性能足够）。</summary>
        private static int IndexOfBytes(byte[] haystack, byte[] needle, int start)
        {
            if (needle.Length == 0) return Math.Max(start, 0);
            for (int i = Math.Max(start, 0); i <= haystack.Length - needle.Length; i++)
            {
                int j = 0;
                for (; j < needle.Length; j++)
                    if (haystack[i + j] != needle[j]) break;
                if (j == needle.Length) return i;
            }
            return -1;
        }
    }
}
