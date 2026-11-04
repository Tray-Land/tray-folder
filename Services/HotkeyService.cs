using System.Diagnostics;
using TrayFolder.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace TrayFolder.Services;

/// <summary>
/// Registers the global hotkey with Windows. WM_HOTKEY goes to a message-only window that this
/// class owns (the XAML flyout may not exist, and the app has no other window), so the hotkey
/// costs nothing while idle: no polling, no hook.
/// </summary>
internal sealed unsafe class HotkeyService : IHotkeyService, IDisposable
{
    private const string WindowClassName = "TrayFolderHotkeyWindow";
    private const int HotkeyId = 1;
    private const uint WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;

    private readonly Action _pressed;
    private readonly WNDPROC _windowProc;
    private HWND _hwnd;
    private bool _registered;

    public HotkeyService(Action pressed)
    {
        _pressed = pressed;
        _windowProc = WindowProc;
    }

    public HotkeyGesture? Gesture { get; private set; }

    public HotkeyResult Status { get; private set; } = HotkeyResult.Disabled;

    /// <summary>Registers the saved hotkey at launch.</summary>
    public void Start()
    {
        Gesture = SettingsService.Hotkey;
        Status = Apply(Gesture);
    }

    public HotkeyResult Set(HotkeyGesture? gesture)
    {
        Gesture = gesture;
        Status = Apply(gesture);
        try
        {
            SettingsService.Hotkey = gesture;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Save hotkey failed: {ex}");
        }

        return Status;
    }

    public void Dispose()
    {
        Unregister();
        if (!_hwnd.IsNull)
        {
            PInvoke.DestroyWindow(_hwnd);
            _hwnd = HWND.Null;
            using FreeLibrarySafeHandle module = PInvoke.GetModuleHandle((string?)null);
            PInvoke.UnregisterClass(WindowClassName, module);
        }
    }

    private HotkeyResult Apply(HotkeyGesture? gesture)
    {
        Unregister();
        if (gesture is not { IsValid: true } value || !EnsureWindow())
        {
            return HotkeyResult.Disabled;
        }

        // MOD_NOREPEAT: holding the keys down toggles the flyout once, not repeatedly.
        _registered = PInvoke.RegisterHotKey(_hwnd, HotkeyId,
            (HOT_KEY_MODIFIERS)((uint)value.Modifiers | ModNoRepeat), value.VirtualKey);
        return _registered ? HotkeyResult.Registered : HotkeyResult.InUse;
    }

    private void Unregister()
    {
        if (_registered)
        {
            PInvoke.UnregisterHotKey(_hwnd, HotkeyId);
            _registered = false;
        }
    }

    private bool EnsureWindow()
    {
        if (!_hwnd.IsNull)
        {
            return true;
        }

        using FreeLibrarySafeHandle module = PInvoke.GetModuleHandle((string?)null);
        fixed (char* className = WindowClassName)
        {
            WNDCLASSW windowClass = new()
            {
                lpfnWndProc = _windowProc,
                hInstance = (HINSTANCE)module.DangerousGetHandle(),
                lpszClassName = className,
            };
            PInvoke.RegisterClass(windowClass);
        }

        // HWND_MESSAGE parent: invisible, not enumerated, receives messages only.
        _hwnd = PInvoke.CreateWindowEx(0, WindowClassName, WindowClassName, 0, 0, 0, 0, 0,
            new HWND(-3), null, module, null);
        return !_hwnd.IsNull;
    }

    private LRESULT WindowProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == WmHotkey && wParam.Value == HotkeyId)
        {
            _pressed();
            return default;
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }
}
