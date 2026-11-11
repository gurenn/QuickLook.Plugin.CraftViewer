using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickLook.Plugin.CraftViewer.Formats;

namespace QuickLook.Plugin.CraftViewer
{
    /// <summary>
    /// 预览面板（设计 §3）：棋盘格底 + 图片 + 来源角标（P1-2 三态）+ 错误区 + 元数据区。
    /// 只在 UI Dispatcher 创建/释放（§7.7）；Show 传入的 BitmapSource 必须已 Freeze。
    /// </summary>
    public partial class CraftPreviewPanel : UserControl, IDisposable
    {
        // P2-1 预留：高保真重渲染入口（本期不触发，不改宿主菜单）
#pragma warning disable CS6712, CS0414
        internal event Action HighFidelityRequested;
#pragma warning restore CS6712, CS0414

        public CraftPreviewPanel()
        {
            InitializeComponent();
        }

        /// <summary>展示图片（棋盘格底 + 角标）。</summary>
        public void Show(PreviewResult r, BitmapSource bmp)
        {
            imageArea.Visibility = Visibility.Visible;
            metadataArea.Visibility = Visibility.Collapsed;
            errorArea.Visibility = Visibility.Collapsed;
            badgeBorder.Visibility = Visibility.Visible;

            previewImage.Source = bmp; // 已 Freeze，可跨线程传入
            badgeText.Text = BadgeTextFor(r != null ? r.Source : PreviewSource.MetadataOnly,
                r != null ? Math.Max(r.PixelWidth, r.PixelHeight) : 0);
        }

        /// <summary>错误面板：类别 + 可读原因（P0-3）。</summary>
        public void ShowError(CraftErrorCategory cat, string detail)
        {
            imageArea.Visibility = Visibility.Collapsed;
            metadataArea.Visibility = Visibility.Collapsed;
            errorArea.Visibility = Visibility.Visible;
            badgeBorder.Visibility = Visibility.Collapsed;

            previewImage.Source = null;
            errorCategoryText.Text = "[" + cat + "]";
            errorDetailText.Text = string.IsNullOrEmpty(detail)
                ? "文件无法解析。"
                : detail;
        }

        /// <summary>MetadataOnly 降级面板：元数据摘要 + 告警。</summary>
        public void ShowMetadata(PreviewResult r)
        {
            imageArea.Visibility = Visibility.Collapsed;
            errorArea.Visibility = Visibility.Collapsed;
            metadataArea.Visibility = Visibility.Visible;
            badgeBorder.Visibility = Visibility.Visible;

            previewImage.Source = null;
            badgeText.Text = BadgeTextFor(PreviewSource.MetadataOnly, 0);

            metadataTitleText.Text = r != null ? r.BuildTitle() : string.Empty;
            metadataText.Text = r != null ? r.MetadataSummary : "无元数据。";

            var warnings = r != null ? r.Warnings.Where(w => !string.IsNullOrEmpty(w)).ToList() : null;
            if (warnings != null && warnings.Count > 0)
            {
                metadataWarningText.Text = "提示：" + string.Join("；", warnings);
                metadataWarningText.Visibility = Visibility.Visible;
            }
            else
            {
                metadataWarningText.Visibility = Visibility.Collapsed;
            }
        }

        public void Dispose()
        {
            previewImage.Source = null;
            HighFidelityRequested = null;
        }

        // ------------------------------------------------------------------

        private static string BadgeTextFor(PreviewSource source, int longerSide)
        {
            string size = longerSide > 0 ? " · " + longerSide + "px" : string.Empty;
            switch (source)
            {
                case PreviewSource.Embedded: return "内嵌预览" + size;
                case PreviewSource.Rendered: return "高保真渲染" + size;
                default: return "仅元数据";
            }
        }
    }
}
