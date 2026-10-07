using System.Runtime.InteropServices;
using System.Windows;
using MoveWindowEverywhere.Models;
using MoveWindowEverywhere.Native;

namespace MoveWindowEverywhere.Services;

/// <summary>窗口移动结果。</summary>
public sealed record WindowMoveResult(bool Success, string Message, bool RequiresElevation)
{
    public static WindowMoveResult Ok(string message) => new(true, message, false);

    public static WindowMoveResult Fail(string message, bool requiresElevation = false) => new(false, message, requiresElevation);
}

/// <summary>
/// 把窗口移动到目标显示器，并为每个窗口维护当前进程生命周期内的多级恢复历史。
/// 每次成功移动前记录完整 <c>WINDOWPLACEMENT</c>；恢复时同时恢复位置、尺寸与窗口状态。
/// </summary>
public sealed class WindowMover
{
    private readonly AppLogger _logger;
    private readonly Dictionary<IntPtr, Stack<WindowRestorePoint>> _restoreHistory = new();

    private readonly record struct WindowRestorePoint(uint ProcessId, string ClassName, WINDOWPLACEMENT Placement);

    public WindowMover(AppLogger logger)
    {
        _logger = logger;
    }

    /// <summary>把窗口移动到目标显示器，并在成功后压入一条可恢复历史。</summary>
    public WindowMoveResult MoveToMonitor(IntPtr handle, MonitorInfo? targetMonitor)
    {
        if (handle == IntPtr.Zero)
        {
            return WindowMoveResult.Fail("目标窗口句柄无效。");
        }

        if (!Win32.IsWindow(handle))
        {
            ForgetRestoreHistory(handle);
            return WindowMoveResult.Fail("目标窗口已关闭或句柄失效，请重新选择。");
        }

        if (targetMonitor is null)
        {
            return WindowMoveResult.Fail("未捕获到目标显示器，请重新按一次快捷键。");
        }

        if (!TryCapturePlacement(handle, out WindowRestorePoint originalPlacement))
        {
            return WindowMoveResult.Fail("无法记录窗口移动前的位置，因此本次未移动窗口。请重试。");
        }

        bool wasMinimized = Win32.IsIconic(handle);
        bool wasMaximized = Win32.IsZoomed(handle);

        if (wasMinimized || wasMaximized)
        {
            Win32.ShowWindow(handle, Win32.SW_RESTORE);
            WaitUntilRestored(handle);
        }

        if (!Win32.TryGetVisibleWindowBounds(handle, out RECT current))
        {
            RestoreBestEffort(handle, originalPlacement);
            return WindowMoveResult.Fail("无法读取窗口位置，窗口可能已关闭。");
        }

        Int32Rect desired = WindowPlacementCalculator.ComputeCenteredRect(
            targetMonitor.WorkRect,
            current.Width,
            current.Height);

        if (!SetPosition(handle, desired))
        {
            string message = BuildSetWindowPosError(handle);
            RestoreBestEffort(handle, originalPlacement);
            return WindowMoveResult.Fail(message);
        }

        // 某些程序会拒绝或修正我们设置的位置，移动后再校验一次并夹紧到工作区
        if (Win32.TryGetVisibleWindowBounds(handle, out RECT after))
        {
            Int32Rect clamped = WindowPlacementCalculator.ClampToWorkArea(
                new Int32Rect(after.Left, after.Top, after.Width, after.Height),
                targetMonitor.WorkRect);

            if (clamped.X != after.Left || clamped.Y != after.Top || clamped.Width != after.Width || clamped.Height != after.Height)
            {
                _logger.Warn($"窗口未按预期位置落位，执行一次夹紧：({after.Left},{after.Top},{after.Width}×{after.Height}) -> ({clamped.X},{clamped.Y},{clamped.Width}×{clamped.Height})");
                if (!SetPosition(handle, clamped))
                {
                    string message = BuildSetWindowPosError(handle);
                    RestoreBestEffort(handle, originalPlacement);
                    return WindowMoveResult.Fail(message);
                }
            }
        }

        if (wasMaximized)
        {
            Win32.ShowWindow(handle, Win32.SW_MAXIMIZE);
        }

        PushRestorePoint(handle, originalPlacement);

        // 最小化的窗口按移动策略恢复为普通可见窗口；恢复历史仍保存它移动前的最小化状态
        bool activated = Activate(handle);
        if (!activated)
        {
            _logger.Warn($"窗口 0x{handle.ToInt64():X} 移动成功，但未能设置为前台窗口");
        }

        return WindowMoveResult.Ok($"已移动到 {targetMonitor.ShortDescription}；可按 Alt + X 恢复");
    }

