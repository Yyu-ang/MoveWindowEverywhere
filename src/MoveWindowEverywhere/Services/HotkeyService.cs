using System.Runtime.InteropServices;
using MoveWindowEverywhere.Models;
using MoveWindowEverywhere.Native;

namespace MoveWindowEverywhere.Services;

/// <summary>全局快捷键注册结果。</summary>
public sealed record HotkeyRegistrationResult(bool Success, string? ErrorMessage, bool AlreadyRegistered)
{
    public static HotkeyRegistrationResult Ok => new(true, null, false);

    public static HotkeyRegistrationResult Fail(string message, bool alreadyRegistered) => new(false, message, alreadyRegistered);
}

/// <summary>
/// 自动换用备用组合之后的结果。
/// <see cref="Effective"/> 是最终真正生效的组合；<see cref="UsedFallback"/> 为真时
/// 说明它与用户原本设定的组合不同，必须告知用户。
/// </summary>
public sealed record HotkeyFallbackOutcome(
    bool Success,
    HotkeySettings Effective,
    bool UsedFallback,
    string? Message)
{
    public static HotkeyFallbackOutcome Failure(HotkeySettings desired, string message) =>
        new(false, desired, false, message);
}

/// <summary>
/// 全局快捷键服务：基于 RegisterHotKey 与隐藏消息窗口的 WM_HOTKEY。
/// 同一个隐藏窗口可挂载多个 HotkeyService 实例，每个实例用独立 hotkey id 过滤自己的消息。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    public const int HotkeyId = 0x4D57; // 'M' << 8 | 'W'，保留原 id 兼容既有测试
    public const int RestoreHotkeyId = 0x4D58;

    private readonly HiddenMessageWindow _window;
    private readonly AppLogger _logger;
    private readonly int _hotkeyId;
    private HotkeySettings _current;
    private bool _registered;
    private bool _disposed;

    public event EventHandler? HotKeyPressed;

    public HotkeyService(HiddenMessageWindow window, HotkeySettings settings, AppLogger logger, int hotkeyId = HotkeyId)
    {
        _window = window;
        _logger = logger;
        _hotkeyId = hotkeyId;
        _current = settings.Copy();
        _window.HotKeyReceived += OnHotKeyReceived;
    }

    public HotkeySettings Current => _current;

    public bool IsRegistered => _registered;

    /// <summary>注册新快捷键。失败时原有快捷键保持不变。</summary>
    public HotkeyRegistrationResult TryRegister(HotkeySettings? settings) =>
        TryRegisterCore(settings, restoreOnFailure: true);

    /// <summary>
    /// 注册快捷键，被占用时按 <see cref="HotkeyFallbackPlanner"/> 的顺序自动换用备用组合。
    /// </summary>
    public HotkeyFallbackOutcome RegisterWithFallback(
        HotkeySettings? desired,
        bool allowFallback = true,
        int maxCandidates = HotkeyFallbackPlanner.DefaultMaxCandidates)
    {
        if (desired is null || !desired.IsValid)
        {
            return HotkeyFallbackOutcome.Failure(
                desired ?? HotkeySettings.CreateDefault(),
                "快捷键无效：至少需要包含一个修饰键和一个按键。");
        }

        HotkeyRegistrationResult primary = TryRegisterCore(desired, restoreOnFailure: true);
        if (primary.Success)
        {
            return new HotkeyFallbackOutcome(true, _current, false, null);
        }

        if (!allowFallback)
        {
            return HotkeyFallbackOutcome.Failure(desired, primary.ErrorMessage ?? "注册快捷键失败。");
        }

        IReadOnlyList<HotkeySettings> candidates = HotkeyFallbackPlanner.Plan(desired, maxCandidates);
        int attempts = 1;

        foreach (HotkeySettings candidate in candidates)
        {
            if (candidate.VirtualKey == desired.VirtualKey
                && candidate.RegisterModifiers == desired.RegisterModifiers)
            {
                continue;
            }

            attempts++;
            HotkeyRegistrationResult result = TryRegisterCore(candidate, restoreOnFailure: false);
            if (!result.Success)
            {
                continue;
            }

            _logger.Info($"快捷键 {desired.DisplayText} 不可用，已自动改用 {_current.DisplayText}（共尝试 {attempts} 个组合）");
            return new HotkeyFallbackOutcome(
                true,
                _current,
                UsedFallback: true,
                $"快捷键 {desired.DisplayText} 已被其他程序占用，已自动改用 {_current.DisplayText}。可在设置中自行修改。");
        }

        _logger.Error($"快捷键 {desired.DisplayText} 与 {attempts - 1} 个备用组合均注册失败");
        return HotkeyFallbackOutcome.Failure(
            desired,
            $"{primary.ErrorMessage} 备用的 {attempts - 1} 个组合也都被占用，请在设置中手动指定一个组合。");
    }

    public HotkeyRegistrationResult TryReregister() => TryRegister(_current);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.HotKeyReceived -= OnHotKeyReceived;
        Unregister();
    }

    private HotkeyRegistrationResult TryRegisterCore(HotkeySettings? settings, bool restoreOnFailure)
    {
        if (settings is null || !settings.IsValid)
        {
            return HotkeyRegistrationResult.Fail("快捷键无效：至少需要包含一个修饰键和一个按键。", false);
        }

        Unregister();
        HotkeySettings previous = _current;

        if (!Win32.RegisterHotKey(_window.Handle, _hotkeyId, settings.RegisterModifiers, settings.VirtualKey))
        {
            int error = Marshal.GetLastWin32Error();
            _logger.Error($"RegisterHotKey 失败（{settings.DisplayText}），Win32 错误码 {error}");

            bool alreadyRegistered = error == Win32.ERROR_HOTKEY_ALREADY_REGISTERED;
            string message = alreadyRegistered
                ? $"快捷键 {settings.DisplayText} 已被其他程序占用，请换一个组合。"
                : $"注册快捷键 {settings.DisplayText} 失败（Win32 错误码 {error}），请换一个组合。";

            if (restoreOnFailure && previous.IsValid && !ReferenceEquals(previous, settings))
            {
                TryRestore(previous);
            }
            else
            {
                _registered = false;
            }

            return HotkeyRegistrationResult.Fail(message, alreadyRegistered);
        }

        _current = settings.Copy();
        _registered = true;
        _logger.Info($"已注册全局快捷键 {_current.DisplayText}（ID 0x{_hotkeyId:X}）");
        return HotkeyRegistrationResult.Ok;
    }

    private void TryRestore(HotkeySettings previous)
    {
        if (Win32.RegisterHotKey(_window.Handle, _hotkeyId, previous.RegisterModifiers, previous.VirtualKey))
        {
            _current = previous.Copy();
            _registered = true;
            _logger.Info($"已恢复原有快捷键 {_current.DisplayText}");
        }
        else
        {
            _registered = false;
            _logger.Error($"恢复原有快捷键失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
        }
    }

    private void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        if (!Win32.UnregisterHotKey(_window.Handle, _hotkeyId))
        {
            _logger.Warn($"UnregisterHotKey 失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
        }

        _registered = false;
    }

    private void OnHotKeyReceived(object? sender, int hotkeyId)
    {
        if (hotkeyId == _hotkeyId)
        {
            HotKeyPressed?.Invoke(this, EventArgs.Empty);
        }
    }
}
