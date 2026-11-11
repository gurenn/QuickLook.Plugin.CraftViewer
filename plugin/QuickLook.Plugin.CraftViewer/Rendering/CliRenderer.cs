using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using QuickLook.Plugin.CraftViewer.Formats;

namespace QuickLook.Plugin.CraftViewer.Rendering
{
    /// <summary>
    /// 外部 CLI 受控渲染（P1-1，设计 §1 取舍#3）：
    /// UseShellExecute=false / CreateNoWindow=true / stdout+stderr 重定向 /
    /// WaitForExit 超时即 Kill；临时 PNG = %TEMP%\CraftViewer_{guid}.png。
    /// CLI 未配置或文件不存在 → CliUnavailable（不抛异常）；超时/失败/输出非法 → CliFailed。
    /// </summary>
    public static class CliRenderer
    {
        public enum Outcome
        {
            Rendered,
            CliUnavailable,
            CliFailed
        }

        /// <summary>
        /// 尝试用外部 CLI 渲染出临时 PNG。成功返回 true 并填充 result（PngBytes 已验签验尺寸）。
        /// </summary>
        public static bool TryRender(CraftKind kind, string inputPath, CliConfig cfg,
            out PreviewResult result, out Outcome outcome, CancellationToken token)
        {
            result = null;
            outcome = Outcome.CliUnavailable;

            // ---- CLI 就位检查：路径缺失/文件不存在 → CliUnavailable，不抛异常 ----
            string exe = kind == CraftKind.PhotoCraft
                ? cfg?.PhotocraftCliPath
                : cfg?.VectorcraftCliPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                return false;

            string temp = Path.Combine(Path.GetTempPath(),
                "CraftViewer_" + Guid.NewGuid().ToString("N") + ".png");

            try
            {
                token.ThrowIfCancellationRequested();

                var psi = new ProcessStartInfo
                {
                    // 不经 shell 拼接：固定参数 + 引号（报告 §5.5）
                    FileName = exe,
                    Arguments = string.Format(CultureInfo.InvariantCulture,
                        "convert \"{0}\" \"{1}\"", inputPath, temp),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    outcome = Outcome.CliFailed;
                    return false;
                }

                // 取消 → Kill（A13/A14）
                using (token.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(); }
                    catch { /* 进程可能已退出 */ }
                }))
                {
                    // 异步排水防死锁
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    int timeoutSeconds = cfg != null && cfg.TimeoutSeconds > 0
                        ? cfg.TimeoutSeconds
                        : CraftLimits.CliTimeoutSeconds;
                    if (!process.WaitForExit(timeoutSeconds * 1000))
                    {
                        try { if (!process.HasExited) process.Kill(); }
                        catch { }
                        outcome = Outcome.CliFailed; // 超时
                        return false;
                    }
                }

                if (token.IsCancellationRequested)
                    throw new OperationCanceledException(token);

                if (process.ExitCode != 0 || !File.Exists(temp))
                {
                    outcome = Outcome.CliFailed;
                    return false;
                }

                // 输出有界 + 验签验尺寸（失败 → CliFailed，防 OOM）
                var info = new FileInfo(temp);
                if (info.Length > CraftLimits.MaxPreviewEntryBytes)
                {
                    outcome = Outcome.CliFailed;
                    return false;
                }

                byte[] png = File.ReadAllBytes(temp);
                PngSniffer.ValidatePng(png, out int w, out int h);

                // 渲染输出统一尺寸上限（maxRenderSize，默认 512）：超出则降采样重编码
                int maxSide = cfg != null ? cfg.MaxRenderSize : CraftLimits.DefaultRenderMaxSide;
                if (maxSide > 0 && Math.Max(w, h) > maxSide)
                {
                    png = new PngDecoder().Downsample(png, maxSide, token);
                    PngSniffer.ValidatePng(png, out w, out h); // 复核缩放后的尺寸
                }

                result = new PreviewResult
                {
                    Kind = kind,
                    Source = PreviewSource.Rendered,
                    PngBytes = png,
                    PixelWidth = w,
                    PixelHeight = h,
                    TempPng = temp,
                    FileName = Path.GetFileName(inputPath)
                };
                outcome = Outcome.Rendered;
                return true;
            }
            catch (OperationCanceledException)
            {
                DeleteTemp(ref temp);
                throw; // 取消语义交由上层静默处理
            }
            catch (Exception)
            {
                // 进程启动失败（Win32Exception 等）、输出非法等 → CliFailed
                outcome = Outcome.CliFailed;
                DeleteTemp(ref temp);
                return false;
            }
        }

        /// <summary>删除临时 PNG；删除失败静默（防杀软/锁定，§7.8）。传入 null 不操作。</summary>
        public static void DeleteTemp(ref string tempPng)
        {
            string path = tempPng;
            tempPng = null;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 静默（§7.8）
            }
        }
    }
}