    /// <summary>恢复指定窗口最近一次移动前的完整位置、尺寸与窗口状态。</summary>
    public WindowMoveResult RestorePrevious(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return WindowMoveResult.Fail("目标窗口句柄无效。");
        }

        if (!TryPeekValidRestorePoint(handle, out WindowRestorePoint restorePoint))
        {
            return WindowMoveResult.Fail("该窗口没有可恢复的历史记录。");
        }

        WINDOWPLACEMENT placement = restorePoint.Placement;
        placement.Length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>();

        if (!Win32.SetWindowPlacement(handle, ref placement))
        {
            int error = Marshal.GetLastWin32Error();
            _logger.Error($"SetWindowPlacement 恢复失败，句柄 0x{handle.ToInt64():X}，Win32 错误码 {error}");

            bool requiresElevation = error == Win32.ERROR_ACCESS_DENIED;
            string message = requiresElevation
                ? "恢复窗口失败，目标窗口权限高于本程序，请尝试以管理员身份运行本工具。"
                : $"恢复窗口失败（Win32 错误码 {error}）。";

            // 恢复失败时不消费历史，允许用户再次尝试。
            return WindowMoveResult.Fail(message, requiresElevation);
        }

        PopRestorePoint(handle);

        // 如果原状态就是最小化，则严格恢复最小化，不再 Activate 把它拉起。
        if (!Win32.IsIconic(handle) && !Activate(handle))
        {
            _logger.Warn($"窗口 0x{handle.ToInt64():X} 已恢复，但未能设置为前台窗口");
        }

