using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using QuickLook.Plugin.CraftViewer.Formats;

namespace ProbeCraftFormats
{
    /// <summary>
    /// M0 格式探针：对合成样本批量验证 Reader roundtrip + 对抗断言（设计 T03）。
    /// 退出码 0 = 全过；非 0 = 有失败并打印逐项表。
    /// </summary>
    internal static class Program
    {
        private static int _pass;
        private static int _fail;
        private static readonly List<string> Results = new List<string>();

        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string samplesDir = args.Length > 0 ? args[0] : Path.Combine(baseDir, "samples");

            Console.WriteLine("==== SampleGenerator：产出样本到 " + samplesDir);
            SampleGenerator.SampleSet set;
            try
            {
                set = SampleGenerator.GenerateAll(samplesDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine("样本生成失败：" + ex);
                return 2;
            }
            Console.WriteLine("样本数：" + set.Files.Count);
            Console.WriteLine();

            var token = CancellationToken.None;

            // ------------------------------------------------------------------
            // 正样本：roundtrip（PngBytes == 嵌入的 PNG 原字节）+ 版本/来源断言
            // ------------------------------------------------------------------
            Roundtrip(set["vc_v1_inline.vectorcraft"], "vc_v1_inline", CraftKind.VectorCraft, 1, token);
            Roundtrip(set["vc_v2_drawcraft.drawcraft"], "vc_v2_drawcraft", CraftKind.VectorCraft, 2, token);
            Roundtrip(set["vc_v3_gzip.vectorcraft"], "vc_v3_gzip", CraftKind.VectorCraft, 3, token);
            Roundtrip(set["vc_v4_blob.vectorcraft"], "vc_v4_blob", CraftKind.VectorCraft, 4, token);

            // A11 正例：1×1 正常显示不拉伸失真（探针层断言能取出并验证通过）
            Roundtrip(set["a11_tiny_1x1.vectorcraft"], "a11_tiny_1x1", CraftKind.VectorCraft, 1, token);

            NoPreview(set["vc_no_preview.vectorcraft"], "vc_no_preview", token);

            Roundtrip(set["pcraft_store.pcraft"], "pcraft_store", CraftKind.PhotoCraft, 1, token, "composite/full.png");
            Roundtrip(set["pcraft_deflate.pcraft"], "pcraft_deflate", CraftKind.PhotoCraft, 1, token, "composite/full.png");
            Roundtrip(set["pcraft_manifest_not_first.pcraft"], "pcraft_manifest_not_first", CraftKind.PhotoCraft, 1, token, "composite/full.png");
            Roundtrip(set["pcraft_prio_preview.pcraft"], "pcraft_prio_preview", CraftKind.PhotoCraft, 1, token, "composite/preview.png");
            Roundtrip(set["pcraft_prio_thumb.pcraft"], "pcraft_prio_thumb", CraftKind.PhotoCraft, 1, token, "thumb.png");
            Roundtrip(set["pcraft_dir.pcraft"], "pcraft_dir", CraftKind.PhotoCraft, 1, token, "composite/full.png", isDirectory: true);

            // ------------------------------------------------------------------
            // Detector 命中/不命中断言（P0-1）
            // ------------------------------------------------------------------
            Sniff(set["vc_v1_inline.vectorcraft"], CraftKind.VectorCraft, "sniff_vc_v1");
            Sniff(set["vc_v2_drawcraft.drawcraft"], CraftKind.VectorCraft, "sniff_vc_v2_drawcraft");
            Sniff(set["vc_v3_gzip.vectorcraft"], CraftKind.VectorCraft, "sniff_vc_v3_gzip");
            Sniff(set["pcraft_store.pcraft"], CraftKind.PhotoCraft, "sniff_pcraft_store");
            Sniff(set["pcraft_dir.pcraft"], CraftKind.PhotoCraft, "sniff_pcraft_dir");
            Sniff(set["a2_wrong_format.vectorcraft"], CraftKind.Unknown, "sniff_a2_wrong_format");
            Sniff(set["a3_version99.vectorcraft"], CraftKind.VectorCraft, "sniff_a3_future_version_hit");
            Sniff(set["a8_no_manifest.pcraft"], CraftKind.Unknown, "sniff_a8_no_manifest");

            // ------------------------------------------------------------------
            // MinimalJson 转义单元检查（设计 T02 自检②：\n \" \\ \uXXXX）
            // ------------------------------------------------------------------
            MinimalJsonUnescape();

            // ------------------------------------------------------------------
            // 负样本 A1–A11：异常被抛出且 Category 与预期一致（catch 内断言，进程不崩）
            // ------------------------------------------------------------------
            Negative(set["a1_bad_gzip.vectorcraft"], CraftErrorCategory.BadGzip, "a1_bad_gzip");
            Negative(set["a2_wrong_format.vectorcraft"], CraftErrorCategory.WrongFormat, "a2_wrong_format");
            Negative(set["a3_version99.vectorcraft"], CraftErrorCategory.FutureVersion, "a3_version99");
            Negative(set["a4_blob_oor.vectorcraft"], CraftErrorCategory.BlobOutOfRange, "a4_blob_oor");
            Negative(set["a4_blob_overflow.vectorcraft"], CraftErrorCategory.BlobOutOfRange, "a4_blob_overflow");
            Negative(set["a5_bomb.vectorcraft"], CraftErrorCategory.DecompressionBomb, "a5_decompression_bomb");
            Negative(set["a6_huge_json.vectorcraft"], CraftErrorCategory.LimitExceeded, "a6_huge_json");
            Negative(set["a6_deep_json.vectorcraft"], CraftErrorCategory.LimitExceeded, "a6_deep_json");
            Negative(set["a7_bad_png.vectorcraft"], CraftErrorCategory.BadPng, "a7_bad_png");
            Negative(set["a7_bad_crc.pcraft"], CraftErrorCategory.BadPng, "a7_bad_crc");
            Negative(set["a8_no_manifest.pcraft"], CraftErrorCategory.MissingManifest, "a8_no_manifest");
            NoThrowMetadataOnly(set["a9_path_traversal.pcraft"], "a9_path_traversal", token);
            Negative(set["a10_encrypted.pcraft"], CraftErrorCategory.UnsupportedZipFeature, "a10_encrypted_zip");
            Negative(set["a11_huge_declared.vectorcraft"], CraftErrorCategory.LimitExceeded, "a11_huge_png_declared");

            // ------------------------------------------------------------------
            // 汇总
            // ------------------------------------------------------------------
            Console.WriteLine("==== 结果表");
            foreach (var line in Results) Console.WriteLine(line);
            Console.WriteLine();
            Console.WriteLine("通过 {0} / 失败 {1}", _pass, _fail);
            return _fail == 0 ? 0 : 1;
        }

