using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MoveWindowEverywhere.Models;
using MoveWindowEverywhere.Native;
using MoveWindowEverywhere.Services;
using MoveWindowEverywhere.ViewModels;

namespace MoveWindowEverywhere.Views;

/// <summary>
/// 窗口选择器。Move 模式移动窗口，Restore 模式恢复窗口；两种模式共用搜索、缩略图和键盘导航。
/// </summary>
public sealed partial class SelectorWindow : Window
{
    private readonly SelectorViewModel _viewModel;
    private readonly WindowMover _mover;
    private readonly MonitorInfo? _targetMonitor;
    private readonly SelectorMode _mode;
    private readonly Action<string, string>? _notify;
    private readonly bool _closeOnFocusLost;
    private readonly bool _showThumbnails;
    private readonly ThumbnailLoader? _thumbnailLoader;
    private bool _isReady;
    private bool _completed;

    public SelectorWindow(
        SelectorViewModel viewModel,
        WindowMover mover,
        MonitorInfo? targetMonitor,
        Action<string, string>? notify = null,
        bool closeOnFocusLost = true,
        bool showThumbnails = true,
        WindowThumbnailService? thumbnailService = null,
        SelectorMode mode = SelectorMode.Move)
    {
        _viewModel = viewModel;
        _mover = mover;
        _targetMonitor = targetMonitor;
        _mode = mode;
        _notify = notify;
        _closeOnFocusLost = closeOnFocusLost;
        _showThumbnails = showThumbnails;

        if (showThumbnails && thumbnailService is not null)
        {
            _thumbnailLoader = new ThumbnailLoader(thumbnailService.TryCapture);
        }

        InitializeComponent();
        DataContext = viewModel;

        Loaded += OnLoaded;
        ContentRendered += OnContentRendered;
        Deactivated += OnDeactivated;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public SelectorViewModel ViewModel => _viewModel;

    public Visibility ThumbnailColumnVisibility => _showThumbnails ? Visibility.Visible : Visibility.Collapsed;

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= OnLoaded;
        ContentRendered -= OnContentRendered;
        Deactivated -= OnDeactivated;
        PreviewKeyDown -= OnPreviewKeyDown;

        _thumbnailLoader?.Dispose();
        base.OnClosed(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SearchTextBox.Focus();
        Keyboard.Focus(SearchTextBox);
        SearchTextBox.SelectAll();

        if (_viewModel.Windows.Count > 0)
        {
            _viewModel.SelectedEntry ??= _viewModel.Windows[0];
        }

        Dispatcher.BeginInvoke(() => _isReady = true);
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        PlaceInsideTargetMonitor();
        StartThumbnailLoading();
    }

    private void StartThumbnailLoading()
    {
        if (_thumbnailLoader is null)
        {
            return;
        }

        _thumbnailLoader.Start(
            _viewModel.Windows.Select(static entry => entry.Info),
            ApplyThumbnail,
            action => Dispatcher.BeginInvoke(DispatcherPriority.Background, action));
    }

    private void ApplyThumbnail(WindowInfo window, BitmapSource bitmap)
    {
        WindowEntryViewModel? entry = _viewModel.FindEntry(window);
        if (entry is not null)
        {
            entry.Thumbnail = bitmap;
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_isReady || !_closeOnFocusLost || _completed)
        {
            return;
        }

        Close();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _completed = true;
            Close();
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ConfirmSelection();
            return;
        }

        bool searchHasFocus = ReferenceEquals(Keyboard.FocusedElement, SearchTextBox);
        if (!searchHasFocus)
        {
            return;
        }

        if (e.Key == Key.Down)
        {
            e.Handled = true;
            _viewModel.MoveSelection(1);
            WindowListBox.ScrollIntoView(_viewModel.SelectedEntry);
        }
        else if (e.Key == Key.Up)
        {
            e.Handled = true;
            _viewModel.MoveSelection(-1);
            WindowListBox.ScrollIntoView(_viewModel.SelectedEntry);
        }
    }

    private void OnWindowItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ConfirmSelection();
    }

    private void ConfirmSelection()
    {
        WindowInfo? selected = _viewModel.SelectedWindow;
        if (selected is null)
        {
            _completed = true;
            Close();
            return;
        }

        _completed = true;
        Hide();

        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                WindowMoveResult result = _mode == SelectorMode.Restore
                    ? _mover.RestorePrevious(selected.Handle)
                    : _mover.MoveToMonitor(selected.Handle, _targetMonitor);

                if (!result.Success)
                {
                    _notify?.Invoke("Move Window Everywhere", result.Message);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理窗口操作时发生异常：{ex}");
                _notify?.Invoke(
                    "Move Window Everywhere",
                    _mode == SelectorMode.Restore ? "恢复窗口失败，详情见日志。" : "移动窗口失败，详情见日志。");
            }
            finally
            {
                Close();
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// 把选择器放到快捷键触发时鼠标所在显示器的工作区内。
    /// Restore 模式下该显示器只决定选择器出现在哪里，不影响窗口恢复目标。
    /// </summary>
    private void PlaceInsideTargetMonitor()
    {
        if (_targetMonitor is null)
        {
            return;
        }

        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !Win32.GetWindowRect(handle, out RECT current))
        {
            return;
        }

        Int32Rect target = WindowPlacementCalculator.ComputeCenteredRect(
            _targetMonitor.WorkRect,
            current.Width,
            current.Height);

        if (!Win32.SetWindowPos(
                handle,
                IntPtr.Zero,
                target.X,
                target.Y,
                target.Width,
                target.Height,
                Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED))
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Debug.WriteLine($"选择器定位失败，Win32 错误码 {error}");
        }
    }
}
