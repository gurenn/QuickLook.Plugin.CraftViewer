using System;
using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;
using QuickLook.Plugin.CraftViewer.Formats;

namespace QuickLook.Plugin.CraftViewer.Rendering
{
    /// <summary>
    /// WPF PNG 解码（Rendering 层，Formats 层零 WPF 依赖的另一半）：
    /// 解码前用 PngSniffer 尺寸预检（超限抛 LimitExceeded，不解码，A11），
    /// BitmapImage OnLoad + DecodePixelWidth 适配 + Freeze 后返回（可跨线程回 UI）。
    /// </summary>
    public sealed class PngDecoder
    {
        /// <summary>显示级解码上限宽（超过则等比降采样解码，防超大图占用内存）。</summary>
        private const int MaxDecodeWidth = 4096;

        /// <summary>解码 PNG 字节为已 Freeze 的 BitmapSource。</summary>
        public BitmapSource Decode(byte[] png, CancellationToken token)
        {
            if (png == null || png.Length == 0)
                throw new CraftFormatException(CraftErrorCategory.BadPng, "[BadPng] PNG 数据为空");
            token.ThrowIfCancellationRequested();

            // 尺寸预检：超限直接抛 LimitExceeded，不进入解码器（不解码到 OOM，A11）
            PngSniffer.ValidatePng(png, out int w, out int h);

            try
            {
                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(png))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad; // 流关闭后仍可渲染
                    if (w > MaxDecodeWidth)
                        bmp.DecodePixelWidth = MaxDecodeWidth;   // 等比降采样解码
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                }
                token.ThrowIfCancellationRequested();
                bmp.Freeze(); // 返回前冻结，允许跨线程传给 UI（§7.7）
                return bmp;
            }
            catch (CraftFormatException) { throw; }
            catch (System.NotSupportedException ex)
            {
                throw new CraftFormatException(CraftErrorCategory.BadPng,
                    "[BadPng] PNG 无法解码（不支持的色彩类型或数据损坏）", ex);
            }
            catch (System.IO.IOException ex)
            {
                throw new CraftFormatException(CraftErrorCategory.BadPng,
                    "[BadPng] PNG 解码失败（IO 错误）", ex);
            }
        }

        /// <summary>
        /// 降采样 PNG 至长边 ≤ maxSide 并重编码为 PNG 字节（CLI 渲染输出的统一尺寸上限）。
        /// 已在限内或 maxSide ≤ 0 时原样返回。解码走同一预检（超限抛 LimitExceeded）。
        /// </summary>
        public byte[] Downsample(byte[] png, int maxSide, CancellationToken token)
        {
            if (png == null || png.Length == 0)
                throw new CraftFormatException(CraftErrorCategory.BadPng, "[BadPng] PNG 数据为空");
            token.ThrowIfCancellationRequested();

            PngSniffer.ValidatePng(png, out int w, out int h);
            int longer = Math.Max(w, h);
            if (maxSide <= 0 || longer <= maxSide)
                return png; // 无需缩放

            double k = (double)maxSide / longer;
            int tw = Math.Max(1, (int)Math.Round(w * k));
            int th = Math.Max(1, (int)Math.Round(h * k));

            try
            {
                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(png))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    if (w >= h) bmp.DecodePixelWidth = tw;   // 只给一边，WPF 等比缩放
                    else bmp.DecodePixelHeight = th;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                }
                token.ThrowIfCancellationRequested();
                bmp.Freeze();

                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var outMs = new MemoryStream())
                {
                    enc.Save(outMs);
                    return outMs.ToArray();
                }
            }
            catch (CraftFormatException) { throw; }
            catch (Exception ex)
            {
                throw new CraftFormatException(CraftErrorCategory.BadPng,
                    "[BadPng] 降采样重编码失败", ex);
            }
        }
    }
}
