namespace Slate.Core;

[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4
}

public enum BrowserCommand
{
    FocusAddress,
    NewTab,
    RestoreClosedTab,
    CloseTab,
    NextTab,
    PreviousTab,
    GoBack,
    GoForward,
    Reload,
    ToggleSidebar,
    CommandPalette,
    History,
    Downloads,
    Settings,
    ToggleSplitView,
    NewTemporaryTab,
    SelectTab1,
    SelectTab2,
    SelectTab3,
    SelectTab4,
    SelectTab5,
    SelectTab6,
    SelectTab7,
    SelectTab8,
    SelectLastTab,
    FindInPage,
    FindNext,
    FindPrevious,
    ZoomIn,
    ZoomOut,
    ZoomReset,
    ToggleFullScreen,
    Print,
    HardReload,
    OpenDevTools,
    StopLoading,
    MoveTabUp,
    MoveTabDown,
    CloseOtherTabs,
    CloseTabsBelow,
    DuplicateTab,
    BookmarkPage,
    OpenBookmarks,
    NewPrivateTab,
    OpenFile,
    SavePage,
    ViewSource
}

public sealed record ShortcutDefinition(int VirtualKey, ShortcutModifiers Modifiers, BrowserCommand Command, string DisplayLabel);

public static class BrowserCommandRegistry
{
    public const int KeyTab = 0x09;
    public const int KeyEscape = 0x1B;
    public const int KeyPageUp = 0x21;
    public const int KeyPageDown = 0x22;
    public const int KeyLeft = 0x25;
    public const int KeyRight = 0x27;
    public const int Key0 = 0x30;
    public const int Key1 = 0x31;
    public const int Key8 = 0x38;
    public const int Key9 = 0x39;
    public const int KeyB = 0x42;
    public const int KeyD = 0x44;
    public const int KeyH = 0x48;
    public const int KeyI = 0x49;
    public const int KeyJ = 0x4A;
    public const int KeyK = 0x4B;
    public const int KeyL = 0x4C;
    public const int KeyN = 0x4E;
    public const int KeyO = 0x4F;
    public const int KeyP = 0x50;
    public const int KeyR = 0x52;
    public const int KeyS = 0x53;
    public const int KeyT = 0x54;
    public const int KeyU = 0x55;
    public const int KeyW = 0x57;
    public const int KeyAdd = 0x6B;
    public const int KeySubtract = 0x6D;
    public const int KeyF3 = 0x72;
    public const int KeyF5 = 0x74;
    public const int KeyF11 = 0x7A;
    public const int KeyF12 = 0x7B;
    public const int KeyPlus = 0xBB;
    public const int KeyComma = 0xBC;
    public const int KeyMinus = 0xBD;

    // Common text-editing keys that should always pass through to web content/native textboxes
    public const int KeyA = 0x41;
    public const int KeyC = 0x43;
    public const int KeyF = 0x46;
    public const int KeyV = 0x56;
    public const int KeyX = 0x58;
    public const int KeyY = 0x59;
    public const int KeyZ = 0x5A;

