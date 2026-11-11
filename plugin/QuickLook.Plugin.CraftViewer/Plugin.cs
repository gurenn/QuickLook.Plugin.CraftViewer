using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickLook.Common.Plugin;
using QuickLook.Plugin.CraftViewer.Formats;
using QuickLook.Plugin.CraftViewer.Rendering;

namespace QuickLook.Plugin.CraftViewer
{
    /// <summary>
    /// 后台加载产物（避免 net462 无 System.ValueTuple 的元组语法）。
    /// </summary>
    internal sealed class LoadOutcome
    {
        public PreviewResult Result;
        public BitmapSource Bitmap;
    }

    /// <summary>
    /// IViewer 生命周期编排（设计 §4 时序）：
    /// View 内 _generation++ + 新 CTS；后台 Load 全程不抛异常；
    /// 回 UI 前核对 generation 与 token（A12/A13/A14）；Cleanup 取消/释放/删临时。
    /// </summary>
    public sealed class Plugin : IViewer
    {
        private int _generation;
        private CancellationTokenSource _cts;
        private CraftPreviewPanel _panel;
        private string _tempPng;

        // CLI 配置必须静态共享：宿主 FindMatch 后用 CreateInstance 新建预览实例且不调 Init
        // （上游 PluginManager.cs：`(matched).GetType().CreateInstance<IViewer>()`），
        // 实例字段在预览实例里永远是默认空配置。
        private static readonly object _cfgLock = new object();
        private static CliConfig _sharedConfig;

        /// <summary>进程级共享 CLI 配置（首次访问时从插件目录读取，含宿主 tag 匹配的默认值回退）。</summary>
        private static CliConfig GetSharedConfig()
        {
            if (_sharedConfig != null) return _sharedConfig;
            lock (_cfgLock)
            {
                if (_sharedConfig != null) return _sharedConfig;
                try
                {
                    string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    _sharedConfig = CliConfig.Load(dir) ?? new CliConfig();
#if DEBUG_TRACE
                    Diag("cfg", "dir=" + dir + " vc=" + _sharedConfig.VectorcraftCliPath +
                        " prefer=" + _sharedConfig.PreferRendered + " max=" + _sharedConfig.MaxRenderSize);
#endif
                }
                catch (Exception ex)
                {
                    _sharedConfig = new CliConfig();
#if DEBUG_TRACE
                    Diag("cfg", "EX " + ex.Message);
#endif
                }
                return _sharedConfig;
            }
        }

        public int Priority => 10;

        public void Init()
        {
            // QuickLook 仅在启动插件匹配实例上调 Init（预览实例不调，见 GetSharedConfig 注释）。
            // 这里预热一次共享配置，保持与旧版为兼容的加载时机一致；失败静默走默认值。
            GetSharedConfig();
        }

#if DEBUG_TRACE
        /// <summary>临时诊断输出（仅 DEBUG_TRACE 编译），写 %TEMP%\CraftViewer_trace.log。</summary>
        internal static void Diag(string tag, string msg)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "CraftViewer_trace.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " [" + tag + "] " + msg + Environment.NewLine);
            }
            catch { }
        }
#endif

        public bool CanHandle(string path)
        {
            try
            {
                return CraftFormatDetector.Sniff(path) != CraftKind.Unknown;
            }
            catch
            {
                // CanHandle 内 try/catch 全吞（设计 T04 自检①）
                return false;
            }
        }

        public void Prepare(string path, ContextObject context)
        {
            context.PreferredSize = new Size(960, 720);
            context.Title = Path.GetFileName(path);
            context.Theme = Themes.None;
        }

        public void View(string path, ContextObject context)
        {
            // 每次生成新代次 + 新 CTS（旧代次被核对机制自然废弃）
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _generation++;
            int myGen = _generation;

            _panel?.Dispose();
            _panel = new CraftPreviewPanel();
            context.ViewerContent = _panel;
            context.IsBusy = true;

            CraftKind kind = CraftKind.Unknown;
            try { kind = CraftFormatDetector.Sniff(path); }
            catch { kind = CraftKind.Unknown; }

            _ = LoadAsync(path, context, kind, myGen, _cts.Token); // 内部捕获全部异常
        }

        public void Cleanup()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _panel?.Dispose();
            _panel = null;
            CliRenderer.DeleteTemp(ref _tempPng);
        }

        // ------------------------------------------------------------------

        private async Task LoadAsync(string path, ContextObject context, CraftKind kind,
            int myGen, CancellationToken token)
        {
            var dispatcher = (context.Source as DispatcherObject)?.Dispatcher;
            if (dispatcher == null) return;

            try
            {
                LoadOutcome outcomeData = await Task.Run(() =>
                {
                    var r = CraftPreviewLoader.Load(path, kind, GetSharedConfig(), token);
                    BitmapSource bmp = null;
                    if (r.PngBytes != null)
                        bmp = new PngDecoder().Decode(r.PngBytes, token); // 后台解码 + Freeze
                    return new LoadOutcome { Result = r, Bitmap = bmp };
                }, token).ConfigureAwait(false);

                var result = outcomeData.Result;
                var bitmap = outcomeData.Bitmap;

                if (result.TempPng != null)
                    _tempPng = result.TempPng;

                // 旧代次结果：丢弃，不回写、不设 IsBusy（A12/A13/A14）
                if (_generation != myGen || token.IsCancellationRequested)
                {
                    string stale = result.TempPng;
                    if (_tempPng != stale) CliRenderer.DeleteTemp(ref stale);
                    return;
                }

                await dispatcher.InvokeAsync(() =>
                {
                    if (_generation != myGen || token.IsCancellationRequested) return; // 二次核对

                    if (result.Source == PreviewSource.MetadataOnly)
                        _panel.ShowMetadata(result);
                    else
                        _panel.Show(result, bitmap);

                    if (bitmap != null && result.PixelWidth > 0)
                        context.SetPreferredSizeFit(new Size(result.PixelWidth, result.PixelHeight), 0.8);
                    context.Title = result.BuildTitle();
                    context.IsBusy = false; // 全路径可达：成功/降级均置 false
                });
            }
            catch (OperationCanceledException)
            {
                // 静默退出（设计 §4）
            }
            catch (CraftFormatException cfe)
            {
                await SafeShowError(dispatcher, myGen, context, cfe.Category, cfe.Message);
            }
            catch (Exception ex)
            {
                // 最终兜底：任何异常不允许逃出后台任务（P0-3）
                await SafeShowError(dispatcher, myGen, context, CraftErrorCategory.IoError,
                    "[IoError] " + ex.Message);
            }
        }

        private async Task SafeShowError(Dispatcher dispatcher, int myGen,
            ContextObject context, CraftErrorCategory category, string detail)
        {
            try
            {
                await dispatcher.InvokeAsync(() =>
                {
                    if (_generation != myGen) return;
                    _panel?.ShowError(category, detail);
                    context.IsBusy = false;
                });
            }
            catch
            {
                // dispatcher 已不可用（窗口关闭竞态）：静默
            }
        }
    }
}
