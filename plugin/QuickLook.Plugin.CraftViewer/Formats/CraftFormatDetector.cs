using System;
using System.IO;
using System.IO.Compression;

namespace QuickLook.Plugin.CraftViewer.Formats
{
    /// <summary>
    /// 轻量 Sniff（设计 §3）：CanHandle 专用，只读头部预算内的字节，绝不抛出。
    /// vectorcraft：扩展名（.vectorcraft/.drawcraft）+ 首字节 1F8B 或 '{' +
    ///   对前 SniffHeaderBytes 做顶层 format/version 校验（version&gt;4 也命中本插件，
    ///   由 View 阶段给"版本过新"面板——P0-1/A3 要求，不能让 InfoPanel 兜底）。
    /// pcraft：扩展名 .pcraft + ZIP 存在 manifest.json entry（只查目录不解压）。
    ///   ZIP 打不开（ZIP64/加密/损坏）但带 PK 签名时仍命中，由 View 阶段报
    ///   "不支持的 ZIP 特性"（A10）；目录包检查 manifest.json 文件存在性。
    /// </summary>
    public static class CraftFormatDetector
    {
        private static readonly string[] VectorExtensions = { ".vectorcraft", ".drawcraft" };

        public static CraftKind Sniff(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return CraftKind.Unknown;

                // 目录包：仅支持 .pcraft 目录形态
                if (Directory.Exists(path))
                {
                    string dirExt = Path.GetExtension(path);
                    if (!dirExt.Equals(".pcraft", StringComparison.OrdinalIgnoreCase)) return CraftKind.Unknown;
                    return File.Exists(Path.Combine(path, "manifest.json"))
                        ? CraftKind.PhotoCraft
                        : CraftKind.Unknown;
                }

                if (!File.Exists(path)) return CraftKind.Unknown;
                string ext = Path.GetExtension(path);

                if (ext.Equals(".pcraft", StringComparison.OrdinalIgnoreCase))
                    return SniffPhotoCraft(path);

                foreach (var ve in VectorExtensions)
                    if (ext.Equals(ve, StringComparison.OrdinalIgnoreCase))
                        return SniffVectorCraft(path);

                return CraftKind.Unknown;
            }
            catch (Exception)
            {
                // 任何 IO/解析异常 → Unknown，绝不抛出（设计 §3）
                return CraftKind.Unknown;
            }
        }

        // ------------------------------------------------------------------

        private static CraftKind SniffVectorCraft(string path)
        {
            byte[] head;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                int len = (int)Math.Min(fs.Length, CraftLimits.SniffHeaderBytes);
                head = new byte[len];
                int read = 0;
                while (read < len)
                {
                    int n = fs.Read(head, read, len - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read < len) Array.Resize(ref head, read);
            }

            // gzip：只解压前 64KB 输出（快路径，不整体解压）
            if (head.Length >= 2 && head[0] == 0x1F && head[1] == 0x8B)
            {
                try
                {
                    using var ms = new MemoryStream(head);
                    using var gz = new GZipStream(ms, CompressionMode.Decompress);
                    var outBuf = new byte[CraftLimits.SniffHeaderBytes];
                    int total = 0, n;
                    while (total < outBuf.Length && (n = gz.Read(outBuf, total, outBuf.Length - total)) > 0)
                        total += n;
                    head = new byte[total];
                    Array.Copy(outBuf, head, total);
                }
                catch (InvalidDataException)
                {
                    return CraftKind.Unknown; // A1 坏 gzip：扩展名对但内容坏，不命中（错误由探索样本的损坏分支兜底）
                }
            }
            else if (head.Length == 0 || head[0] != (byte)'{')
            {
                return CraftKind.Unknown;
            }

            var env = MinimalJson.ScanEnvelope(head, tolerant: true);
            if (env.HasFormat && !VectorCraftReader.IsValidFormatName(env.Format))
                return CraftKind.Unknown; // A2 错 format：不命中
            if (env.HasVersion && env.Version < VectorCraftReader.MinVersion)
                return CraftKind.Unknown; // 版本号非法：不命中
            // 命中（含未来版本 v>4——View 阶段出"版本过新"面板，A3）
            return CraftKind.VectorCraft;
        }

        private static CraftKind SniffPhotoCraft(string path)
        {
            // ZIP 签名：本地文件头 PK\x03\x04 或空归档 PK\x05\x06
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                if (fs.Length < 4) return CraftKind.Unknown;
                int b0 = fs.ReadByte(), b1 = fs.ReadByte();
                if (b0 != 'P' || b1 != 'K') return CraftKind.Unknown;
            }

            // 查 manifest.json entry（只读中央目录，不解压内容）
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
                foreach (var entry in zip.Entries)
                    if (entry.FullName == "manifest.json")
                        return CraftKind.PhotoCraft;
                return CraftKind.Unknown; // A8 无 manifest：不命中
            }
            catch (Exception)
            {
                // ZIP64/加密/损坏：带 PK 签名仍命中，由 View 阶段给出明确面板（A10）
                return CraftKind.PhotoCraft;
            }
        }
    }
}