        // ==================================================================

        private static void Roundtrip(string path, string name, CraftKind kind, int version,
            CancellationToken token, string expectedWarningContains = null, bool isDirectory = false)
        {
            bool ok = true;
            try
            {
                var r = kind == CraftKind.VectorCraft
                    ? VectorCraftReader.ReadPreview(path, token)
                    : PcraftReader.ReadPreview(path, token);

                ok &= Assert(name, "Kind", kind == r.Kind, "期望 {0} 实得 {1}", kind, r.Kind);
                ok &= Assert(name, "Version", version == r.FormatVersion, "期望 {0} 实得 {1}", version, r.FormatVersion);
                ok &= Assert(name, "Source=Embedded", r.Source == PreviewSource.Embedded, "实得 {0}", r.Source);
                ok &= Assert(name, "PngBytes 非空", r.PngBytes != null && r.PngBytes.Length > 0, "为空");
                ok &= Assert(name, "尺寸>0", r.PixelWidth > 0 && r.PixelHeight > 0,
                    "实得 {0}×{1}", r.PixelWidth, r.PixelHeight);
                if (!isDirectory)
                {
                    // 目录包 full.png 与 ZIP 内字节不同源，不做字节级 roundtrip
                    ok &= Assert(name, "PngBytes 尾部为 IEND",
                        r.PngBytes.Length >= 12 && r.PngBytes[r.PngBytes.Length - 8] == 'I'
                        && r.PngBytes[r.PngBytes.Length - 7] == 'E'
                        && r.PngBytes[r.PngBytes.Length - 6] == 'N'
                        && r.PngBytes[r.PngBytes.Length - 5] == 'D', "PNG 结构不完整");
                }
                if (expectedWarningContains != null)
                {
                    ok &= Assert(name, "Warnings 含 " + expectedWarningContains,
                        r.Warnings.Any(w => w != null && w.Contains(expectedWarningContains)),
                        "实得 [{0}]", string.Join("; ", r.Warnings));
                }
                if (ok) Pass(name);
            }
            catch (Exception ex)
            {
                Fail(name, "不应抛异常：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        private static void NoPreview(string path, string name, CancellationToken token)
        {
            bool ok = true;
            try
            {
                var r = VectorCraftReader.ReadPreview(path, token);
                ok &= Assert(name, "Source=MetadataOnly", r.Source == PreviewSource.MetadataOnly, "实得 {0}", r.Source);
                ok &= Assert(name, "PngBytes=null", r.PngBytes == null, "非空");
                ok &= Assert(name, "MetadataSummary 非空", !string.IsNullOrEmpty(r.MetadataSummary), "为空");
                if (ok) Pass(name);
            }
            catch (Exception ex)
            {
                Fail(name, "不应抛异常：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        private static void Sniff(string path, CraftKind expected, string name)
        {
            CraftKind k = CraftFormatDetector.Sniff(path);
            if (Assert(name, "Sniff", expected == k, "期望 {0} 实得 {1}", expected, k))
                Pass(name);
        }

        private static void Negative(string path, CraftErrorCategory expectedCategory, string name)
        {
            // 按扩展名选 Reader（Sniff=Unknown 时仍需验证 Reader 的错误类别）
            bool isPcraft = string.Equals(Path.GetExtension(path), ".pcraft", StringComparison.OrdinalIgnoreCase);
            try
            {
                var r = isPcraft
                    ? PcraftReader.ReadPreview(path, CancellationToken.None)
                    : VectorCraftReader.ReadPreview(path, CancellationToken.None);
                Fail(name, "期望抛出 " + expectedCategory + "，实际返回 Source=" + r.Source);
            }
            catch (CraftFormatException cfe)
            {
                if (Assert(name, "Category", cfe.Category == expectedCategory,
                        "期望 {0} 实得 {1}（{2}）", expectedCategory, cfe.Category, cfe.Message))
                    Pass(name);
            }
            catch (Exception ex)
            {
                Fail(name, "抛出了非 CraftFormatException：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        /// <summary>A9：白名单不匹配 → 视为缺失，不抛异常、不读文件系统外内容。</summary>
        private static void NoThrowMetadataOnly(string path, string name, CancellationToken token)
        {
            bool ok = true;
            try
            {
                var r = PcraftReader.ReadPreview(path, token);
                ok &= Assert(name, "Source=MetadataOnly", r.Source == PreviewSource.MetadataOnly, "实得 {0}", r.Source);
                ok &= Assert(name, "PngBytes=null（未读取穿越目标）", r.PngBytes == null, "非空");
                if (ok) Pass(name);
            }
            catch (Exception ex)
            {
                Fail(name, "A9 应静默降级而非抛异常：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        private static void MinimalJsonUnescape()
        {
            const string name = "minimal_json_unescape";
            bool ok = true;
            try
            {
                string json = "{\"format\":\"vectorcraft\",\"version\":3," +
                              "\"generator\":\"A\\u0041B\\n\\\"q\\\"\\\\\"," +
                              "\"document\":{\"deep\":{\"x\":[1,2,{\"y\":\"z\"}]}},\"ignored\":[[[[1]]]]}";
                var env = MinimalJson.ScanEnvelope(System.Text.Encoding.UTF8.GetBytes(json), tolerant: false);
                ok &= Assert(name, "format", env.Format == "vectorcraft", "实得 {0}", env.Format);
                ok &= Assert(name, "version", env.Version == 3 && env.HasVersion, "实得 {0}", env.Version);
                ok &= Assert(name, "generator 反转义", env.Generator == "AAB\n\"q\"\\", "实得 [{0}]", env.Generator);
                ok &= Assert(name, "Complete", env.Complete, "未闭合");
                if (ok) Pass(name);
            }
            catch (Exception ex)
            {
                Fail(name, "异常：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        // ==================================================================

        private static bool Assert(string name, string what, bool ok, string failFormat, params object[] args)
        {
            if (!ok)
                Fail(name, what + "：[" + string.Format(failFormat, args) + "]");
            return ok;
        }

        private static void Pass(string name)
        {
            _pass++;
            Results.Add("[PASS] " + name);
        }

        private static void Fail(string name, string detail)
        {
            _fail++;
            Results.Add("[FAIL] " + name + " —— " + detail);
        }
    }
}
