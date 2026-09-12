using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Slate.Core;
using Slate.Services;
using Windows.System;

namespace Slate;

public sealed partial class MainWindow
{
    private sealed record PaletteItem(string Title, string Detail, string Glyph, Func<Task> Run);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private ContentDialog Dialog(string title, object content, string close = "Done")
    {
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot, RequestedTheme = _root.RequestedTheme,
            Title = title, Content = content, CloseButtonText = close,
            DefaultButton = ContentDialogButton.Close
        };
        dialog.Opened += (_, _) =>
        {
            foreach (var button in Descendants(dialog).OfType<Button>())
                if (!string.IsNullOrEmpty(button.Name) && string.IsNullOrEmpty(AutomationProperties.GetAutomationId(button)))
                    AutomationProperties.SetAutomationId(button, button.Name);
        };
        return dialog;
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        await _dialogs.WaitAsync();
        try { _activeDialog = dialog; return _closing ? ContentDialogResult.None : await dialog.ShowAsync(); }
        finally { _activeDialog = null; _dialogs.Release(); }
    }

    private async Task ShowPaletteAsync(string query = "")
    {
        // Ignore repeated invocations while the palette (or another modal) is open.
        if (_activeDialog is not null) return;
        var input = new TextBox
        {
            PlaceholderText = "Search tabs, history, or the web…",
            Text = query,
            FontSize = 14,
            Padding = new(10, 6, 10, 6),
            BorderThickness = new(1),
            BorderBrush = _theme.DividerBrush,
            Background = _theme.SurfaceInteractiveBrush,
            CornerRadius = new(SlateTheme.RadiusSmall)
        };
        input.Resources["TextControlBorderBrushFocused"] = _theme.FocusRingBrush;
        input.Resources["TextControlBorderBrushPointerOver"] = _theme.BorderStrongBrush;
        input.Resources["TextControlBackgroundFocused"] = _theme.SurfaceInteractiveBrush;
        input.Resources["TextControlBackgroundPointerOver"] = _theme.SurfaceInteractiveHoverBrush;
        input.GotFocus += (_, _) => input.BorderBrush = _theme.FocusRingBrush;
        input.LostFocus += (_, _) => input.BorderBrush = _theme.DividerBrush;
        AutomationProperties.SetName(input, "Command search");
        var list = new ListView { Height = 340, SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true };
        list.Resources["ListViewItemSelectionIndicatorNormalBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        list.Resources["ListViewItemSelectionIndicatorPointerOverBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        list.Resources["ListViewItemSelectionIndicatorPressedBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        list.Resources["ListViewItemSelectionIndicatorBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        list.Resources["ListViewItemBackgroundSelected"] = _theme.SurfaceSelectedBrush;
        list.Resources["ListViewItemBackgroundSelectedPointerOver"] = _theme.SurfaceSelectedBrush;
        list.Resources["ListViewItemBackgroundSelectedPressed"] = _theme.SurfacePressedBrush;
        list.Resources["ListViewItemBackgroundPointerOver"] = _theme.SurfaceInteractiveHoverBrush;
        AutomationProperties.SetName(list, "Command results");
        var panel = new StackPanel { MaxWidth = 540, Spacing = 8, Children = { input, list,
            new TextBlock { Text = "↑ ↓  Navigate     Enter  Open     Esc  Dismiss", FontSize = 11, Opacity = .5 } } };
        var dialog = Dialog("Go anywhere", panel, "");
        dialog.CloseButtonText = "";
        dialog.PrimaryButtonText = "";
        dialog.DefaultButton = ContentDialogButton.None;
        dialog.Resources["ContentDialogTitleMargin"] = new Thickness(0, 0, 0, 4);
        PaletteItem? chosen = null;
        void Choose() { if (ResolvePaletteItem(list.SelectedItem) is { } item) { chosen = item; dialog.Hide(); } }
        dialog.Tag = (Action)Choose;
        void Search()
        {
            list.Items.Clear();
            foreach (var result in GetPaletteItems(input.Text).Take(24))
            {
                var row = new Grid { Tag = result, ColumnSpacing = 14, Padding = new(2, 5, 2, 5) };
                row.ColumnDefinitions.Add(new() { Width = new(20) }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                row.Children.Add(Icon(result.Glyph, 16));
                var text = new StackPanel { Spacing = 3, Children = {
                    new TextBlock { Text = result.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = result.Detail, FontSize = 11, Opacity = .5, TextTrimming = TextTrimming.CharacterEllipsis } } };
                Grid.SetColumn(text, 1); row.Children.Add(text);
                var item = new ListViewItem { Content = row, Tag = result, HorizontalContentAlignment = HorizontalAlignment.Stretch, CornerRadius = new(SlateTheme.RadiusSmall) };
                AutomationProperties.SetName(item, result.Title + ", " + result.Detail); list.Items.Add(item);
            }
            if (list.Items.Count > 0) { list.SelectedIndex = 0; list.SelectedItem = list.Items[0]; }
        }
        string lastQuery = input.Text;
        void OnQueryChanged()
        {
            if (input.Text == lastQuery) return;
            lastQuery = input.Text;
            Search();
        }
        input.RegisterPropertyChangedCallback(TextBox.TextProperty, (_, _) => OnQueryChanged());
        input.TextChanged += (_, _) => OnQueryChanged();
        // TextBox/ListView may handle arrow keys themselves; observe the routed event too.
        dialog.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key == VirtualKey.Enter) { e.Handled = true; Choose(); }
            else if (e.Key == VirtualKey.Escape) { e.Handled = true; dialog.Hide(); }
            else if (e.Key is VirtualKey.Down or VirtualKey.Up && input.FocusState != FocusState.Unfocused && list.Items.Count > 0)
            {
                e.Handled = true;
                int current = list.SelectedIndex >= 0 ? list.SelectedIndex : 0;
                list.SelectedIndex = Math.Clamp(current + (e.Key == VirtualKey.Down ? 1 : -1), 0, list.Items.Count - 1);
                if (list.SelectedItem is not null) list.ScrollIntoView(list.SelectedItem);
            }
        }), true);
        list.ItemClick += (_, e) => { if (ResolvePaletteItem(e.ClickedItem) is { } item) { chosen = item; dialog.Hide(); } };
        dialog.Opened += (_, _) => { if (list.SelectedIndex < 0 && list.Items.Count > 0) list.SelectedIndex = 0; input.Focus(FocusState.Programmatic); input.SelectAll(); };
        Search(); await ShowDialogAsync(dialog);
        if (chosen is not null) await RunAsync(chosen.Run);
    }

    // WinUI can report either an explicit ListViewItem or its content for ItemClick.
    private static PaletteItem? ResolvePaletteItem(object? item) => item as PaletteItem ?? (item as FrameworkElement)?.Tag as PaletteItem;

    private static bool MatchesPaletteQuery(string text, string query)
    {
        int position = 0;
        foreach (char character in text)
            if (position < query.Length && char.ToUpperInvariant(character) == char.ToUpperInvariant(query[position])) position++;
        return position == query.Length;
    }

    private IEnumerable<PaletteItem> GetPaletteItems(string query)
    {
        var q = query.Trim();
        bool Matches(string text) => MatchesPaletteQuery(text, q);
        var commands = new PaletteItem[]
        {
            new("New tab", "Command · Ctrl+T", "\uE710", () => NewTabAsync()),
            new("Close tab", "Command · Ctrl+W", "\uE711", () => CloseTabAsync(FocusedTab.Id)),
            new("Reopen closed tab", "Command · Ctrl+Shift+T", "\uE7A7", () => RestoreClosedAsync()),
            new("History", "Command · Ctrl+H", "\uE81C", ShowHistoryAsync),
            new("Downloads", "Command · Ctrl+J", "\uE896", ShowDownloadsAsync),
            new("Settings", "Appearance, search, and tab behavior · Ctrl+,", "\uE713", ShowSettingsAsync),
            new("Clear browsing data", "Choose whether to clear local browsing data", "\uE74D", ClearAllBrowsingDataAsync),
            new("New temporary tab", "Discarded on restart · shares regular site data", "\uE78B", () => NewTabAsync(Navigation.NewTab, true)),
            new("Toggle sidebar", "Command · Ctrl+B", "\uE700", () => { ToggleSidebar(); return Task.CompletedTask; }),
            new("Toggle split view", "Command · Ctrl+Shift+S", "\uE89F", ToggleSplitAsync),
            new("Pin or unpin current tab", "Change pinned state", "\uE718", async () => { _session.Pin(FocusedTab.Id); await RefreshAsync(); QueueSave(); }),
            new("Reload page", "Command · Ctrl+R", "\uE72C", () => { Reload(); return Task.CompletedTask; }),
            new("Manage workspaces", "Create, rename, or remove workspaces", "\uE8F1", ShowWorkspacesAsync),
            new("Recently closed tabs", "Restore a tab", "\uE7A7", ShowRecentlyClosedAsync),
            new("Site permissions", "Review permissions for this site", "\uE72E", ShowSiteInfoAsync),
            new("Find in page", "Command · Ctrl+F", "\uE721", () => { OpenFindBar(); return Task.CompletedTask; }),
            new("Print page", "Command · Ctrl+P", "\uE749", () => { PrintPage(); return Task.CompletedTask; }),
            new("Hard reload", "Command · Ctrl+Shift+R", "\uE72C", HardReloadAsync),
            new("Zoom in", "Command · Ctrl++", "\uE710", () => { ZoomIn(); return Task.CompletedTask; }),
            new("Zoom out", "Command · Ctrl+-", "\uE738", () => { ZoomOut(); return Task.CompletedTask; }),
            new("Reset zoom", "Command · Ctrl+0", "\uE777", () => { ZoomReset(); return Task.CompletedTask; }),
            new("Toggle full screen", "Command · F11", "\uE740", () => { ToggleFullScreen(); return Task.CompletedTask; }),
            new("Developer tools", "Command · F12", "\uE756", () => { OpenDevTools(); return Task.CompletedTask; }),
            new("Duplicate tab", "Command · Ctrl+Shift+D", "\uE78B", () => DuplicateTabAsync(FocusedTab.Id)),
            new("Move tab up", "Command · Ctrl+Shift+PageUp", "\uE74A", () => MoveTabAsync(FocusedTab.Id, -1)),
            new("Move tab down", "Command · Ctrl+Shift+PageDown", "\uE74B", () => MoveTabAsync(FocusedTab.Id, 1)),
            new("Close other tabs", "Command", "\uE711", () => CloseOtherTabsAsync(FocusedTab.Id)),
            new("Close tabs below", "Command", "\uE711", () => CloseTabsBelowAsync(FocusedTab.Id)),
            new("Bookmarks", "Command · Ctrl+Shift+O", "\uE735", ShowBookmarksAsync),
            new("Bookmark current tab", "Command · Ctrl+D", "\uE734", ToggleBookmarkAsync),
            new("New private tab", "Command · Ctrl+Alt+N", "\uE727", () => NewTabAsync(Navigation.NewTab, isPrivate: true)),
            new("Open file", "Command · Ctrl+O", "\uE8E5", OpenLocalFileAsync),
            new("Save page as", "Command · Ctrl+S", "\uE74E", SavePageAsync),
            new("View source", "Command · Ctrl+U", "\uE8A1", () => ViewSourceAsync())
        };
        if (q.Length > 0)
        {
            foreach (var command in commands.Where(c => Matches(c.Title)).OrderByDescending(c => c.Title.StartsWith(q, StringComparison.OrdinalIgnoreCase))) yield return command;
            foreach (var tab in _session.State.Tabs.Where(t => Matches(t.Title) || Matches(t.Url)).Take(8))
                yield return new(tab.Title, "Open tab · " + _session.State.Workspaces.First(w => w.Id == tab.WorkspaceId).Name, "\uE8A5", () => ActivateTabAsync(tab.Id));
            foreach (var workspace in _session.State.Workspaces.Where(w => Matches(w.Name)))
                yield return new("Switch to " + workspace.Name, "Workspace", "\uE8F1", async () => { _session.State.ActiveWorkspaceId = workspace.Id; _splitTabId = null; _focusedTabId = null; await RefreshAsync(); QueueSave(); });
            foreach (var history in _session.State.History.Where(h => Matches(h.Title) || Matches(h.Url)).Take(5))
                yield return new(history.Title, "History · " + history.Url, "\uE81C", () => NewTabAsync(history.Url));
            var resolved = Navigation.Resolve(q, _session.State.Settings.SearchEngine);
            yield return new("Open “" + q + "”", resolved, "\uE721", () => NavigateAsync(q));
            yield return new("Search the web for “" + q + "”", _session.State.Settings.SearchEngine, "\uE721", () => NavigateAsync(Navigation.Search(q, _session.State.Settings.SearchEngine)));
        }
        else
        {
            foreach (var command in commands.Take(7)) yield return command;
            foreach (var tab in _session.VisibleTabs.Take(5)) yield return new(tab.Title, "Open tab · " + tab.Url, "\uE8A5", () => ActivateTabAsync(tab.Id));
            foreach (var command in commands.Skip(7)) yield return command;
        }
    }

    private async Task ShowHistoryAsync()
    {
        var search = new TextBox { PlaceholderText = "Search browsing history" };
        var list = new ListView { Height = 340, IsItemClickEnabled = true };
        var clearBrowsingDataBtn = new Button { Content = "Clear browsing data…", HorizontalAlignment = HorizontalAlignment.Left };
        var panel = new StackPanel { Width = 520, Spacing = 12, Children = { search, clearBrowsingDataBtn, list } };
        var dialog = Dialog("History", panel); dialog.PrimaryButtonText = "Clear history";
        dialog.DefaultButton = ContentDialogButton.Close;
        string? selected = null;
        bool openClearDialog = false;

        clearBrowsingDataBtn.Click += (_, _) =>
        {
            openClearDialog = true;
            dialog.Hide();
        };

        void Update()
        {
            dialog.PrimaryButtonText = string.IsNullOrWhiteSpace(search.Text) ? "Clear history" : "Clear filtered";
            list.Items.Clear();
            var matches = _session.State.History
                .Where(h => (h.Title + h.Url).Contains(search.Text, StringComparison.OrdinalIgnoreCase))
                .Take(150)
                .ToList();

            foreach (var entry in matches)
            {
                var rowGrid = new Grid { ColumnSpacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var textStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                textStack.Children.Add(new TextBlock { Text = entry.Title, TextTrimming = TextTrimming.CharacterEllipsis });
                textStack.Children.Add(new TextBlock { Text = entry.VisitedAt.ToLocalTime().ToString("MMM d, HH:mm") + " · " + entry.Url, FontSize = 11, Opacity = .55, TextTrimming = TextTrimming.CharacterEllipsis });
                rowGrid.Children.Add(textStack);

                var deleteBtn = IconButton("\uE711", "Delete from history", () =>
                {
                    _session.RemoveHistoryEntry(entry.Url);
                    Update();
                    QueueSave();
                });
                deleteBtn.Width = 28; deleteBtn.Height = 28; deleteBtn.Padding = new(4);
                Grid.SetColumn(deleteBtn, 1);
                rowGrid.Children.Add(deleteBtn);

                var item = new ListViewItem
                {
                    Tag = entry.Url,
                    Content = rowGrid,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                list.Items.Add(item);
            }

            if (list.Items.Count == 0)
                list.Items.Add(new ListViewItem { Content = "No browsing history yet.", IsEnabled = false });
        }

        search.TextChanged += (_, _) => Update();
        list.ItemClick += (_, e) => { if (e.ClickedItem is ListViewItem { Tag: string url }) { selected = url; dialog.Hide(); } };
        Update();
        var result = await ShowDialogAsync(dialog);
        if (openClearDialog)
        {
            await ClearAllBrowsingDataAsync();
            return;
        }
        if (result == ContentDialogResult.Primary)
        {
            if (string.IsNullOrWhiteSpace(search.Text))
                _session.State.History.Clear();
            else
                _session.ClearHistory(query: search.Text);
            QueueSave();
        }
        if (selected is not null) await NewTabAsync(selected);
    }

    private async Task ShowBookmarksAsync()
    {
        var search = new TextBox { PlaceholderText = "Search bookmarks" };
        var list = new ListView { Height = 340, IsItemClickEnabled = true };
        var addCurrent = new Button { Content = "Bookmark current tab", HorizontalAlignment = HorizontalAlignment.Left };
        var panel = new StackPanel { Width = 520, Spacing = 12, Children = { search, addCurrent, list } };
        var dialog = Dialog("Bookmarks", panel, "Close");
        string? selected = null;

        void Update()
        {
            list.Items.Clear();
            var matches = _session.State.Bookmarks
                .Where(b => (b.Title + b.Url).Contains(search.Text, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var bookmark in matches)
            {
                var rowGrid = new Grid { ColumnSpacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var fontIcon = Icon("\uE735", 13);
                fontIcon.Foreground = _theme.AccentPrimaryBrush;
                var icon = IconSlot(fontIcon);
                rowGrid.Children.Add(icon);

                var textStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                textStack.Children.Add(new TextBlock { Text = bookmark.Title, TextTrimming = TextTrimming.CharacterEllipsis });
                textStack.Children.Add(new TextBlock { Text = bookmark.Url, FontSize = 11, Opacity = .55, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(textStack, 1);
                rowGrid.Children.Add(textStack);

                var deleteBtn = IconButton("\uE711", "Delete bookmark", () =>
                {
                    _session.RemoveBookmark(bookmark.Id);
                    Update();
                    UpdateBookmarkIndicator();
                    RenderBookmarksBar();
                    QueueSave();
                });
                deleteBtn.Width = 28; deleteBtn.Height = 28; deleteBtn.Padding = new(4);
                Grid.SetColumn(deleteBtn, 2);
                rowGrid.Children.Add(deleteBtn);

                var item = new ListViewItem
                {
                    Tag = bookmark.Url,
                    Content = rowGrid,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                list.Items.Add(item);
            }

            if (list.Items.Count == 0)
                list.Items.Add(new ListViewItem { Content = "No bookmarks yet.", IsEnabled = false });
        }

        addCurrent.Click += async (_, _) =>
        {
            await ToggleBookmarkAsync();
            Update();
        };

        search.TextChanged += (_, _) => Update();
        list.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is ListViewItem { Tag: string url })
            {
                selected = url;
                dialog.Hide();
            }
        };

        Update();
        await ShowDialogAsync(dialog);
        if (selected is not null)
            await NavigateAsync(selected);
    }

    private static ListViewItem TextRow(string title, string detail, object tag) => new()
    {
        Tag = tag, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Content = new StackPanel { Spacing = 4, Padding = new(0, 5, 0, 5), Children = {
            new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis },
            new TextBlock { Text = detail, FontSize = 11, Opacity = .55, TextTrimming = TextTrimming.CharacterEllipsis } } }
    };

    private async Task ShowRecentlyClosedAsync()
    {
        var list = new ListView { Width = 520, MaxHeight = 380, IsItemClickEnabled = true };
        foreach (var tab in _session.State.RecentlyClosed) list.Items.Add(TextRow(tab.Title, tab.Url, tab.Id));
        if (list.Items.Count == 0) list.Items.Add(new ListViewItem { Content = "No recently closed tabs.", IsEnabled = false });
        var dialog = Dialog("Recently closed", list); Guid? selected = null;
        list.ItemClick += (_, e) => { if (e.ClickedItem is ListViewItem { Tag: Guid id }) { selected = id; dialog.Hide(); } };
        await ShowDialogAsync(dialog); if (selected.HasValue) await RestoreClosedAsync(selected);
    }

    private async Task ShowWorkspacesAsync()
    {
        var workspace = _session.ActiveWorkspace;
        var name = new TextBox { Header = "Current workspace", Text = workspace.Name, MaxLength = 40 };
        var newName = new TextBox { Header = "Create a workspace", PlaceholderText = "e.g. Research", MaxLength = 40 };
        var panel = new StackPanel { Width = 420, Spacing = 20, Children = { name, newName } };
        var remove = new Button { Content = "Remove current workspace", IsEnabled = _session.State.Workspaces.Count > 1 };
        panel.Children.Add(remove);
        panel.Children.Add(new TextBlock { Text = "Removing a workspace moves its tabs to another space. Workspaces share cookies and site data.", FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = .55 });
        var dialog = Dialog("Your spaces", panel, "Cancel"); dialog.PrimaryButtonText = "Save"; bool removed = false;
        remove.Click += (_, _) => { removed = true; dialog.Hide(); };
        var result = await ShowDialogAsync(dialog);
        if (removed) _session.RemoveWorkspace(workspace.Id);
        else if (result == ContentDialogResult.Primary)
        {
            if (!string.IsNullOrWhiteSpace(name.Text)) workspace.Name = name.Text.Trim();
            if (!string.IsNullOrWhiteSpace(newName.Text)) _session.AddWorkspace(newName.Text);
        }
        else return;
        _splitTabId = null; _focusedTabId = null; await RefreshAsync(); QueueSave();
    }

    private async Task ShowSettingsAsync()
    {
        var settings = _session.State.Settings;

        // Section 1: Appearance
        var theme = new ComboBox
        {
            Header = "Theme",
            ItemsSource = new[] { "System", "Light", "Dark" },
            SelectedItem = settings.Theme,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0
        };
        AutomationProperties.SetName(theme, "Theme");
        AutomationProperties.SetAutomationId(theme, "SettingsThemeComboBox");

        var accent = new ComboBox
        {
            Header = "Accent color",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0
        };
        AutomationProperties.SetName(accent, "Accent color");
        AutomationProperties.SetAutomationId(accent, "SettingsAccentComboBox");

        foreach (var name in SlateTheme.AccentNames)
        {
            var swatch = SlateTheme.Create(name, _root.ActualTheme == ElementTheme.Dark);
            var item = new ComboBoxItem
            {
                Tag = name,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        new Border { Width = 12, Height = 12, CornerRadius = new(6), Background = swatch.AccentBrush },
                        new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center }
                    }
                }
            };
            AutomationProperties.SetName(item, name);
            accent.Items.Add(item);
            if (name == settings.AccentTheme) accent.SelectedItem = item;
        }

        var motion = new ToggleSwitch
        {
            Header = "Reduce motion",
            IsOn = settings.ReduceMotion
        };
        AutomationProperties.SetName(motion, "Reduce motion");
        AutomationProperties.SetAutomationId(motion, "SettingsReduceMotionToggle");

        var bookmarksBarToggle = new ToggleSwitch
        {
            Header = "Show bookmarks bar",
            IsOn = settings.ShowBookmarksBar
        };
        AutomationProperties.SetName(bookmarksBarToggle, "Show bookmarks bar");
        AutomationProperties.SetAutomationId(bookmarksBarToggle, "SettingsBookmarksBarToggle");

        // Section 2: Browsing
        var engine = new ComboBox
        {
            Header = "Search engine",
            ItemsSource = Navigation.SearchEngines,
            SelectedItem = settings.SearchEngine,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0
        };
        AutomationProperties.SetName(engine, "Search engine");
        AutomationProperties.SetAutomationId(engine, "SettingsSearchEngineComboBox");

        var startupBehavior = new ComboBox
        {
            Header = "Startup behavior",
            ItemsSource = BrowserSettings.StartupBehaviorOptions,
            SelectedItem = settings.StartupBehavior,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0
        };
        AutomationProperties.SetName(startupBehavior, "Startup behavior");
        AutomationProperties.SetAutomationId(startupBehavior, "SettingsStartupBehaviorComboBox");

        var defaultZoom = new NumberBox
        {
            Header = "Default page zoom (%)",
            Minimum = 25,
            Maximum = 500,
            Value = settings.DefaultZoomPercent,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0
        };
        AutomationProperties.SetName(defaultZoom, "Default page zoom (%)");
        AutomationProperties.SetAutomationId(defaultZoom, "SettingsDefaultZoomNumberBox");

        var devTools = new ToggleSwitch
        {
            Header = "Developer tools (F12)",
            IsOn = settings.DeveloperToolsEnabled
        };
        AutomationProperties.SetName(devTools, "Developer tools (F12)");
        AutomationProperties.SetAutomationId(devTools, "SettingsDevToolsToggle");

        var defaultBrowserButton = new Button
        {
            Content = "Open Windows Default Apps settings",
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 248,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(defaultBrowserButton, "Open Windows Default Apps settings");
        AutomationProperties.SetAutomationId(defaultBrowserButton, "SettingsDefaultBrowserButton");
        defaultBrowserButton.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Notify("Could not open Default Apps settings: " + ex.Message);
            }
        };

        // Section 3: Tabs
        var restore = new ToggleSwitch
        {
            Header = "Restore tabs on startup",
            IsOn = settings.RestoreSession
        };
        AutomationProperties.SetName(restore, "Restore tabs on startup");
        AutomationProperties.SetAutomationId(restore, "SettingsRestoreTabsToggle");

        restore.Toggled += (_, _) =>
        {
            startupBehavior.SelectedItem = restore.IsOn ? "Restore previous session" : "Open new tab page";
        };
        startupBehavior.SelectionChanged += (_, _) =>
        {
            restore.IsOn = (startupBehavior.SelectedItem as string) == "Restore previous session";
        };

        var sleep = new ComboBox
        {
            Header = "Sleep inactive tabs",
            ItemsSource = BrowserSettings.SleepOptions,
            SelectedItem = BrowserSettings.MinutesToSleepOption(settings.SleepAfterMinutes),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0
        };
        AutomationProperties.SetName(sleep, "Sleep inactive tabs");
        AutomationProperties.SetAutomationId(sleep, "SettingsTabSleepComboBox");

        // Section 4: Privacy / Security
        var hardenedIsolationToggle = new ToggleSwitch
        {
            Header = "Hardened isolation (JIT-less mode)",
            IsOn = settings.HardenedIsolation
        };
        AutomationProperties.SetName(hardenedIsolationToggle, "Hardened isolation (JIT-less mode)");
        AutomationProperties.SetAutomationId(hardenedIsolationToggle, "SettingsHardenedIsolationToggle");

        var clearData = new Button
        {
            Content = "Clear browsing data",
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 248,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(clearData, "Clear browsing data");
        AutomationProperties.SetAutomationId(clearData, "SettingsClearBrowsingDataButton");

        // Section 5: Downloads
        var downloadLocation = new TextBox
        {
            Header = "Download location",
            Text = settings.DownloadPath ?? "",
            PlaceholderText = "Default (Downloads folder)",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(downloadLocation, "Download location");
        AutomationProperties.SetAutomationId(downloadLocation, "SettingsDownloadLocationTextBox");
        AutomationProperties.SetHelpText(downloadLocation, "Leave blank to use the Windows Downloads folder");

        var chooseFolder = new Button
        {
            Content = "Choose folder…",
            VerticalAlignment = VerticalAlignment.Bottom,
            MinWidth = 112
        };
        AutomationProperties.SetName(chooseFolder, "Choose folder");
        AutomationProperties.SetAutomationId(chooseFolder, "SettingsChooseFolderButton");
        ToolTipService.SetToolTip(chooseFolder, "Choose download folder");
        chooseFolder.Click += async (_, _) =>
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FolderPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
                picker.FileTypeFilter.Add("*");
                var folder = await picker.PickSingleFolderAsync();
                if (folder is not null)
                {
                    var path = folder.Path;
                    if (DownloadSafety.IsSafeLocalDirectory(path))
                    {
                        downloadLocation.Text = path;
                    }
                    else
                    {
                        Notify("The selected folder is invalid or unsafe.");
                    }
                }
            }
            catch
            {
                Notify("The folder picker is unavailable.");
            }
        };

        var downloadGrid = new Grid { ColumnSpacing = 8 };
        downloadGrid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        downloadGrid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        downloadGrid.Children.Add(downloadLocation);
        Grid.SetColumn(chooseFolder, 1);
        downloadGrid.Children.Add(chooseFolder);

        var askDownload = new ToggleSwitch
        {
            Header = "Ask where to save each file before downloading",
            IsOn = settings.AskDownloadLocation
        };
        AutomationProperties.SetName(askDownload, "Ask where to save each file before downloading");
        AutomationProperties.SetAutomationId(askDownload, "SettingsAskDownloadToggle");

        // Section 6: Profile
        var profile = new Button
        {
            Content = "Open profile folder",
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 248,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(profile, "Open profile folder");
        AutomationProperties.SetAutomationId(profile, "SettingsOpenProfileButton");
        profile.Click += (_, _) =>
        {
            if (DownloadSafety.IsSafeLocalDirectory(_store.DirectoryPath))
            {
                Directory.CreateDirectory(_store.DirectoryPath);
                Process.Start(new ProcessStartInfo(_store.DirectoryPath) { UseShellExecute = true });
            }
            else Notify("The profile directory is invalid.");
        };

        // Section 7: About
        var appVersion = typeof(MainWindow).Assembly.GetName().Version;
        var appVersionString = appVersion is not null ? $"{appVersion.Major}.{appVersion.Minor}" : "0.2";
        var fullVersionString = appVersion?.ToString() ?? "0.2.0.0";
        string webView2Version;
        try
        {
            webView2Version = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch
        {
            webView2Version = "Unavailable";
        }
        var arch = RuntimeInformation.ProcessArchitecture.ToString();

        var aboutTitle = new TextBlock
        {
            Text = $"Slate {appVersionString}",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        AutomationProperties.SetName(aboutTitle, $"Slate {appVersionString}");
        AutomationProperties.SetAutomationId(aboutTitle, "SettingsAboutTitleText");
        AutomationProperties.SetHeadingLevel(aboutTitle, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level3);
        var assemblyVersionNote = Note($"Assembly version: {fullVersionString}");
        AutomationProperties.SetName(assemblyVersionNote, assemblyVersionNote.Text);
        AutomationProperties.SetAutomationId(assemblyVersionNote, "SettingsAssemblyVersionText");
        var webViewVersionNote = Note($"WebView2 runtime: {webView2Version}");
        AutomationProperties.SetName(webViewVersionNote, webViewVersionNote.Text);
        AutomationProperties.SetAutomationId(webViewVersionNote, "SettingsWebView2VersionText");
        var architectureNote = Note($"Architecture: {arch}");
        AutomationProperties.SetName(architectureNote, architectureNote.Text);
        AutomationProperties.SetAutomationId(architectureNote, "SettingsArchitectureText");

        StackPanel Section(string title, params UIElement[] controls)
        {
            var section = new StackPanel { Spacing = 6 };
            var heading = new TextBlock
            {
                Text = title,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = _theme.TextSecondaryBrush,
                CharacterSpacing = 30,
                Margin = new(0, 2, 0, 2)
            };
            AutomationProperties.SetName(heading, title + " settings");
            AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
            section.Children.Add(heading);
            foreach (var control in controls) section.Children.Add(control);
            return section;
        }
        TextBlock Note(string text) => new() { Text = text, FontSize = 11, Foreground = _theme.TextMutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new(0, 2, 0, 0) };
        Grid ToggleRow(string label, ToggleSwitch toggle)
        {
            toggle.Header = null;
            toggle.HorizontalAlignment = HorizontalAlignment.Right;
            toggle.VerticalAlignment = VerticalAlignment.Center;
            var row = new Grid { MinHeight = 32, ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetName(text, label);
            row.Children.Add(text);
            Grid.SetColumn(toggle, 1);
            row.Children.Add(toggle);
            return row;
        }

        theme.TabIndex = 0;
        accent.TabIndex = 1;
        motion.TabIndex = 2;
        bookmarksBarToggle.TabIndex = 3;
        engine.TabIndex = 4;
        startupBehavior.TabIndex = 5;
        defaultZoom.TabIndex = 6;
        devTools.TabIndex = 7;
        defaultBrowserButton.TabIndex = 8;
        restore.TabIndex = 9;
        sleep.TabIndex = 10;
        clearData.TabIndex = 11;
        downloadLocation.TabIndex = 12;
        chooseFolder.TabIndex = 13;
        askDownload.TabIndex = 14;
        profile.TabIndex = 15;

        var colors = new Grid { ColumnSpacing = 12 };
        colors.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        colors.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        colors.Children.Add(theme); Grid.SetColumn(accent, 1); colors.Children.Add(accent);

        var panel = new StackPanel
        {
            Width = 430,
            Spacing = 12,
            Padding = new(2, 0, 12, 4),
            Children =
            {
                Section("Appearance", colors, ToggleRow("Reduce motion", motion), ToggleRow("Show bookmarks bar", bookmarksBarToggle)),
                Section("Browsing", engine, startupBehavior, defaultZoom, ToggleRow("Developer tools (F12)", devTools), defaultBrowserButton, Note("Configure Slate as your default web browser in Windows Settings.")),
                Section("Tabs", ToggleRow("Restore tabs on startup", restore), sleep, Note("Sleeping preserves page state while freeing memory. Closed tabs restore their last URL.")),
                Section("Privacy / Security", ToggleRow("Hardened isolation (JIT-less mode)", hardenedIsolationToggle), Note("JIT-less mode neutralizes ~60% of zero-day memory bugs. Keep off for full hardware VP9/AV1 decoding (YouTube 4K) and responsive web apps."), Note("Temporary tabs share regular cookies; InPrivate tabs use isolated ephemeral storage."), clearData),
                Section("Downloads", downloadGrid, ToggleRow("Ask where to save each file before downloading", askDownload)),
                Section("Profile", profile, Note("Slate isolates browsing data locally. Cloud sync is disabled for privacy.")),
                Section("About", aboutTitle, assemblyVersionNote, webViewVersionNote, architectureNote, Note("Native Windows browser · .NET 8 · Microsoft WebView2"))
            }
        };

        var settingsScroll = new ScrollViewer
        {
            Content = panel,
            MaxHeight = 560,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            ZoomMode = ZoomMode.Disabled,
            BringIntoViewOnFocusChange = true,
            IsTabStop = false
        };
        var dialog = Dialog("Settings", settingsScroll, "Cancel");
        dialog.PrimaryButtonText = "Save changes";
        dialog.DefaultButton = ContentDialogButton.Primary;

        var systemAnimationsEnabled = true;
        try { systemAnimationsEnabled = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
        catch { }

        void SuppressSettingsTransitions()
        {
            dialog.Transitions.Clear();
            settingsScroll.Transitions.Clear();
            panel.Transitions.Clear();
            foreach (var element in Descendants(dialog).OfType<UIElement>()) element.Transitions.Clear();
        }

        var shouldReduceMotion = settings.ReduceMotion || !systemAnimationsEnabled;
        if (shouldReduceMotion) SuppressSettingsTransitions();
        motion.Toggled += (_, _) =>
        {
            if (motion.IsOn || !systemAnimationsEnabled) SuppressSettingsTransitions();
        };
        dialog.Opened += (_, _) =>
        {
            if (motion.IsOn || !systemAnimationsEnabled) SuppressSettingsTransitions();
            theme.Focus(FocusState.Programmatic);
        };

        bool clearRequested = false;
        clearData.Click += (_, _) => { clearRequested = true; dialog.Hide(); };

        var result = await ShowDialogAsync(dialog);
        if (clearRequested) { await ClearAllBrowsingDataAsync(); return; }
        if (result != ContentDialogResult.Primary) return;

        settings.Theme = theme.SelectedItem as string ?? "System";
        settings.AccentTheme = (accent.SelectedItem as ComboBoxItem)?.Tag as string ?? "Slate";
        settings.SearchEngine = engine.SelectedItem as string ?? "DuckDuckGo";
        settings.StartupBehavior = startupBehavior.SelectedItem as string ?? "Restore previous session";
        settings.DeveloperToolsEnabled = devTools.IsOn;
        settings.HardenedIsolation = hardenedIsolationToggle.IsOn;
        settings.ShowBookmarksBar = bookmarksBarToggle.IsOn;

        var customPath = downloadLocation.Text.Trim();
        string safePath;
        if (string.IsNullOrWhiteSpace(customPath))
        {
            settings.DownloadPath = null;
            safePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
        else if (DownloadSafety.IsSafeLocalDirectory(customPath))
        {
            settings.DownloadPath = customPath;
            safePath = customPath;
        }
        else
        {
            Notify("The specified download folder is invalid or unsafe; keeping previous setting.");
            safePath = DownloadSafety.IsSafeLocalDirectory(settings.DownloadPath)
                ? settings.DownloadPath!
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        if (DownloadSafety.IsSafeLocalDirectory(safePath))
        {
            foreach (var runtime in _runtimes.Values)
            {
                if (runtime.View.CoreWebView2 is { } core)
                {
                    try
                    {
                        core.Profile.DefaultDownloadFolderPath = safePath;
                    }
                    catch { }
                }
            }
        }

        settings.AskDownloadLocation = askDownload.IsOn;

        foreach (var runtime in _runtimes.Values)
        {
            if (runtime.View.CoreWebView2 is { } core)
            {
                core.Settings.AreDevToolsEnabled = settings.DeveloperToolsEnabled;
            }
        }

        settings.SleepAfterMinutes = BrowserSettings.SleepOptionToMinutes(sleep.SelectedItem as string);
        settings.DefaultZoomPercent = double.IsFinite(defaultZoom.Value) ? (int)Math.Clamp(defaultZoom.Value, 25, 500) : 100;
        settings.RestoreSession = restore.IsOn;
        settings.ReduceMotion = motion.IsOn;

        ApplyAppearance(); RenderBookmarksBar(); await RefreshAsync(); QueueSave();
    }

    private async Task ClearAllBrowsingDataAsync()
    {
        var timeRanges = new[] { "Last hour", "Last 24 hours", "Last 7 days", "Last 4 weeks", "All time" };
        var timeCombo = new ComboBox { Header = "Time range", ItemsSource = timeRanges, SelectedItem = "All time", HorizontalAlignment = HorizontalAlignment.Stretch };
        var chkHistory = new CheckBox { Content = "Browsing history", IsChecked = true };
        var chkDownloads = new CheckBox { Content = "Download history", IsChecked = true };
        var chkCookies = new CheckBox { Content = "Cookies and other site data", IsChecked = true };
        var chkCache = new CheckBox { Content = "Cached images and files", IsChecked = true };
        var chkPasswords = new CheckBox { Content = "Passwords and autofill data", IsChecked = false };

        var panel = new StackPanel
        {
            Width = 430,
            Spacing = 12,
            Children =
            {
                timeCombo,
                new TextBlock { Text = "Data categories", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = _theme.TextSecondaryBrush, Margin = new(0, 4, 0, 0) },
                chkHistory,
                chkDownloads,
                chkCookies,
                chkCache,
                chkPasswords,
                new TextBlock { Text = "Open tabs and downloaded files are kept.", FontSize = 11, Opacity = .6, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) }
            }
        };

        var dialog = Dialog("Clear browsing data", panel, "Cancel");
        dialog.PrimaryButtonText = "Clear data";
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

        if (new[] { chkHistory, chkDownloads, chkCookies, chkCache, chkPasswords }.All(item => item.IsChecked != true))
        {
            Notify("Choose at least one data category to clear.");
            return;
        }

        DateTimeOffset? since = (timeCombo.SelectedItem as string) switch
        {
            "Last hour" => DateTimeOffset.UtcNow.AddHours(-1),
            "Last 24 hours" => DateTimeOffset.UtcNow.AddHours(-24),
            "Last 7 days" => DateTimeOffset.UtcNow.AddDays(-7),
            "Last 4 weeks" => DateTimeOffset.UtcNow.AddDays(-28),
            _ => null
        };

        await PerformClearBrowsingDataAsync(
            since,
            chkHistory.IsChecked == true,
            chkDownloads.IsChecked == true,
            chkCookies.IsChecked == true,
            chkCache.IsChecked == true,
            chkPasswords.IsChecked == true);
    }

    private async Task PerformClearBrowsingDataAsync(
        DateTimeOffset? since = null,
        bool clearHistory = true,
        bool clearDownloads = true,
        bool clearCookies = true,
        bool clearCache = true,
        bool clearPasswords = false)
    {
        WebView2? temporaryView = null;
        try
        {
            var kinds = (CoreWebView2BrowsingDataKinds)0;
            if (clearCookies) kinds |= CoreWebView2BrowsingDataKinds.Cookies | CoreWebView2BrowsingDataKinds.AllSite;
            if (clearCache) kinds |= CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage;
            if (clearHistory) kinds |= CoreWebView2BrowsingDataKinds.BrowsingHistory;
            if (clearDownloads) kinds |= CoreWebView2BrowsingDataKinds.DownloadHistory;
            if (clearPasswords) kinds |= CoreWebView2BrowsingDataKinds.PasswordAutosave | CoreWebView2BrowsingDataKinds.GeneralAutofill;
            if (kinds == 0) { Notify("Choose at least one data category to clear."); return; }

            if (kinds != 0)
            {
                var profile = _runtimes.Values.Select(runtime => runtime.View.CoreWebView2?.Profile).FirstOrDefault(value => value is not null);
                if (profile is null)
                {
                    temporaryView = new WebView2 { Visibility = Visibility.Collapsed, Width = 1, Height = 1 };
                    _parkedViews.Children.Add(temporaryView);
                    await temporaryView.EnsureCoreWebView2Async(await GetEnvironmentAsync());
                    profile = temporaryView.CoreWebView2.Profile;
                }
                if (since.HasValue)
                    await profile.ClearBrowsingDataAsync(kinds, since.Value.UtcDateTime, DateTime.UtcNow);
                else
                    await profile.ClearBrowsingDataAsync(kinds);
            }

            if (clearCache)
            {
                _uiFaviconGeneration++;
                _uiFaviconCache.Clear();
                _inFlightFaviconLoads.Clear();
                await _faviconStore.ClearAsync();
            }

            if (clearHistory)
            {
                if (since.HasValue)
                    _session.ClearHistory(since: since.Value);
                else
                {
                    _session.State.History.Clear();
                    _session.State.RecentlyClosed.Clear();
                }
            }

            if (clearDownloads)
            {
                if (since.HasValue)
                    _session.State.Downloads.RemoveAll(d => d.StartedAt >= since.Value && !_downloadOperations.ContainsKey(d.Id));
                else
                    _session.State.Downloads.RemoveAll(download => !_downloadOperations.ContainsKey(download.Id));
            }

            foreach (var runtime in _runtimes.Values)
            {
                runtime.SecureNavigation = false; runtime.CertificateError = false;
                runtime.View.CoreWebView2?.Reload();
            }
            RenderSidebar(); QueueSave(); Notify("Browsing data cleared. Downloaded files were not deleted.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Notify("Browsing data could not be cleared: " + ex.Message);
        }
        finally
        {
            if (temporaryView is not null)
            {
                if (temporaryView.Parent is Panel parent) parent.Children.Remove(temporaryView);
                temporaryView.Close();
            }
        }
    }

    private async Task<(bool Allow, bool Remember)> RequestPermissionAsync(string origin, string permission, string description)
    {
        var remember = new CheckBox { Content = "Remember for this site", IsChecked = false };
        var panel = new StackPanel { Width = 420, Spacing = 18, Children = {
            new TextBlock { Text = origin, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = "This site is asking to use: " + permission, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Opacity = .65 }, remember } };
        var dialog = Dialog("Site permission", panel, "Block"); dialog.PrimaryButtonText = "Allow";
        return (await ShowDialogAsync(dialog) == ContentDialogResult.Primary, remember.IsChecked == true);
    }

    private async Task ShowSiteInfoAsync()
    {
        var core = CurrentCore(); var url = FocusedTab.Url;
        if (Navigation.IsLocalFileUrl(url))
        {
            var filePanel = new StackPanel { Width = 430, Spacing = 10, Children = {
                new TextBlock { Text = "Local File", FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                new TextBlock { Text = "This file is loaded from your local computer. Local files cannot access internet services or other sites.", TextWrapping = TextWrapping.Wrap, Opacity = .7 },
                new TextBlock { Text = new Uri(url).LocalPath, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }
            } };
            var fileDialog = Dialog("File information", filePanel);
            await ShowDialogAsync(fileDialog);
            return;
        }
        if (Navigation.IsViewSourceUrl(url))
        {
            var srcPanel = new StackPanel { Width = 430, Spacing = 10, Children = {
                new TextBlock { Text = "Page Source", FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                new TextBlock { Text = "This tab is displaying the sandboxed, script-disabled source code of a document.", TextWrapping = TextWrapping.Wrap, Opacity = .7 },
                new TextBlock { Text = url["view-source:".Length..], FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }
            } };
            var srcDialog = Dialog("Source information", srcPanel);
            await ShowDialogAsync(srcDialog);
            return;
        }
        if (core is null || !Navigation.IsWebUrl(url)) { Notify("Open a website to see connection details and permissions."); return; }
        var origin = new Uri(url).GetLeftPart(UriPartial.Authority);
        var runtime = _runtimes.GetValueOrDefault(FocusedTab.Id);
        var permissions = (await core.Profile.GetNonDefaultPermissionSettingsAsync())
            .Where(p => string.Equals(Navigation.WebOrigin(p.PermissionOrigin), origin, StringComparison.OrdinalIgnoreCase)).ToList();
        var connection = runtime?.CertificateError == true ? "Slate blocked a TLS certificate error for this page." :
            runtime?.SecureNavigation == true ? "This page completed a secure HTTPS navigation." :
            url.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ? "This HTTPS navigation has not completed securely." :
            "This page uses an unencrypted HTTP connection.";
        var panel = new StackPanel { Width = 430, Spacing = 14, Children = {
            new TextBlock { Text = origin, FontSize = 17, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = connection, TextWrapping = TextWrapping.Wrap, Opacity = .65 } } };
        var dialog = Dialog("Site information", panel);
        bool openCertViewer = false;
        if (url.StartsWith("https:", StringComparison.OrdinalIgnoreCase) || runtime?.CertificateError == true || runtime?.LastCertificate is not null)
        {
            var viewCertBtn = new Button { Content = "View certificate", HorizontalAlignment = HorizontalAlignment.Left };
            viewCertBtn.Click += (_, _) => { openCertViewer = true; dialog.Hide(); };
            panel.Children.Add(viewCertBtn);
        }
        if (permissions.Count == 0) panel.Children.Add(new TextBlock { Text = "No remembered permissions for this site.", Opacity = .6 });
        foreach (var permission in permissions) panel.Children.Add(new TextBlock { Text = permission.PermissionKind + " · " + permission.PermissionState });
        if (permissions.Count > 0) dialog.PrimaryButtonText = "Reset permissions";
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
        {
            foreach (var permission in permissions) await core.Profile.SetPermissionStateAsync(permission.PermissionKind, permission.PermissionOrigin, CoreWebView2PermissionState.Default);
            Notify("Permissions reset. Reload the site to apply.");
        }
        if (openCertViewer)
        {
            await ShowCertificateViewerAsync(url, runtime);
        }
    }

    private async Task ShowCertificateViewerAsync(string url, TabRuntime? runtime)
    {
        var panel = new StackPanel { Width = 480, Spacing = 12 };
        X509Certificate2? cert = null;
        string? certErrorDetail = null;

        if (runtime?.LastCertificate is not null)
        {
            try
            {
                var pem = runtime.LastCertificate.ToPemEncoding();
                if (!string.IsNullOrEmpty(pem))
                {
                    cert = X509Certificate2.CreateFromPem(pem);
                }
            }
            catch { }
            if (runtime.LastCertificateError.HasValue)
            {
                certErrorDetail = "TLS Error: " + runtime.LastCertificateError.Value;
            }
        }

        if (cert is null && runtime?.LastCertificate is null && runtime?.CertificateError != true &&
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            try
            {
                cert = await ProbeServerCertificateAsync(uri, TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                certErrorDetail = "Could not probe TLS certificate: " + ex.Message;
            }
        }

        if (cert is not null)
        {
            void AddRow(string label, string value)
            {
                var row = new StackPanel { Spacing = 2 };
                row.Children.Add(new TextBlock { Text = label, FontSize = 11, Opacity = 0.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                row.Children.Add(new TextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                panel.Children.Add(row);
            }

            if (!string.IsNullOrEmpty(certErrorDetail))
            {
                panel.Children.Add(new TextBlock { Text = certErrorDetail, Foreground = _theme.AccentPrimaryBrush, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            }

            AddRow("Subject", SafeCertificateValue(() => cert.Subject));
            AddRow("Issuer", SafeCertificateValue(() => cert.Issuer));
            AddRow("Validity", FormatCertificateValidity(cert.NotBefore, cert.NotAfter, DateTime.Now));
            AddRow("Thumbprint (SHA-1)", SafeCertificateValue(() => cert.Thumbprint));
            AddRow("Serial Number", SafeCertificateValue(() => cert.SerialNumber));
            if (cert.SignatureAlgorithm?.FriendlyName is { } sigAlg)
            {
                AddRow("Signature Algorithm", SafeCertificateValue(() => sigAlg));
            }
        }
        else if (runtime?.LastCertificate is not null)
        {
            var rawCert = runtime.LastCertificate;
            void AddRow(string label, string value)
            {
                var row = new StackPanel { Spacing = 2 };
                row.Children.Add(new TextBlock { Text = label, FontSize = 11, Opacity = 0.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                row.Children.Add(new TextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                panel.Children.Add(row);
            }

            if (!string.IsNullOrEmpty(certErrorDetail))
            {
                panel.Children.Add(new TextBlock { Text = certErrorDetail, Foreground = _theme.AccentPrimaryBrush, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            }

            AddRow("Subject", SafeCertificateValue(() => rawCert.Subject));
            AddRow("Issuer", SafeCertificateValue(() => rawCert.Issuer));
            string validity;
            try { validity = FormatCertificateValidity(rawCert.ValidFrom, rawCert.ValidTo, DateTime.Now); }
            catch { validity = "Unavailable"; }
            AddRow("Validity", validity);
            AddRow("Serial Number", SafeCertificateValue(() => rawCert.DerEncodedSerialNumber));
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = certErrorDetail ?? "No certificate information available for this address.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7
            });
        }

        var dialog = Dialog("Certificate details", panel, "Close");
        await ShowDialogAsync(dialog);
    }

    internal static async Task<X509Certificate2?> ProbeServerCertificateAsync(Uri uri, TimeSpan timeout)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || timeout <= TimeSpan.Zero)
            throw new ArgumentException("A positive timeout and HTTPS URI are required.");
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(timeout);
        await client.ConnectAsync(uri.Host, uri.Port > 0 ? uri.Port : 443, cts.Token);
        using var sslStream = new SslStream(client.GetStream(), false);
        await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = uri.Host }, cts.Token);
        return sslStream.RemoteCertificate is null ? null : new X509Certificate2(sslStream.RemoteCertificate);
    }

    internal static string FormatCertificateValidity(double validFrom, double validTo, DateTime now)
    {
        if (!double.IsFinite(validFrom) || !double.IsFinite(validTo) ||
            validFrom < DateTimeOffset.MinValue.ToUnixTimeSeconds() || validFrom > DateTimeOffset.MaxValue.ToUnixTimeSeconds() ||
            validTo < DateTimeOffset.MinValue.ToUnixTimeSeconds() || validTo > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            return "Unavailable";
        return FormatCertificateValidity(
            DateTimeOffset.FromUnixTimeSeconds((long)validFrom).LocalDateTime,
            DateTimeOffset.FromUnixTimeSeconds((long)validTo).LocalDateTime,
            now);
    }

    private static string FormatCertificateValidity(DateTime validFrom, DateTime validTo, DateTime now)
        => $"From {validFrom.ToLocalTime():yyyy-MM-dd HH:mm} to {validTo.ToLocalTime():yyyy-MM-dd HH:mm} ({(now > validTo ? "Expired" : now < validFrom ? "Not yet valid" : "Valid")})";

    private static string SafeCertificateValue(Func<string?> read)
    {
        try { return BrowserText.SanitizeLabel(read(), 2048); }
        catch { return "Unavailable"; }
    }

    private async Task ShowDownloadsAsync()
    {
        var rows = new StackPanel { Width = 520, Spacing = 16 };
        var updates = new List<Action>();
        foreach (var entry in _session.State.Downloads.Take(100))
        {
            var title = new TextBlock { Text = entry.FileName, TextTrimming = TextTrimming.CharacterEllipsis };
            var state = new TextBlock { FontSize = 11, Opacity = .6 };
            var progress = new ProgressBar { Height = 3, Maximum = 100 };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var open = IconButton("\uE8E5", "Open file", () =>
            {
                if (File.Exists(entry.Path) && DownloadSafety.IsSafeLocalFilePath(entry.Path) &&
                    !Navigation.IsDangerousExtension(Path.GetExtension(entry.Path)))
                    Process.Start(new ProcessStartInfo(entry.Path) { UseShellExecute = true });
                else
                    Notify("The downloaded file no longer exists or is no longer safe to open.");
            });
            var pause = IconButton("\uE769", "Pause download", () => { if (_downloadOperations.TryGetValue(entry.Id, out var op)) op.Pause(); });
            var resume = IconButton("\uE768", "Resume download", () => { if (_downloadOperations.TryGetValue(entry.Id, out var op) && op.CanResume) op.Resume(); });
            var cancel = IconButton("\uE711", "Cancel download", () => { if (_downloadOperations.TryGetValue(entry.Id, out var op)) op.Cancel(); });
            var folder = IconButton("\uE8B7", "Show in folder", () =>
            {
                var directory = Path.GetDirectoryName(entry.Path);
                if (File.Exists(entry.Path) && DownloadSafety.IsSafeLocalFilePath(entry.Path) &&
                    !Navigation.IsDangerousExtension(Path.GetExtension(entry.Path)))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{entry.Path}\"") { UseShellExecute = true });
                else if (directory is not null && Directory.Exists(directory) && DownloadSafety.IsSafeLocalDirectory(directory))
                    Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
                else Notify("The download folder no longer exists or is invalid.");
            });
            actions.Children.Add(open); actions.Children.Add(pause); actions.Children.Add(resume); actions.Children.Add(cancel); actions.Children.Add(folder);
            rows.Children.Add(new StackPanel { Spacing = 7, Children = { title, state, progress, actions } });
            updates.Add(() =>
            {
                var op = _downloadOperations.GetValueOrDefault(entry.Id);
                title.Text = entry.FileName;
                state.Text = entry.Status + " · " + FormatBytes(entry.BytesReceived) + (entry.TotalBytes is > 0 ? " / " + FormatBytes(entry.TotalBytes.Value) : "");
                progress.IsIndeterminate = entry.TotalBytes is not > 0 && entry.Status == "Downloading";
                progress.Value = entry.TotalBytes is > 0 ? Math.Min(100, 100d * entry.BytesReceived / entry.TotalBytes.Value) : 0;
                open.IsEnabled = entry.Status == "Completed" && File.Exists(entry.Path) &&
                    DownloadSafety.IsSafeLocalFilePath(entry.Path) && !Navigation.IsDangerousExtension(Path.GetExtension(entry.Path));
                pause.IsEnabled = op?.State == CoreWebView2DownloadState.InProgress;
                resume.IsEnabled = op?.CanResume == true && op.State == CoreWebView2DownloadState.Interrupted;
                cancel.IsEnabled = op is not null && entry.Status is "Downloading" or "Paused";
            });
        }
        if (updates.Count == 0) rows.Children.Add(new TextBlock { Text = "Your downloads will appear here.", Opacity = .6, Margin = new(0, 20, 0, 20) });
        var dialog = Dialog("Downloads", new ScrollViewer { Content = rows, MaxHeight = 430 });
        dialog.PrimaryButtonText = "Clear finished";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => { foreach (var update in updates) update(); };
        foreach (var update in updates) update(); timer.Start();
        try
        {
            if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
            {
                _session.State.Downloads.RemoveAll(d => d.Status is "Completed" or "Canceled" || (d.Status == "Interrupted" && !_downloadOperations.ContainsKey(d.Id))); QueueSave();
            }
        }
        finally { timer.Stop(); }
    }

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB" : $"{bytes / 1024d:0.0} KB";
}
