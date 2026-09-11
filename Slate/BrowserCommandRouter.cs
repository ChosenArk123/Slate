using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Slate.Core;
using Windows.System;

namespace Slate;

public sealed class BrowserCommandRouter : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    private const uint GA_ROOT = 2;
    private const uint GA_ROOTOWNER = 3;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly IntPtr _windowHandle;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
    private readonly Func<bool> _isModalDialogOpen;
    private readonly Dictionary<BrowserCommand, Func<Task>> _actions = new();
    private readonly LowLevelKeyboardProc _hookProc;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _disposed;
    private long _lastCommandTick;
    private BrowserCommand _lastCommand;
    private int _lastSuppressedVk;

    public BrowserCommandRouter(Window window, Func<bool> isModalDialogOpen)
    {
        IntPtr hwnd;
        try
        {
            hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        }
        catch
        {
            hwnd = (IntPtr)(long)window.AppWindow.Id.Value;
        }

        if (hwnd == IntPtr.Zero)
        {
            hwnd = (IntPtr)(long)window.AppWindow.Id.Value;
        }

        _windowHandle = hwnd;
        _dispatcherQueue = window.DispatcherQueue;
        _isModalDialogOpen = isModalDialogOpen;

        _hookProc = HookCallback;
        InstallHook();
    }

    private void InstallHook()
    {
        try
        {
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            var modHandle = curModule != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, modHandle, 0);
        }
        catch
        {
            // Low-level hook installation failed; fall back to XAML accelerators
        }
    }

    public void RegisterAction(BrowserCommand command, Func<Task> action)
    {
        _actions[command] = action;
    }

    public void AttachWebView2(WebView2 view)
    {
        if (view is null) return;
        view.PreviewKeyDown -= OnWebViewPreviewKeyDown;
        view.PreviewKeyDown += OnWebViewPreviewKeyDown;
    }

    public void DetachWebView2(WebView2 view)
    {
        if (view is null) return;
        view.PreviewKeyDown -= OnWebViewPreviewKeyDown;
    }

    private void OnWebViewPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_isModalDialogOpen()) return;

        var mods = ShortcutModifiers.None;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        var menu = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu);
        if (ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) mods |= ShortcutModifiers.Control;
        if (shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) mods |= ShortcutModifiers.Shift;
        if (menu.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) mods |= ShortcutModifiers.Alt;

        int vk = (int)e.Key;
        if (CanRoute(vk, mods) && BrowserCommandRegistry.TryMatch(vk, mods, out var command))
        {
            e.Handled = true;
            _ = RouteCommandAsync(command);
        }
    }

    public bool IsSlateForeground()
    {
        if (_windowHandle == IntPtr.Zero) return false;
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (fg == _windowHandle) return true;
        if (GetAncestor(fg, GA_ROOT) == _windowHandle) return true;
        if (GetAncestor(fg, GA_ROOTOWNER) == _windowHandle) return true;
        return false;
    }

    private static ShortcutModifiers GetCurrentModifiers()
    {
        var mods = ShortcutModifiers.None;
        if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) mods |= ShortcutModifiers.Control;
        if ((GetAsyncKeyState(VK_MENU) & 0x8000) != 0) mods |= ShortcutModifiers.Alt;
        if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) mods |= ShortcutModifiers.Shift;
        return mods;
    }

    public Func<BrowserCommand, bool>? CanExecuteFilter { get; set; }

    public bool CanRoute(int virtualKey, ShortcutModifiers modifiers)
    {
        if (BrowserCommandRegistry.IsTextEditingShortcut(virtualKey, modifiers)) return false;
        if (_isModalDialogOpen()) return false;
        if (!BrowserCommandRegistry.TryMatch(virtualKey, modifiers, out var command)) return false;
        if (CanExecuteFilter is not null && !CanExecuteFilter(command)) return false;
        return true;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                if (IsSlateForeground())
                {
                    // Ignore if Windows key is down (e.g. Win+L, Win+R, Win+1..9)
                    if ((GetAsyncKeyState(VK_LWIN) & 0x8000) == 0 && (GetAsyncKeyState(VK_RWIN) & 0x8000) == 0)
                    {
                        var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                        int vk = (int)kbd.vkCode;
                        var mods = GetCurrentModifiers();

                        if (TryHandleKeyDown(vk, mods))
                        {
                            _lastSuppressedVk = vk;
                            return (IntPtr)1; // Suppress from Chromium / WebView2 / Windows
                        }
                    }
                }
            }
            else if (msg is WM_KEYUP or WM_SYSKEYUP)
            {
                var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vk = (int)kbd.vkCode;
                if (_lastSuppressedVk != 0 && vk == _lastSuppressedVk)
                {
                    _lastSuppressedVk = 0;
                    return (IntPtr)1; // Suppress matching keyup
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private bool TryHandleKeyDown(int vk, ShortcutModifiers mods)
    {
        if (!CanRoute(vk, mods)) return false;

        if (BrowserCommandRegistry.TryMatch(vk, mods, out var command))
        {
            long now = Environment.TickCount64;
            if (now - _lastCommandTick < 80 && _lastCommand == command)
            {
                return true; // Suppress duplicate key event within debounce window
            }

            _lastCommandTick = now;
            _lastCommand = command;

            _dispatcherQueue.TryEnqueue(async () =>
            {
                await ExecuteCommandAsync(command);
            });

            return true;
        }

        return false;
    }

    public async Task<bool> RouteCommandAsync(BrowserCommand command)
    {
        if (_isModalDialogOpen()) return false;

        long now = Environment.TickCount64;
        if (now - _lastCommandTick < 80 && _lastCommand == command)
        {
            return false;
        }

        _lastCommandTick = now;
        _lastCommand = command;
        return await ExecuteCommandAsync(command);
    }

    public async Task<bool> SimulateKeyDownAsync(int virtualKey, ShortcutModifiers modifiers)
    {
        if (CanRoute(virtualKey, modifiers) && BrowserCommandRegistry.TryMatch(virtualKey, modifiers, out var command))
        {
            return await RouteCommandAsync(command);
        }
        return false;
    }

    private async Task<bool> ExecuteCommandAsync(BrowserCommand command)
    {
        if (_actions.TryGetValue(command, out var action))
        {
            try
            {
                await action();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error executing command {command}: {ex.Message}");
            }
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        _actions.Clear();
    }
}