        int remaining = GetRestoreCount(handle);
        _logger.Info($"窗口 0x{handle.ToInt64():X} 已恢复到上一次移动前状态，剩余恢复层数 {remaining}");
        return WindowMoveResult.Ok(remaining > 0
            ? $"已恢复到上一次移动前的位置和尺寸；还可继续恢复 {remaining} 次。"
            : "已恢复到移动前的位置和尺寸。");
    }

    /// <summary>
    /// 返回当前仍然有效且有历史的窗口句柄。无效 HWND、PID/窗口类不匹配的记录会被自动清理。
    /// </summary>
    public IReadOnlyList<IntPtr> GetRestorableHandles()
    {
        foreach (IntPtr handle in _restoreHistory.Keys.ToArray())
        {
            _ = TryPeekValidRestorePoint(handle, out _);
        }

        return _restoreHistory
            .Where(static pair => pair.Value.Count > 0)
            .Select(static pair => pair.Key)
            .ToArray();
    }

    /// <summary>指定窗口还能恢复多少步。</summary>
    public int GetRestoreCount(IntPtr handle) =>
        TryPeekValidRestorePoint(handle, out _) && _restoreHistory.TryGetValue(handle, out Stack<WindowRestorePoint>? stack)
            ? stack.Count
            : 0;

    /// <summary>主动丢弃某个窗口的全部恢复历史。</summary>
    public void ForgetRestoreHistory(IntPtr handle) => _restoreHistory.Remove(handle);

    private bool TryCapturePlacement(IntPtr handle, out WindowRestorePoint restorePoint)
    {
        _ = Win32.GetWindowThreadProcessId(handle, out uint processId);
        string className = Win32.GetWindowClassName(handle);

        var placement = new WINDOWPLACEMENT
        {
            Length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>(),
        };

        if (processId == 0 || !Win32.GetWindowPlacement(handle, ref placement))
        {
            int error = Marshal.GetLastWin32Error();
            _logger.Error($"GetWindowPlacement 失败，句柄 0x{handle.ToInt64():X}，Win32 错误码 {error}");
            restorePoint = default;
            return false;
        }

        restorePoint = new WindowRestorePoint(processId, className, placement);
        return true;
    }

    private bool TryPeekValidRestorePoint(IntPtr handle, out WindowRestorePoint restorePoint)
    {
        restorePoint = default;

        if (!_restoreHistory.TryGetValue(handle, out Stack<WindowRestorePoint>? stack) || stack.Count == 0)
        {
            _restoreHistory.Remove(handle);
            return false;
        }

        if (!Win32.IsWindow(handle))
        {
            _restoreHistory.Remove(handle);
            return false;
        }

        WindowRestorePoint candidate = stack.Peek();
        _ = Win32.GetWindowThreadProcessId(handle, out uint processId);
        string className = Win32.GetWindowClassName(handle);

        // HWND 会被系统复用；PID + 窗口类至少要同时一致才允许恢复。
        if (processId == 0
            || processId != candidate.ProcessId
            || !string.Equals(className, candidate.ClassName, StringComparison.Ordinal))
        {
            _logger.Warn($"窗口 0x{handle.ToInt64():X} 的身份已变化，丢弃旧恢复历史");
            _restoreHistory.Remove(handle);
            return false;
        }

        restorePoint = candidate;
        return true;
    }

    private void PushRestorePoint(IntPtr handle, WindowRestorePoint restorePoint)
    {
        if (!_restoreHistory.TryGetValue(handle, out Stack<WindowRestorePoint>? stack))
        {
            stack = new Stack<WindowRestorePoint>();
            _restoreHistory[handle] = stack;
        }

        stack.Push(restorePoint);
    }

    private void PopRestorePoint(IntPtr handle)
    {
        if (!_restoreHistory.TryGetValue(handle, out Stack<WindowRestorePoint>? stack) || stack.Count == 0)
        {
            return;
        }

        stack.Pop();
        if (stack.Count == 0)
        {
            _restoreHistory.Remove(handle);
        }
    }

    private void RestoreBestEffort(IntPtr handle, WindowRestorePoint restorePoint)
    {
        WINDOWPLACEMENT placement = restorePoint.Placement;
        placement.Length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>();

        if (!Win32.SetWindowPlacement(handle, ref placement))
        {
            _logger.Warn($"移动失败后的自动回滚也失败，句柄 0x{handle.ToInt64():X}，Win32 错误码 {Marshal.GetLastWin32Error()}");
        }
    }

    private bool SetPosition(IntPtr handle, Int32Rect rect)
    {
        bool ok = Win32.SetWindowPos(
            handle,
            IntPtr.Zero,
            rect.X,
            rect.Y,
            rect.Width,
            rect.Height,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);

        if (!ok)
        {
            int error = Marshal.GetLastWin32Error();
            _logger.Error($"SetWindowPos 失败，句柄 0x{handle.ToInt64():X}，Win32 错误码 {error}");
            return false;
        }

        return true;
    }

    private string BuildSetWindowPosError(IntPtr handle)
    {
        int error = Marshal.GetLastWin32Error();
        if (error == Win32.ERROR_ACCESS_DENIED || error == Win32.ERROR_INVALID_WINDOW_HANDLE)
        {
            return "目标窗口权限高于本程序或句柄已失效，请尝试以管理员身份运行本工具。";
        }

        return $"移动窗口失败（Win32 错误码 {error}）。";
    }

    private static void WaitUntilRestored(IntPtr handle)
    {
        for (int i = 0; i < 20; i++)
        {
            if (!Win32.IsIconic(handle) && !Win32.IsZoomed(handle))
            {
                return;
            }

            Thread.Sleep(10);
        }

        // 少数程序的恢复是异步的，超时后继续尝试移动，后续校验会再夹紧一次
    }

    /// <summary>把窗口设置为前台窗口。失败时尝试线程附加方式再次激活。</summary>
    public bool Activate(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !Win32.IsWindow(handle))
        {
            return false;
        }

        // 允许其他进程抢占前台，降低 SetForegroundWindow 被系统拒绝的概率
        Win32.AllowSetForegroundWindow(uint.MaxValue);

        if (Win32.SetForegroundWindow(handle))
        {
            return true;
        }

        _logger.Warn($"SetForegroundWindow 失败，句柄 0x{handle.ToInt64():X}，Win32 错误码 {Marshal.GetLastWin32Error()}，尝试线程附加方式激活");

        uint targetThread = Win32.GetWindowThreadProcessId(handle, out _);
        uint currentThread = Win32.GetCurrentThreadId();
        bool attached = false;
        try
        {
            if (targetThread != 0 && targetThread != currentThread)
            {
                attached = Win32.AttachThreadInput(currentThread, targetThread, true);
            }

            Win32.BringWindowToTop(handle);
            return Win32.SetForegroundWindow(handle);
        }
        finally
        {
            if (attached)
            {
                Win32.AttachThreadInput(currentThread, targetThread, false);
            }
        }
    }
}
