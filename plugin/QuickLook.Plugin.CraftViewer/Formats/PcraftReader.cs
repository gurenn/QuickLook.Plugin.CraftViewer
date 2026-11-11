using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace QuickLook.Plugin.CraftViewer.Formats
{
    /// <summary>
    /// .pcraft 读取器（ZIP/目录 + manifest + 白名单 PNG）。
    /// ZIP64/加密 → UnsupportedZipFeature；manifest 缺失 → MissingManifest；
    /// format_version != 1 → FutureManifestVersion（版本过新，不误报损坏）；
    /// 白名单：manifest 字段值必须精确等于固定相对路径，否则视为该预览缺失继续降级，
    /// 绝不按 manifest 值拼任意路径（A9 路径穿越）。
    /// </summary>
    public static class PcraftReader
    {
        /// <summary>预览白名单：full > preview > thumb（§7.6）。Item1=manifest 字段名，Item2=固定相对路径。</summary>
        private static readonly Tuple<string, string>[] Whitelist =
        {
            Tuple.Create("full_composite", "composite/full.png"),
            Tuple.Create("composite", "composite/preview.png"),
            Tuple.Create("thumbnail", "thumb.png"),
        };

        private const int FormatVersion = 1;

        public static PreviewResult ReadPreview(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(path)) return ReadDirectoryPackage(path, token);
            return ReadZipPackage(path, token);
        }

        // ------------------------------------------------------------------
        // ZIP 包
        // ------------------------------------------------------------------

        private static PreviewResult ReadZipPackage(string path, CancellationToken token)
        {
            PreviewResult partial;
            byte[] previewPng = null;
            int w = 0, h = 0;
            string chosen = null;

            using (FileStream fs = OpenShared(path))
            {
                ZipArchive zip;
                try
                {
                    zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: true);
                }
                catch (InvalidDataException ex)
                {
                    throw new CraftFormatException(CraftErrorCategory.UnsupportedZipFeature,
                        "[UnsupportedZipFeature] ZIP 容器无法打开（可能为 ZIP64 或已损坏）", ex);
                }
                catch (NotSupportedException ex)
                {
                    throw new CraftFormatException(CraftErrorCategory.UnsupportedZipFeature,
                        "[UnsupportedZipFeature] 不支持的 ZIP 特性（ZIP64/加密）", ex);
                }

                using (zip)
                {
                    // ---- 加密/ZIP64 预检（net462 BCL 不强制，A10） ----
                    var manifestZipInfo = ZipInspector.Inspect(fs, "manifest.json");
                    if (manifestZipInfo != null && manifestZipInfo.Zip64)
                        throw new CraftFormatException(CraftErrorCategory.UnsupportedZipFeature,
                            "[UnsupportedZipFeature] 不支持的 ZIP 特性（ZIP64）");
                    if (manifestZipInfo != null && manifestZipInfo.Encrypted)
                        throw new CraftFormatException(CraftErrorCategory.UnsupportedZipFeature,
                            "[UnsupportedZipFeature] manifest.json 条目已加密，不支持的 ZIP 特性");

                    // ---- manifest.json（超限 → JsonTooLarge；读取失败 → UnsupportedZipFeature） ----
                    ZipArchiveEntry manifestEntry = FindEntry(zip, "manifest.json");
                    if (manifestEntry == null)
                        throw new CraftFormatException(CraftErrorCategory.MissingManifest,
                            "[MissingManifest] ZIP 包中缺少 manifest.json");

                    byte[] manifestBytes;
                    try
                    {
                        manifestBytes = ReadEntryBounded(manifestEntry, CraftLimits.MaxManifestBytes,
                            CraftErrorCategory.JsonTooLarge, "[JsonTooLarge] manifest.json 超过 4MB 上限");
                    }
                    catch (InvalidDataException ex)
                    {
                        throw new CraftFormatException(CraftErrorCategory.UnsupportedZipFeature,
                            "[UnsupportedZipFeature] manifest.json 读取失败（entry 加密或 ZIP 数据损坏）", ex);
                    }
                    catch (NotSupportedException ex)
                    {
                        throw new CraftFormatException(CraftErrorCategory.UnsupportedZipFeature,
                            "[UnsupportedZipFeature] 不支持的 ZIP 特性（ZIP64/加密）", ex);
                    }

                    PcraftManifest manifest = MinimalJson.ScanManifest(manifestBytes);
                    if (!manifest.HasFormatVersion)
                        throw new CraftFormatException(CraftErrorCategory.BadJson,
                            "[BadJson] manifest.json 缺少 format_version 字段");
                    if (manifest.FormatVersion != FormatVersion)
                        throw new CraftFormatException(CraftErrorCategory.FutureManifestVersion,
                            "[FutureManifestVersion] 版本过新（manifest v" + manifest.FormatVersion +
                            "，插件支持 1）——不误报损坏");

                    partial = new PreviewResult
                    {
                        Kind = CraftKind.PhotoCraft,
                        FormatVersion = FormatVersion,
                        Generator = manifest.Generator,
                        FileName = Path.GetFileName(path)
                    };

                    // ---- 白名单逐级尝试（full > preview > thumb） ----
                    foreach (var wl in Whitelist)
                    {
                        token.ThrowIfCancellationRequested();
                        string declared = GetManifestValue(manifest, wl.Item1);

                        // manifest 字段值必须精确等于白名单路径，否则视为"该预览缺失"（A9）
                        if (string.IsNullOrEmpty(declared) ||
                            !declared.Equals(wl.Item2, StringComparison.Ordinal))
                            continue;

                        ZipArchiveEntry pngEntry = FindEntry(zip, wl.Item2);
                        if (pngEntry == null) continue;

                        // 加密/ZIP64 预检（A10）
                        var pngZipInfo = ZipInspector.Inspect(fs, wl.Item2);
                        if (pngZipInfo != null && pngZipInfo.Zip64)
                            throw new CraftFormatException(CraftErrorCategory.UnsupportedZipFeature,
                                "[UnsupportedZipFeature] 不支持的 ZIP 特性（ZIP64）");
                        if (pngZipInfo != null && pngZipInfo.Encrypted)
                            throw new CraftFormatException(CraftErrorCategory.UnsupportedZipFeature,
                                "[UnsupportedZipFeature] 预览条目 " + wl.Item2 + " 已加密，不支持的 ZIP 特性");

                        if (pngEntry.Length > CraftLimits.MaxPreviewEntryBytes)
                            throw new CraftFormatException(CraftErrorCategory.LimitExceeded,
                                string.Format(CultureInfo.InvariantCulture,
                                    "[LimitExceeded] 预览条目 {0} 超过大小上限", wl.Item2));

                        byte[] png;
                        try
                        {
                            png = ReadEntryBounded(pngEntry, CraftLimits.MaxPreviewEntryBytes,
                                CraftErrorCategory.LimitExceeded,
                                "[LimitExceeded] 预览条目超过大小上限");
                        }
                        catch (InvalidDataException ex)
                        {
                            // A7：坏 CRC 等容器级损坏 → 走降级链（CLI/元数据），不当硬错误
                            throw new CraftFormatException(CraftErrorCategory.BadPng,
                                "[BadPng] 预览 PNG " + wl.Item2 + " 读取失败（CRC 校验错误等）", ex);
                        }
                        catch (NotSupportedException ex)
                        {
                            throw new CraftFormatException(CraftErrorCategory.UnsupportedZipFeature,
                                "[UnsupportedZipFeature] 不支持的 ZIP 特性（ZIP64/加密）", ex);
                        }

                        PngSniffer.ValidatePng(png, out w, out h); // 失败抛 BadPng/LimitExceeded → Loader 降级

                        // CRC-32 校验（net462 BCL 不校验，A7：坏 CRC → 降级链）
                        if (pngZipInfo != null && !pngZipInfo.Zip64)
                        {
                            uint actual = Crc32.Compute(png, 0, png.Length);
                            if (actual != pngZipInfo.Crc32)
                                throw new CraftFormatException(CraftErrorCategory.BadPng,
                                    string.Format(CultureInfo.InvariantCulture,
                                        "[BadPng] 预览 PNG {0} CRC 校验失败（中央目录 0x{1:X8} ≠ 实际 0x{2:X8}）",
                                        wl.Item2, pngZipInfo.Crc32, actual));
                        }

                        previewPng = png;
                        chosen = wl.Item2;
                        break;
                    }
                }
            }

            if (previewPng != null)
            {
                partial.PngBytes = previewPng;
                partial.PixelWidth = w;
                partial.PixelHeight = h;
                partial.Source = PreviewSource.Embedded;
                partial.AddWarning("预览来源：" + chosen);
                return partial;
            }

            // 无内嵌预览是正常情况（默认 SaveOptions 无 thumbnail），返回占位而非错误
            return PreviewResult.NoPreview(CraftKind.PhotoCraft, FormatVersion,
                partial.Generator, partial.FileName, null);
        }

        // ------------------------------------------------------------------
        // 目录包（固定相对路径，白名单同样生效）
        // ------------------------------------------------------------------

        private static PreviewResult ReadDirectoryPackage(string path, CancellationToken token)
        {
            string manifestPath = System.IO.Path.Combine(path, "manifest.json");
            if (!File.Exists(manifestPath))
                throw new CraftFormatException(CraftErrorCategory.MissingManifest,
                    "[MissingManifest] 目录包缺少 manifest.json");

            var manifestInfo = new FileInfo(manifestPath);
            if (manifestInfo.Length > CraftLimits.MaxManifestBytes)
                throw new CraftFormatException(CraftErrorCategory.JsonTooLarge,
                    "[JsonTooLarge] manifest.json 超过 4MB 上限");

            byte[] manifestBytes = File.ReadAllBytes(manifestPath);
            PcraftManifest manifest = MinimalJson.ScanManifest(manifestBytes);
            if (!manifest.HasFormatVersion)
                throw new CraftFormatException(CraftErrorCategory.BadJson,
                    "[BadJson] manifest.json 缺少 format_version 字段");
            if (manifest.FormatVersion != FormatVersion)
                throw new CraftFormatException(CraftErrorCategory.FutureManifestVersion,
                    "[FutureManifestVersion] 版本过新（manifest v" + manifest.FormatVersion +
                    "，插件支持 1）——不误报损坏");

            var result = new PreviewResult
            {
                Kind = CraftKind.PhotoCraft,
                FormatVersion = FormatVersion,
                Generator = manifest.Generator,
                FileName = manifestInfo.Name == "manifest.json"
                    ? new DirectoryInfo(path).Name
                    : manifestInfo.Name
            };

            foreach (var wl in Whitelist)
            {
                token.ThrowIfCancellationRequested();
                string declared = GetManifestValue(manifest, wl.Item1);
                if (string.IsNullOrEmpty(declared) ||
                    !declared.Equals(wl.Item2, StringComparison.Ordinal))
                    continue;

                string pngPath = System.IO.Path.Combine(path, wl.Item2); // 白名单固定相对路径，无穿越风险
                if (!File.Exists(pngPath)) continue;

                var pngInfo = new FileInfo(pngPath);
                if (pngInfo.Length > CraftLimits.MaxPreviewEntryBytes)
                    throw new CraftFormatException(CraftErrorCategory.LimitExceeded,
                        string.Format(CultureInfo.InvariantCulture,
                            "[LimitExceeded] 预览文件 {0} 超过大小上限", wl.Item2));

                byte[] png = File.ReadAllBytes(pngPath);
                PngSniffer.ValidatePng(png, out int w, out int h);
                result.PngBytes = png;
                result.PixelWidth = w;
                result.PixelHeight = h;
                result.Source = PreviewSource.Embedded;
                result.AddWarning("预览来源：" + wl.Item2 + "（目录包）");
                return result;
            }

            return PreviewResult.NoPreview(CraftKind.PhotoCraft, FormatVersion,
                manifest.Generator, result.FileName, null);
        }

        // ------------------------------------------------------------------
        // 内部工具
        // ------------------------------------------------------------------

        private static FileStream OpenShared(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }

        /// <summary>精确匹配 entry 名（不区分目录前缀写法差异一律按官方固定名查找）。</summary>
        private static ZipArchiveEntry FindEntry(ZipArchive zip, string fullName)
        {
            foreach (var e in zip.Entries)
                if (string.Equals(e.FullName, fullName, StringComparison.Ordinal))
                    return e;
            return null;
        }

        private static string GetManifestValue(PcraftManifest m, string key)
        {
            switch (key)
            {
                case "full_composite": return m.FullComposite;
                case "composite": return m.Composite;
                case "thumbnail": return m.Thumbnail;
                default: return null;
            }
        }

        /// <summary>有界读取 entry 内容；超过 cap 抛 limitCategory。</summary>
        private static byte[] ReadEntryBounded(ZipArchiveEntry entry, long cap,
            CraftErrorCategory limitCategory, string limitMessage)
        {
            using var es = entry.Open();
            using var ms = new MemoryStream();
            var buf = new byte[64 * 1024];
            long total = 0;
            int n;
            while ((n = es.Read(buf, 0, buf.Length)) > 0)
            {
                total += n;
                if (total > cap)
                    throw new CraftFormatException(limitCategory, limitMessage);
                ms.Write(buf, 0, n);
            }
            return ms.ToArray();
        }
    }
}
