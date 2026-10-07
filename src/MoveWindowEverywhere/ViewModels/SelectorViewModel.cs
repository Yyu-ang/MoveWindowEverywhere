using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using MoveWindowEverywhere.Models;
using MoveWindowEverywhere.Services;

namespace MoveWindowEverywhere.ViewModels;

public enum SelectorMode
{
    Move = 0,
    Restore = 1,
}

/// <summary>列表中的一行：缩略图、标题、进程名、PID、状态与图标。</summary>
public sealed class WindowEntryViewModel : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private bool _iconResolved;
    private ImageSource? _thumbnail;

    public WindowEntryViewModel(WindowInfo info)
    {
        Info = info;
    }

    public WindowInfo Info { get; }

    public string Title => string.IsNullOrWhiteSpace(Info.Title) ? "（无标题）" : Info.Title;

    public string ProcessText => $"{Info.ProcessName} · PID {Info.ProcessId}";

    public string StateText => Info.State switch
    {
        WindowStateKind.Minimized => "最小化",
        WindowStateKind.Maximized => "最大化",
        _ => string.Empty,
    };

    public int MonitorIndex => Info.MonitorIndex;

    public string MonitorLabel => Info.MonitorLabel;

    public bool IsOnTargetMonitor => Info.IsOnTargetMonitor;

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value))
            {
                return;
            }

            _thumbnail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    public ImageSource? Icon
    {
        get
        {
            if (!_iconResolved)
            {
                _iconResolved = true;
                _icon = IconHelper.ToImageSource(Info.IconHandle);
            }

            return _icon;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// 窗口选择器视图模型：维护窗口列表、搜索筛选与选中项。
/// Move 模式列出可移动窗口；Restore 模式只接收调用方筛出的有恢复历史窗口。
/// </summary>
public sealed class SelectorViewModel : INotifyPropertyChanged
{
    private readonly List<WindowEntryViewModel> _all = new();
    private readonly Dictionary<WindowInfo, WindowEntryViewModel> _entryByWindow = new();
    private string _searchText = string.Empty;

    public SelectorViewModel(MonitorInfo? targetMonitor, SelectorMode mode = SelectorMode.Move)
    {
        TargetMonitor = targetMonitor;
        Mode = mode;
    }

    public ObservableCollection<WindowEntryViewModel> Windows { get; } = new();

    public MonitorInfo? TargetMonitor { get; }

    public SelectorMode Mode { get; }

    public string TargetMonitorText => TargetMonitor?.ShortDescription ?? "未捕获到目标显示器";

    public string HeaderText => Mode == SelectorMode.Move ? "移动窗口到目标显示器" : "恢复窗口";

    public string ContextText => Mode == SelectorMode.Move ? TargetMonitorText : "只显示当前会话中可恢复的窗口";

    public string EmptyTitle => Mode == SelectorMode.Move ? "没有可移动的窗口" : "没有可恢复的窗口";

    public string EmptySubtitle => Mode == SelectorMode.Move
        ? "当前没有匹配筛选条件的窗口，按 Esc 关闭"
        : "没有匹配的恢复历史，按 Esc 关闭";

    public string ActionHint => Mode == SelectorMode.Move
        ? "↑ ↓ 选择 · Enter 移动 · Esc 取消 · 双击移动"
        : "↑ ↓ 选择 · Enter 恢复 · Esc 取消 · 双击恢复";

    public string FooterText => Mode == SelectorMode.Move
        ? "目标：快捷键触发时鼠标所在的显示器"
        : "恢复：位置 + 尺寸 + 窗口状态";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value)
            {
                return;
            }

            _searchText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSearchEmpty));
            ApplyFilter();
        }
    }

    public bool IsSearchEmpty => SearchText.Length == 0;

    public bool IsEmpty => Windows.Count == 0;

    public WindowEntryViewModel? SelectedEntry { get; set; }

    public WindowInfo? SelectedWindow => SelectedEntry?.Info;

    public WindowEntryViewModel? FindEntry(WindowInfo window) =>
        window is not null && _entryByWindow.TryGetValue(window, out WindowEntryViewModel? entry) ? entry : null;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetWindows(IEnumerable<WindowInfo> windows)
    {
        _all.Clear();
        _entryByWindow.Clear();

        foreach (WindowInfo window in windows)
        {
            var entry = new WindowEntryViewModel(window);
            _all.Add(entry);
            _entryByWindow[window] = entry;
        }

        ApplyFilter();
    }

    public void ApplyFilter()
    {
        IReadOnlyList<WindowInfo> filtered = WindowSearcher.Search(_all.Select(static e => e.Info), SearchText);

        Windows.Clear();
        foreach (WindowInfo window in filtered)
        {
            if (_entryByWindow.TryGetValue(window, out WindowEntryViewModel? entry))
            {
                Windows.Add(entry);
            }
        }

        if (Windows.Count > 0)
        {
            if (SelectedEntry is null || !Windows.Contains(SelectedEntry))
            {
                SelectedEntry = Windows[0];
            }
        }
        else
        {
            SelectedEntry = null;
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(SelectedEntry));
    }

    public void MoveSelection(int delta)
    {
        if (Windows.Count == 0)
        {
            return;
        }

        int currentIndex = SelectedEntry is null ? -1 : Windows.IndexOf(SelectedEntry);
        int nextIndex = currentIndex + delta;
        if (nextIndex < 0)
        {
            nextIndex = 0;
        }
        else if (nextIndex >= Windows.Count)
        {
            nextIndex = Windows.Count - 1;
        }

        SelectedEntry = Windows[nextIndex];
        OnPropertyChanged(nameof(SelectedEntry));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