    private static readonly List<ShortcutDefinition> _shortcuts =
    [
        new(KeyL, ShortcutModifiers.Control, BrowserCommand.FocusAddress, "Ctrl+L"),
        new(KeyT, ShortcutModifiers.Control, BrowserCommand.NewTab, "Ctrl+T"),
        new(KeyT, ShortcutModifiers.Control | ShortcutModifiers.Shift, BrowserCommand.RestoreClosedTab, "Ctrl+Shift+T"),
        new(KeyW, ShortcutModifiers.Control, BrowserCommand.CloseTab, "Ctrl+W"),
        new(KeyTab, ShortcutModifiers.Control, BrowserCommand.NextTab, "Ctrl+Tab"),
        new(KeyTab, ShortcutModifiers.Control | ShortcutModifiers.Shift, BrowserCommand.PreviousTab, "Ctrl+Shift+Tab"),
        new(KeyLeft, ShortcutModifiers.Alt, BrowserCommand.GoBack, "Alt+Left"),
        new(KeyRight, ShortcutModifiers.Alt, BrowserCommand.GoForward, "Alt+Right"),
        new(KeyR, ShortcutModifiers.Control, BrowserCommand.Reload, "Ctrl+R"),
        new(KeyF5, ShortcutModifiers.None, BrowserCommand.Reload, "F5"),
        new(KeyB, ShortcutModifiers.Control, BrowserCommand.ToggleSidebar, "Ctrl+B"),
        new(KeyK, ShortcutModifiers.Control, BrowserCommand.CommandPalette, "Ctrl+K"),
        new(KeyP, ShortcutModifiers.Control | ShortcutModifiers.Shift, BrowserCommand.CommandPalette, "Ctrl+Shift+P"),
        new(KeyH, ShortcutModifiers.Control, BrowserCommand.History, "Ctrl+H"),
        new(KeyJ, ShortcutModifiers.Control, BrowserCommand.Downloads, "Ctrl+J"),
        new(KeyComma, ShortcutModifiers.Control, BrowserCommand.Settings, "Ctrl+,"),
        new(KeyS, ShortcutModifiers.Control | ShortcutModifiers.Shift, BrowserCommand.ToggleSplitView, "Ctrl+Shift+S"),
        new(KeyN, ShortcutModifiers.Control | ShortcutModifiers.Shift, BrowserCommand.NewTemporaryTab, "Ctrl+Shift+N"),
        new(0x31, ShortcutModifiers.Control, BrowserCommand.SelectTab1, "Ctrl+1"),
        new(0x32, ShortcutModifiers.Control, BrowserCommand.SelectTab2, "Ctrl+2"),
        new(0x33, ShortcutModifiers.Control, BrowserCommand.SelectTab3, "Ctrl+3"),
        new(0x34, ShortcutModifiers.Control, BrowserCommand.SelectTab4, "Ctrl+4"),
        new(0x35, ShortcutModifiers.Control, BrowserCommand.SelectTab5, "Ctrl+5"),
        new(0x36, ShortcutModifiers.Control, BrowserCommand.SelectTab6, "Ctrl+6"),
        new(0x37, ShortcutModifiers.Control, BrowserCommand.SelectTab7, "Ctrl+7"),
        new(0x38, ShortcutModifiers.Control, BrowserCommand.SelectTab8, "Ctrl+8"),
        new(0x39, ShortcutModifiers.Control, BrowserCommand.SelectLastTab, "Ctrl+9"),
        new(KeyF, ShortcutModifiers.Control, BrowserCommand.FindInPage, "Ctrl+F"),
        new(KeyF3, ShortcutModifiers.None, BrowserCommand.FindNext, "F3"),
        new(KeyF3, ShortcutModifiers.Shift, BrowserCommand.FindPrevious, "Shift+F3"),
        new(KeyPlus, ShortcutModifiers.Control, BrowserCommand.ZoomIn, "Ctrl++"),
        new(KeyAdd, ShortcutModifiers.Control, BrowserCommand.ZoomIn, "Ctrl++"),
        new(KeyMinus, ShortcutModifiers.Control, BrowserCommand.ZoomOut, "Ctrl+-"),
        new(KeySubtract, ShortcutModifiers.Control, BrowserCommand.ZoomOut, "Ctrl+-"),
        new(Key0, ShortcutModifiers.Control, BrowserCommand.ZoomReset, "Ctrl+0"),
        new(KeyF11, ShortcutModifiers.None, BrowserCommand.ToggleFullScreen, "F11"),
        new(KeyP, ShortcutModifiers.Control, BrowserCommand.Print, "Ctrl+P"),
        new(KeyF5, ShortcutModifiers.Control, BrowserCommand.HardReload, "Ctrl+F5"),
        new(KeyR, ShortcutModifiers.Control | ShortcutModifiers.Shift, BrowserCommand.HardReload, "Ctrl+Shift+R"),
        new(KeyF12, ShortcutModifiers.None, BrowserCommand.OpenDevTools, "F12"),
        new(KeyI, ShortcutModifiers.Control | ShortcutModifiers.Shift, BrowserCommand.OpenDevTools, "Ctrl+Shift+I"),
        new(KeyEscape, ShortcutModifiers.None, BrowserCommand.StopLoading, "Esc"),
        new(KeyPageUp, ShortcutModifiers.Control | ShortcutModifiers.Shift, BrowserCommand.MoveTabUp, "Ctrl+Shift+PageUp"),
        new(KeyPageDown, ShortcutModifiers.Control | ShortcutModifiers.Shift, BrowserCommand.MoveTabDown, "Ctrl+Shift+PageDown"),
        new(KeyD, ShortcutModifiers.Control | ShortcutModifiers.Shift, BrowserCommand.DuplicateTab, "Ctrl+Shift+D"),
        new(KeyD, ShortcutModifiers.Control, BrowserCommand.BookmarkPage, "Ctrl+D"),
        new(KeyO, ShortcutModifiers.Control | ShortcutModifiers.Shift, BrowserCommand.OpenBookmarks, "Ctrl+Shift+O"),
        new(KeyN, ShortcutModifiers.Control | ShortcutModifiers.Alt, BrowserCommand.NewPrivateTab, "Ctrl+Alt+N"),
        new(KeyO, ShortcutModifiers.Control, BrowserCommand.OpenFile, "Ctrl+O"),
        new(KeyS, ShortcutModifiers.Control, BrowserCommand.SavePage, "Ctrl+S"),
        new(KeyU, ShortcutModifiers.Control, BrowserCommand.ViewSource, "Ctrl+U")
    ];

    public static IReadOnlyList<ShortcutDefinition> AllShortcuts => _shortcuts;

    public static bool TryMatch(int virtualKey, ShortcutModifiers modifiers, out BrowserCommand command)
    {
        // Normalize modifiers to ignore unexpected flags
        var cleanMods = modifiers & (ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Shift);
        foreach (var def in _shortcuts)
        {
            if (def.VirtualKey == virtualKey && def.Modifiers == cleanMods)
            {
                command = def.Command;
                return true;
            }
        }
        command = default;
        return false;
    }

    public static bool IsBrowserShortcut(int virtualKey, ShortcutModifiers modifiers)
        => TryMatch(virtualKey, modifiers, out _);

    public static bool IsTextEditingShortcut(int virtualKey, ShortcutModifiers modifiers)
    {
        var cleanMods = modifiers & (ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Shift);
        if (cleanMods == ShortcutModifiers.Control)
        {
            return virtualKey is KeyA or KeyC or KeyV or KeyX or KeyY or KeyZ;
        }
        if (cleanMods == (ShortcutModifiers.Control | ShortcutModifiers.Shift))
        {
            return virtualKey == KeyZ; // Redo (Ctrl+Shift+Z)
        }
        return false;
    }

    public static string GetShortcutLabel(BrowserCommand command)
    {
        var match = _shortcuts.FirstOrDefault(s => s.Command == command);
        return match?.DisplayLabel ?? string.Empty;
    }
}
