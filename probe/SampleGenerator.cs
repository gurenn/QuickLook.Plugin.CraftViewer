using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ProbeCraftFormats
{
    /// <summary>
    /// 合成样本生成器（M0，设计 T03）。
    /// 覆盖报告 §7.2 矩阵 + PRD 对抗样本 A1–A11 构造，落盘 samples\ 供 QA 复用（golden corpus 雏形）。
    /// 全部样本由本类程序化构造，不依赖外部语料。
    /// </summary>
    internal static class SampleGenerator
    {
        /// <summary>单个样本描述：文件名 → 绝对路径。</summary>
        public sealed class SampleSet
        {
            public string RootDir { get; set; }
            public Dictionary<string, string> Files { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

            public string this[string name] => Files[name];
        }

        /// <summary>BLOB_MAGIC "\n\0VCBLOBS\0"（与 VectorCraftReader.BlobMagic 一致）。</summary>
        private static readonly byte[] BlobMagic = { 0x0A, 0x00, 0x56, 0x43, 0x42, 0x4C, 0x4F, 0x42, 0x53, 0x00 };

        // ==================================================================
        // 入口：产出全部样本
        // ==================================================================

        public static SampleSet GenerateAll(string rootDir)
        {
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
            Directory.CreateDirectory(rootDir);

            var set = new SampleSet { RootDir = rootDir };
            byte[] png96 = MiniPng.CreatePng(96, 64, 30, 144, 255);   // 主预览图
            byte[] pngFull = MiniPng.CreatePng(64, 64, 255, 128, 0);  // pcraft full
            byte[] pngPrev = MiniPng.CreatePng(32, 32, 0, 200, 100);  // pcraft preview
            byte[] pngThumb = MiniPng.CreatePng(16, 16, 128, 128, 128); // pcraft thumb
            byte[] pngTiny = MiniPng.CreatePng(1, 1, 255, 0, 0);      // A11 正例
            string b64 = Convert.ToBase64String(png96);

            // ---------------- vectorcraft 正样本（报告 §7.2 标准/新版本/无预览/兼容） ----------------
            WriteFile(set, "vc_v1_inline.vectorcraft",
                Envelope("vectorcraft", 1, InlinePreview(b64), withDocument: true));
            WriteFile(set, "vc_v2_drawcraft.drawcraft",
                Envelope("drawcraft", 2, InlinePreview(b64), withDocument: true));
            WriteFile(set, "vc_v3_gzip.vectorcraft", Gzip(
                Encoding.UTF8.GetBytes(Envelope("vectorcraft", 3, InlinePreview(b64), withDocument: true))));
            WriteBlobTailSample(set, "vc_v4_blob.vectorcraft",
                Envelope("vectorcraft", 4, BlobPreview(0, png96.Length), withDocument: true), png96);
            WriteFile(set, "vc_no_preview.vectorcraft",
                Envelope("vectorcraft", 4, null, withDocument: true));

            // ---------------- pcraft 正样本 ----------------
            WriteZip(set, "pcraft_store.pcraft", CompressionLevel.NoCompression, ManifestAll(),
                new Dictionary<string, byte[]>
                {
                    { "manifest.json", Utf8(ManifestAll()) },
                    { "composite/full.png", pngFull },
                    { "composite/preview.png", pngPrev },
                    { "thumb.png", pngThumb },
                });
            WriteZip(set, "pcraft_deflate.pcraft", CompressionLevel.Optimal, ManifestAll(),
                new Dictionary<string, byte[]>
                {
                    { "manifest.json", Utf8(ManifestAll()) },
                    { "composite/full.png", pngFull },
                    { "composite/preview.png", pngPrev },
                    { "thumb.png", pngThumb },
                });
            // manifest 非首 entry + full > preview > thumb 逐级缺失
            WriteZip(set, "pcraft_manifest_not_first.pcraft", CompressionLevel.Optimal, ManifestAll(),
                new Dictionary<string, byte[]>
                {
                    { "composite/full.png", pngFull },
                    { "manifest.json", Utf8(ManifestAll()) },
                });
            WriteZip(set, "pcraft_prio_preview.pcraft", CompressionLevel.Optimal, ManifestAll(),
                new Dictionary<string, byte[]>
                {
                    { "manifest.json", Utf8(ManifestAll()) },
                    { "composite/preview.png", pngPrev },
                    { "thumb.png", pngThumb },
                });
            WriteZip(set, "pcraft_prio_thumb.pcraft", CompressionLevel.Optimal, ManifestAll(),
                new Dictionary<string, byte[]>
                {
                    { "manifest.json", Utf8(ManifestAll()) },
                    { "thumb.png", pngThumb },
                });

            // 目录包
            string dirPkg = Path.Combine(rootDir, "pcraft_dir.pcraft");
            Directory.CreateDirectory(Path.Combine(dirPkg, "composite"));
            File.WriteAllBytes(Path.Combine(dirPkg, "manifest.json"), Utf8(ManifestAll()));
            File.WriteAllBytes(Path.Combine(dirPkg, "composite", "full.png"), pngFull);
            set.Files["pcraft_dir.pcraft"] = dirPkg;

            // ---------------- A1 坏 gzip ----------------
            WriteFile(set, "a1_bad_gzip.vectorcraft", GzipCorrupt());

            // ---------------- A2 错 format ----------------
            WriteFile(set, "a2_wrong_format.vectorcraft",
                Envelope("othercraft", 1, InlinePreview(b64), withDocument: false));

            // ---------------- A3 未来 version ----------------
            WriteFile(set, "a3_version99.vectorcraft",
                Envelope("vectorcraft", 99, InlinePreview(b64), withDocument: false));

            // ---------------- A4 越界 blob（越界 + 溢出两例） ----------------
            WriteFile(set, "a4_blob_oor.vectorcraft",
                Envelope("vectorcraft", 4, BlobPreview(999999, 999999), withDocument: false));
            WriteFile(set, "a4_blob_overflow.vectorcraft",
                Envelope("vectorcraft", 4, BlobPreview(long.MaxValue, long.MaxValue), withDocument: false));

            // ---------------- A5 解压炸弹（解压输出 > 256MB 上限，压缩后体积可控） ----------------
            WriteFile(set, "a5_bomb.vectorcraft", GzipZeros(270L * 1024 * 1024));

            // ---------------- A6 超大 JSON / 超深嵌套 ----------------
            WriteFile(set, "a6_huge_json.vectorcraft", HugeJson());
            WriteFile(set, "a6_deep_json.vectorcraft", DeepNestedEnvelope(70));

            // ---------------- A7 坏 PNG（vectorcraft preview 非合法 PNG） ----------------
            byte[] garbage = new byte[512];
            new Random(20261111).NextBytes(garbage);
            WriteFile(set, "a7_bad_png.vectorcraft",
                Envelope("vectorcraft", 1, InlinePreview(Convert.ToBase64String(garbage)), withDocument: false));

            // ---------------- A8 无 manifest（ZIP 有效但缺 manifest） ----------------
            WriteZip(set, "a8_no_manifest.pcraft", CompressionLevel.Optimal, null,
                new Dictionary<string, byte[]> { { "thumb.png", pngThumb } });

            // ---------------- A9 路径穿越（manifest 指向 ../../evil.png） ----------------
            WriteZip(set, "a9_path_traversal.pcraft", CompressionLevel.Optimal,
                "{\"format_version\":1,\"generator\":\"ProbeGen\",\"thumbnail\":\"../../evil.png\"}",
                new Dictionary<string, byte[]>
                {
                    { "manifest.json", Utf8("{\"format_version\":1,\"generator\":\"ProbeGen\",\"thumbnail\":\"../../evil.png\"}") },
                    { "thumb.png", pngThumb },
                });

            // ---------------- A10 加密标志 ZIP ----------------
            WriteEncryptedFlagZip(set, "a10_encrypted.pcraft", ManifestAll(),
                new Dictionary<string, byte[]>
                {
                    { "manifest.json", Utf8(ManifestAll()) },
                    { "thumb.png", pngThumb },
                });

            // ---------------- A11 极端尺寸 PNG ----------------
            WriteFile(set, "a11_tiny_1x1.vectorcraft",
                Envelope("vectorcraft", 1, InlinePreview(Convert.ToBase64String(pngTiny)), withDocument: false));
            WriteFile(set, "a11_huge_declared.vectorcraft",
                Envelope("vectorcraft", 1, InlinePreview(Convert.ToBase64String(MiniPng.CreateHeaderOnlyPng(30000, 30000))), withDocument: false));

            // ---------------- pcraft 坏 CRC ----------------
            WriteBadCrcZip(set, "a7_bad_crc.pcraft", ManifestAll(),
                new Dictionary<string, byte[]>
                {
                    { "manifest.json", Utf8(ManifestAll()) },
                    { "thumb.png", pngThumb },
                });

            return set;
        }

        // ==================================================================
        // vectorcraft 信封构造
        // ==================================================================

        private static string Envelope(string format, int version, string previewValue, bool withDocument)
        {
            var sb = new StringBuilder();
            sb.Append("{\"format\":\"").Append(format).Append("\",\"version\":").Append(version)
              .Append(",\"generator\":\"ProbeGen 0.1\"");
            if (previewValue != null)
            {
                sb.Append(",\"preview\":{\"mime\":\"image/png\",");
                if (previewValue.StartsWith("[", StringComparison.Ordinal))
                    sb.Append("\"blob\":").Append(previewValue).Append('}');   // blob 数组 [offset,length]
                else
                    sb.Append("\"data\":\"").Append(previewValue).Append("\"}"); // inline base64
            }
            if (withDocument)
                sb.Append(",\"profiles\":{\"icc\":{\"mime\":\"application/vnd.iccprofile\",\"blob\":[0,128]}}")
                  .Append(",\"pdf\":{\"mime\":\"application/pdf\",\"blob\":[128,256]}")
                  .Append(",\"document\":{\"version\":3,\"artboards\":[],\"layers\":[]}")
                  .Append(",\"images\":{\"k1\":{\"mime\":\"image/png\",\"data\":\"iVBORw0KGgo=\"}}");
            sb.Append('}');
            return sb.ToString();
        }

        private static string InlinePreview(string base64) => base64;

        private static string BlobPreview(long offset, long length) =>
            "[" + offset.ToString(CultureInfo.InvariantCulture) + "," +
            length.ToString(CultureInfo.InvariantCulture) + "]";

        /// <summary>A6 超深嵌套：document 下 depth 层数组（&gt; MaxJsonDepth=64）。</summary>
        private static string DeepNestedEnvelope(int depth)
        {
            var sb = new StringBuilder();
            sb.Append("{\"format\":\"vectorcraft\",\"version\":4,\"generator\":\"ProbeGen 0.1\",\"document\":");
            for (int i = 0; i < depth; i++) sb.Append('[');
            sb.Append('0');
            for (int i = 0; i < depth; i++) sb.Append(']');
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>JSON + BLOB_MAGIC + PNG 尾部（v4 blob 路径）。</summary>
        private static void WriteBlobTailSample(SampleSet set, string name, string json, byte[] png)
        {
            using var ms = new MemoryStream();
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            ms.Write(jsonBytes, 0, jsonBytes.Length);
            ms.Write(BlobMagic, 0, BlobMagic.Length);
            ms.Write(png, 0, png.Length);
            WriteFile(set, name, ms.ToArray());
        }

        private static string HugeJson()
        {
            // document 内含 ~34MB 巨型字符串 → 超过 32MB 扫描预算（A6）
            var sb = new StringBuilder();
            sb.Append("{\"format\":\"vectorcraft\",\"version\":4,\"generator\":\"ProbeGen\",");
            sb.Append("\"document\":{\"blob\":\"");
            sb.Append('A', 34 * 1024 * 1024);
            sb.Append("\"}}");
            return sb.ToString();
        }

        private static byte[] Gzip(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                gz.Write(data, 0, data.Length);
            return ms.ToArray();
        }

        /// <summary>A1：合法 gzip 头后接随机垃圾。</summary>
        private static byte[] GzipCorrupt()
        {
            var ms = new MemoryStream();
            ms.WriteByte(0x1F); ms.WriteByte(0x8B);
            var rnd = new Random(1);
            var garbage = new byte[512];
            rnd.NextBytes(garbage);
            ms.Write(garbage, 0, garbage.Length);
            return ms.ToArray();
        }

        /// <summary>A5：压缩 size 字节的 0x00（解压输出超限而压缩体很小）。</summary>
        private static byte[] GzipZeros(long size)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                var zeros = new byte[1024 * 1024];
                long remaining = size;
                while (remaining > 0)
                {
                    int n = (int)Math.Min(zeros.Length, remaining);
                    gz.Write(zeros, 0, n);
                    remaining -= n;
                }
            }
            return ms.ToArray();
        }

        // ==================================================================
        // pcraft 构造
        // ==================================================================

        private static string ManifestAll() =>
            "{\"format_version\":1,\"generator\":\"ProbeGen 0.1\",\"thumbnail\":\"thumb.png\"," +
            "\"composite\":\"composite/preview.png\",\"full_composite\":\"composite/full.png\"}";

        private static void WriteZip(SampleSet set, string name, CompressionLevel level,
            string manifestJson, Dictionary<string, byte[]> entries)
        {
            string path = Path.Combine(set.RootDir, name);
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var kv in entries)
                {
                    var entry = zip.CreateEntry(kv.Key, level);
                    using var es = entry.Open();
                    es.Write(kv.Value, 0, kv.Value.Length);
                }
            }
            set.Files[name] = path;
        }

        /// <summary>A10：普通 ZIP 后打补丁置 General Purpose Flag bit0（加密标志）。</summary>
        private static void WriteEncryptedFlagZip(SampleSet set, string name, string manifestJson,
            Dictionary<string, byte[]> entries)
        {
            WriteZip(set, name, CompressionLevel.Optimal, manifestJson, entries);
            string path = set[name];
            PatchZipHeaders(path, patchEncrypted: true, patchCrc: false, skipFirstLocalHeader: 0);
        }

        /// <summary>A7（pcraft 侧）：打补丁篡改最后一个 entry 的 CRC-32。</summary>
        private static void WriteBadCrcZip(SampleSet set, string name, string manifestJson,
            Dictionary<string, byte[]> entries)
        {
            WriteZip(set, name, CompressionLevel.Optimal, manifestJson, entries);
            string path = set[name];
            PatchZipHeaders(path, patchEncrypted: false, patchCrc: true, skipFirstLocalHeader: 1);
        }

        /// <summary>
        /// 在生成的 ZIP 字节上打补丁：local file header（PK\x03\x04）flags@6/CRC@14，
        /// central directory header（PK\x01\x02）flags@8/CRC@16。
        /// </summary>
        private static void PatchZipHeaders(string path, bool patchEncrypted, bool patchCrc, int skipFirstLocalHeader)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int localCount = 0;
            for (int i = 0; i + 4 <= bytes.Length - 4; i++)
            {
                if (bytes[i] == 'P' && bytes[i + 1] == 'K' && bytes[i + 2] == 0x03 && bytes[i + 3] == 0x04)
                {
                    bool skip = localCount < skipFirstLocalHeader;
                    localCount++;
                    if (skip) continue;
                    if (patchEncrypted)
                        bytes[i + 6] |= 0x01; // flags bit0 = encrypted
                    if (patchCrc)
                    {
                        bytes[i + 14] = 0xEF; bytes[i + 15] = 0xBE;
                        bytes[i + 16] = 0xAD; bytes[i + 17] = 0xDE; // CRC = 0xDEADBEEF
                    }
                }
                else if (bytes[i] == 'P' && bytes[i + 1] == 'K' && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02)
                {
                    // central directory：与 local header 顺序一致（单份），仅处理非跳过项
                    if (patchEncrypted && localCount > skipFirstLocalHeader)
                        bytes[i + 8] |= 0x01;
                    if (patchCrc && localCount > skipFirstLocalHeader)
                    {
                        bytes[i + 16] = 0xEF; bytes[i + 17] = 0xBE;
                        bytes[i + 18] = 0xAD; bytes[i + 19] = 0xDE;
                    }
                }
            }
            File.WriteAllBytes(path, bytes);
        }

        // ==================================================================
        // 工具
        // ==================================================================

        private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

        private static void WriteFile(SampleSet set, string name, string content)
        {
            WriteFile(set, name, Encoding.UTF8.GetBytes(content));
        }

        private static void WriteFile(SampleSet set, string name, byte[] bytes)
        {
            string path = Path.Combine(set.RootDir, name);
            File.WriteAllBytes(path, bytes);
            set.Files[name] = path;
        }
    }
}
