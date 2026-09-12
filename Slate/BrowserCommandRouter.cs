using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Slate.Core;
using Windows.System;

namespace Slate;

public sealed class BrowserCommandRouter : IDisposable
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
    private readonly Func<bool> _isModalDialogOpen;
    private readonly Dictionary<BrowserCommand, Func<Task>> _actions = new();
    private bool _disposed;
    private long _lastCommandTick;
    private BrowserCommand _lastCommand;

    public BrowserCommandRouter(Window window, Func<bool> isModalDialogOpen)
    {
        _dispatcherQueue = window.DispatcherQueue;
        _isModalDialogOpen = isModalDialogOpen;
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

    public Func<BrowserCommand, bool>? CanExecuteFilter { get; set; }

    public bool CanRoute(int virtualKey, ShortcutModifiers modifiers)
    {
        if (BrowserCommandRegistry.IsTextEditingShortcut(virtualKey, modifiers)) return false;
        if (_isModalDialogOpen()) return false;
        if (!BrowserCommandRegistry.TryMatch(virtualKey, modifiers, out var command)) return false;
        if (CanExecuteFilter is not null && !CanExecuteFilter(command)) return false;
        return true;
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
        _actions.Clear();
    }
}
