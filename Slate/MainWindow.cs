using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using Slate.Core;
using Slate.Engine;
using Slate.Services;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.Graphics;

namespace Slate;

public sealed partial class MainWindow : Window
{
    private readonly StateStore _store;
    private readonly FaviconStore _faviconStore;
    private readonly Dictionary<string, BitmapImage> _uiFaviconCache = new();
    private readonly Dictionary<string, Task<BitmapImage?>> _inFlightFaviconLoads = new();
    private int _uiFaviconGeneration;
    private readonly BrowserSession _session;
    private readonly BrowserEngineService _engineService;
    private readonly TabManagerService _tabManager = new();
    private readonly NavigationCoordinator _navigationCoordinator = new();
    private readonly DownloadCoordinator _downloadCoordinator = new();
    private readonly GamingEfficiencyService _gamingEfficiencyService;
    private readonly Grid _root = new();
    private readonly Grid _body = new();
    private readonly Grid _sidebar = new();
    private readonly StackPanel _tabList = new() { Spacing = 4 };
    private readonly ComboBox _workspacePicker = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
    private readonly Grid _content = new();
    private readonly Border _toolbarSurface = new() { HorizontalAlignment = HorizontalAlignment.Stretch, CornerRadius = new(SlateTheme.RadiusMedium) };
    private readonly Border _pageFrame = new() { CornerRadius = new(SlateTheme.RadiusMedium), BorderThickness = new(1) };
    private readonly Grid _panes = new();
    private readonly Grid _primary = new();
    private readonly Grid _secondary = new();
    private readonly Grid _parkedViews = new() { Visibility = Visibility.Collapsed };
    private readonly TextBox _address = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Search or enter address", MinWidth = 100, Height = 36, FontSize = 13, Padding = new(12, 6, 12, 6), BorderThickness = new(1), CornerRadius = new(SlateTheme.RadiusMedium) };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 2, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = .65 };
    private readonly TextBlock _windowTitle = new() { FontSize = 12, Opacity = .6, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _brand = new() { Text = "S L A T E", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, CharacterSpacing = 120 };
    private readonly Ellipse _brandRing = new() { StrokeThickness = 1.25 };
    private readonly Ellipse _brandDot = new() { Width = 3, Height = 3, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
    private readonly UISettings _uiSettings = new();
    private SlateTheme.Palette _theme = null!;
    private readonly Border _findBar = new();
    private readonly TextBox _findInput = new();
    private readonly TextBlock _findStatus = new();
    private readonly Button _zoomBadge = new();
    private Button _findPrev = null!, _findNext = null!, _findClose = null!;
    private bool _findBarOpen;
    private bool _isFullScreen;
    private Button _bookmarkButton = null!;
    private Button _downloadsButton = null!;
    private readonly ScrollViewer _bookmarksBar = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Height = 32, Visibility = Visibility.Collapsed };
    private readonly StackPanel _bookmarksList = new() { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new(4, 0, 4, 0) };
    private readonly Popup _suggestionsPopup = new();
    private readonly Border _suggestionsBorder = new();
    private readonly StackPanel _suggestionsList = new() { Spacing = 2 };
    private int _selectedSuggestionIndex = -1;
    private List<OmniboxSuggestion> _currentSuggestions = [];
    private Button _back = null!, _forward = null!, _reload = null!, _security = null!, _collapse = null!, _workspaceMenu = null!;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly DispatcherTimer _sleepTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _dialogs = new(1, 1);
    private Guid? _splitTabId;
    private Guid? _focusedTabId;
    private bool _rendering, _closing, _addressEditing, _ready;
    private ContentDialog? _activeDialog;
    private DispatcherTimer? _sidebarAnimation;
    private readonly BrowserCommandRouter _commandRouter;
    internal BrowserCommandRouter CommandRouter => _commandRouter;
    private bool Collapsed => _session.State.Settings.SidebarCollapsed;
    private BrowserTab FocusedTab => _session.State.Tabs.FirstOrDefault(t => t.Id == _focusedTabId) ?? _session.ActiveTab;
    public MainWindow()
    {
        Title = "Slate";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Slate.ico"));
        _store = new StateStore(App.ProfileDirectory);
        _faviconStore = new FaviconStore(Path.Combine(App.ProfileDirectory, "favicons"));
        _session = new BrowserSession(_store.Load());
        bool hardenedIsolation = App.HasHardenedIsolationArg || _session.State.Settings.HardenedIsolation;
        _engineService = new BrowserEngineService(App.ProfileDirectory, hardenedIsolation);
        _tabManager.FocusedTabIdProvider = () => _focusedTabId;
        _gamingEfficiencyService = new GamingEfficiencyService(
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            DispatcherQueue,
            () => _tabManager.GetActiveCoreViews(),
            () => _tabManager.GetFocusedCoreView(),
            () => _engineService.GetProcessInfos());
        BuildShell();
        Content = _root;
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        ConfigureWindow();
        ApplyAppearance();
        _uiSettings.ColorValuesChanged += OnSystemColorValuesChanged;
        _commandRouter = new BrowserCommandRouter(this, () => _activeDialog is not null);
        _commandRouter.CanExecuteFilter = cmd =>
        {
            if (cmd == BrowserCommand.StopLoading)
                return _findBarOpen || FocusedTab.IsLoading;
            return true;
        };
        AddShortcuts();
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        _sleepTimer.Tick += (_, _) => SleepIdleTabs();
        _sleepTimer.Start();
        _root.Loaded += async (_, _) =>
        {
            if (_ready) return;
            _ready = true;
            await RefreshAsync();
            if (_store.LastError is { } error) Notify(error);
        };
        AppWindow.Closing += (_, _) => Shutdown();
        Closed += (_, _) => Shutdown();
    }

    private void BuildShell()
    {
        _root.RowDefinitions.Add(new() { Height = new(36) });
        _root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var titleBar = new Grid { Padding = new(18, 0, 150, 0) };
        titleBar.ColumnDefinitions.Add(new() { Width = new(210) });
        titleBar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var brandLockup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center };
        var brandMark = new Grid { Width = 12, Height = 12 };
        brandMark.Children.Add(_brandRing); brandMark.Children.Add(_brandDot);
        brandLockup.Children.Add(brandMark);
        brandLockup.Children.Add(_brand);
        titleBar.Children.Add(brandLockup);
        Grid.SetColumn(_windowTitle, 1); titleBar.Children.Add(_windowTitle);
        _root.Children.Add(titleBar);
        SetTitleBar(titleBar);

        _body.ColumnDefinitions.Add(new() { Width = new(228) });
        _body.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        Grid.SetRow(_body, 1); _root.Children.Add(_body);
        _sidebar.Padding = new(10, 12, 10, 12);
        _sidebar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _sidebar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _sidebar.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        _sidebar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _body.Children.Add(_sidebar);

        var workspaceRow = new Grid { Margin = new(0, 0, 0, 16) };
        workspaceRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        workspaceRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _workspacePicker.DisplayMemberPath = "Name";
        AutomationProperties.SetName(_workspacePicker, "Workspace");
        _workspacePicker.SelectionChanged += async (_, _) =>
        {
            if (_rendering || _workspacePicker.SelectedItem is not Workspace workspace) return;
            _session.State.ActiveWorkspaceId = workspace.Id; _splitTabId = null; _focusedTabId = null;
            await RefreshAsync(); QueueSave();
        };
        workspaceRow.Children.Add(_workspacePicker);
        _workspaceMenu = IconButton("\uE712", "Manage workspaces", () => ShowWorkspacesAsync());
        _workspaceMenu.Content = IconSlot(Icon("\uE712"));
        Grid.SetColumn(_workspaceMenu, 1); workspaceRow.Children.Add(_workspaceMenu);
        _sidebar.Children.Add(workspaceRow);

        var newTab = IconButton("\uE710", "New tab · Ctrl+T", () => NewTabAsync());
        newTab.HorizontalAlignment = HorizontalAlignment.Stretch;
        newTab.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        newTab.Padding = new(8, 0, 8, 0);
        newTab.Content = IconLabel("\uE710", "New tab");
        newTab.Tag = "new";
        Grid.SetRow(newTab, 1); _sidebar.Children.Add(newTab);
        var scroll = new ScrollViewer { Content = _tabList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new(0, 12, 0, 0) };
        Grid.SetRow(scroll, 2); _sidebar.Children.Add(scroll);
        var bottom = new StackPanel { Spacing = 4 };
        foreach (var (icon, label, action) in new (string, string, Func<Task>)[]
        {
            ("\uE721", "Command palette", () => ShowPaletteAsync()),
            ("\uE81C", "History", () => ShowHistoryAsync()),
            ("\uE896", "Downloads", () => ShowDownloadsAsync()),
            ("\uE713", "Settings", () => ShowSettingsAsync())
        })
        {
            var button = IconButton(icon, label, action); button.Content = IconLabel(icon, label);
            button.Tag = (icon, label); button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.Padding = new(8, 0, 8, 0);
            bottom.Children.Add(button);
        }
        bottom.Tag = "bottom";
        Grid.SetRow(bottom, 3); _sidebar.Children.Add(bottom);

        _content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _content.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        _content.RowDefinitions.Add(new() { Height = new(24) });
        _content.Margin = new(0, 0, 10, 0);
        Grid.SetColumn(_content, 1); _body.Children.Add(_content);
        var toolbar = new Grid { ColumnSpacing = 4, Padding = new(0, 4, 0, 8) };
        _collapse = IconButton("\uE700", "Toggle sidebar · Ctrl+B", ToggleSidebar);
        _back = IconButton("\uE72B", "Back · Alt+Left", () => CurrentCore()?.GoBack());
        _forward = IconButton("\uE72A", "Forward · Alt+Right", () => CurrentCore()?.GoForward());
        _reload = IconButton("\uE72C", "Reload · Ctrl+R", Reload);
        _security = IconButton("\uE946", "Site information and permissions", () => ShowSiteInfoAsync());
        var split = IconButton("\uE89F", "Split view · Ctrl+Shift+S", () => ToggleSplitAsync());
        var menu = IconButton("\uE712", "Browser menu", () => ShowPaletteAsync());
        _zoomBadge.Content = "100%";
        _zoomBadge.Height = 34;
        _zoomBadge.FontSize = 11;
        _zoomBadge.Padding = new(8, 0, 8, 0);
        _zoomBadge.Visibility = Visibility.Collapsed;
        _zoomBadge.Background = new SolidColorBrush(Colors.Transparent);
        _zoomBadge.BorderThickness = new(0);
        _zoomBadge.CornerRadius = new(SlateTheme.RadiusSmall);
        ToolTipService.SetToolTip(_zoomBadge, "Reset zoom · Ctrl+0");
        AutomationProperties.SetName(_zoomBadge, "Reset zoom");
        _zoomBadge.Click += (_, _) => ZoomReset();
        _bookmarkButton = IconButton("\uE734", "Bookmark this tab · Ctrl+D", () => ToggleBookmarkAsync());
        _downloadsButton = IconButton("\uE896", "Downloads · Ctrl+J", () => ShowDownloadsAsync());
        FrameworkElement[] toolbarItems = [_collapse, _back, _forward, _reload, _security, _address, _bookmarkButton, _zoomBadge, _downloadsButton, split, menu];
        for (int i = 0; i < toolbarItems.Length; i++)
        {
            toolbar.ColumnDefinitions.Add(new() { Width = toolbarItems[i] == _address ? new(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetColumn(toolbarItems[i], i);
            toolbar.Children.Add(toolbarItems[i]);
        }
        _address.VerticalContentAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(_address, "Address and search");
        _address.GotFocus += (_, _) =>
        {
            _addressEditing = true;
            _address.BorderBrush = _theme.AccentBorderBrush;
            _address.SelectAll();
            if (!string.IsNullOrWhiteSpace(_address.Text)) UpdateSuggestions(_address.Text);
        };
        _address.LostFocus += (_, _) =>
        {
            _addressEditing = false;
            _address.BorderBrush = _theme.DividerBrush;
            UpdateChrome();
            DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(150);
                CloseSuggestions();
            });
        };
        _address.TextChanged += (_, _) =>
        {
            if (_addressEditing) UpdateSuggestions(_address.Text);
        };
        _address.KeyDown += async (_, e) =>
        {
            if (e.Key == VirtualKey.Down)
            {
                if (_currentSuggestions.Count > 0)
                {
                    e.Handled = true;
                    _selectedSuggestionIndex = (_selectedSuggestionIndex + 1) % _currentSuggestions.Count;
                    HighlightSuggestion(_selectedSuggestionIndex);
                }
            }
            else if (e.Key == VirtualKey.Up)
            {
                if (_currentSuggestions.Count > 0)
                {
                    e.Handled = true;
                    _selectedSuggestionIndex = (_selectedSuggestionIndex - 1 + _currentSuggestions.Count) % _currentSuggestions.Count;
                    HighlightSuggestion(_selectedSuggestionIndex);
                }
            }
            else if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                if (_selectedSuggestionIndex >= 0 && _selectedSuggestionIndex < _currentSuggestions.Count)
                {
                    var selected = _currentSuggestions[_selectedSuggestionIndex];
                    CloseSuggestions();
                    await ExecuteSuggestionAsync(selected);
                }
                else
                {
                    CloseSuggestions();
                    await NavigateAsync(_address.Text);
                }
            }
            else if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                if (_suggestionsPopup.IsOpen)
                {
                    CloseSuggestions();
                }
                else
                {
                    _addressEditing = false;
                    UpdateChrome();
                    FocusPage();
                }
            }
        };
        _suggestionsBorder.CornerRadius = new(SlateTheme.RadiusMedium);
        _suggestionsBorder.BorderThickness = new(1);
        _suggestionsBorder.Padding = new(4);
        _suggestionsBorder.MaxHeight = 280;
        _suggestionsBorder.Child = new ScrollViewer
        {
            Content = _suggestionsList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        _suggestionsPopup.Child = _suggestionsBorder;
        _suggestionsPopup.IsLightDismissEnabled = false;
        _root.Children.Add(_suggestionsPopup);
        _bookmarksBar.Content = _bookmarksList;
        var toolbarContainer = new StackPanel { Spacing = 2 };
        toolbarContainer.Children.Add(toolbar);
        toolbarContainer.Children.Add(_bookmarksBar);
        _toolbarSurface.Child = toolbarContainer; _content.Children.Add(_toolbarSurface);
        _panes.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        _panes.ColumnDefinitions.Add(new() { Width = new(0) });
        _panes.ColumnSpacing = 0;
        _panes.Children.Add(_primary); Grid.SetColumn(_secondary, 1); _panes.Children.Add(_secondary);
        _primary.GotFocus += (_, _) => { _focusedTabId = _session.ActiveTab.Id; UpdateChrome(); };
        _secondary.GotFocus += (_, _) => { _focusedTabId = _splitTabId; UpdateChrome(); };

        _findInput.Width = 150;
        _findInput.Height = 28;
        _findInput.FontSize = 12;
        _findInput.PlaceholderText = "Find in page";
        _findInput.Padding = new(6, 2, 6, 2);
        _findInput.BorderThickness = new(0);
        _findInput.Background = new SolidColorBrush(Colors.Transparent);
        _findInput.VerticalContentAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(_findInput, "Find in page");
        _findInput.TextChanged += async (_, _) => await StartFindAsync(_findInput.Text);
        _findInput.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
                if (shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) FindPrevious();
                else FindNext();
            }
            else if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                CloseFindBar();
            }
        };
        _findStatus.FontSize = 11;
        _findStatus.VerticalAlignment = VerticalAlignment.Center;
        _findStatus.Margin = new(4, 0, 4, 0);
        _findPrev = IconButton("\uE74B", "Previous match · Shift+F3", FindPrevious);
        _findPrev.Width = 28; _findPrev.MinWidth = 28; _findPrev.Height = 28; _findPrev.Padding = new(4);
        _findNext = IconButton("\uE74A", "Next match · F3 / Enter", FindNext);
        _findNext.Width = 28; _findNext.MinWidth = 28; _findNext.Height = 28; _findNext.Padding = new(4);
        _findClose = IconButton("\uE711", "Close · Esc", CloseFindBar);
        _findClose.Width = 28; _findClose.MinWidth = 28; _findClose.Height = 28; _findClose.Padding = new(4);

        var findStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        findStack.Children.Add(_findInput);
        findStack.Children.Add(_findStatus);
        findStack.Children.Add(_findPrev);
        findStack.Children.Add(_findNext);
        findStack.Children.Add(_findClose);

        _findBar.Child = findStack;
        _findBar.HorizontalAlignment = HorizontalAlignment.Right;
        _findBar.VerticalAlignment = VerticalAlignment.Top;
        _findBar.Margin = new(0, 8, 16, 0);
        _findBar.CornerRadius = new(SlateTheme.RadiusMedium);
        _findBar.BorderThickness = new(1);
        _findBar.Padding = new(4, 2, 4, 2);
        _findBar.Visibility = Visibility.Collapsed;

        var pageContainer = new Grid();
        pageContainer.Children.Add(_panes);
        pageContainer.Children.Add(_findBar);
        _pageFrame.Child = pageContainer;
        Grid.SetRow(_pageFrame, 2); _content.Children.Add(_pageFrame);
        Grid.SetRow(_progress, 2); _content.Children.Add(_progress);
        _status.Margin = new(8, 3, 8, 0); Grid.SetRow(_status, 3); _content.Children.Add(_status);
        _root.Children.Add(_parkedViews);
    }

    private void ConfigureWindow()
    {
        var placement = _session.State.Window;
        AppWindow.Resize(new SizeInt32(Math.Clamp(placement.Width, 760, 4000), Math.Clamp(placement.Height, 500, 2400)));
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        if (placement.X is int x && placement.Y is int y)
            AppWindow.Move(new PointInt32(Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - 760)), Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - 500))));
        if (placement.Maximized && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        AppWindow.Changed += (_, args) =>
        {
            if (!_ready || _closing) return;
            if (AppWindow.Presenter is OverlappedPresenter p)
            {
                placement.Maximized = p.State == OverlappedPresenterState.Maximized;
                if (p.State == OverlappedPresenterState.Restored && (args.DidSizeChange || args.DidPositionChange))
                {
                    placement.Width = AppWindow.Size.Width; placement.Height = AppWindow.Size.Height;
                    placement.X = AppWindow.Position.X; placement.Y = AppWindow.Position.Y;
                }
                QueueSave();
            }
        };
    }

    private void ApplyAppearance()
    {
        _root.RequestedTheme = _session.State.Settings.Theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        UpdateThemeColors();
        _root.ActualThemeChanged -= OnThemeChanged; _root.ActualThemeChanged += OnThemeChanged;
    }

    private void OnThemeChanged(FrameworkElement sender, object args) => UpdateThemeColors();
    private void OnSystemColorValuesChanged(UISettings sender, object args)
    {
        if (_session.State.Settings.AccentTheme == "System") DispatcherQueue.TryEnqueue(UpdateThemeColors);
    }

    private void UpdateThemeColors()
    {
        if (_closing) return;
        var dark = _root.ActualTheme == ElementTheme.Dark;
        _theme = SlateTheme.Create(_session.State.Settings.AccentTheme, dark);
        SlateTheme.ApplyResources(_root.Resources, _theme);
        _root.Background = _theme.ChromeBrush;
        _sidebar.Background = _theme.SidebarBrush;
        _panes.Background = _theme.SurfaceBaseBrush;
        _pageFrame.Background = _theme.SurfaceBaseBrush;
        _pageFrame.BorderBrush = _theme.DividerBrush;
        _address.Background = _theme.SurfaceInteractiveBrush;
        _address.BorderBrush = _addressEditing ? _theme.AccentBorderBrush : _theme.DividerBrush;
        _findBar.Background = _theme.SurfaceRaisedBrush;
        _findBar.BorderBrush = _theme.DividerBrush;
        _findInput.Foreground = _theme.TextPrimaryBrush;
        _findStatus.Foreground = _theme.TextSecondaryBrush;
        _zoomBadge.Foreground = _theme.TextPrimaryBrush;
        _zoomBadge.BorderBrush = _theme.DividerBrush;
        _suggestionsBorder.Background = _theme.SurfaceRaisedBrush;
        _suggestionsBorder.BorderBrush = _theme.DividerBrush;
        UpdateDownloadIndicator();
        UpdateBookmarkIndicator();
        RenderBookmarksBar();
        _brand.Foreground = _theme.AccentPrimaryBrush;
        _brandRing.Stroke = _theme.AccentPrimaryBrush;
        _brandDot.Fill = _theme.AccentPrimaryBrush;
        _progress.Foreground = _theme.AccentPrimaryBrush;
        AppWindow.TitleBar.ButtonForegroundColor = _theme.TextPrimary;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = _theme.TextMuted;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = SlateTheme.WithAlpha(_theme.AccentPrimary, dark ? (byte)38 : (byte)26);
        AppWindow.TitleBar.ButtonPressedBackgroundColor = SlateTheme.WithAlpha(_theme.AccentPrimary, dark ? (byte)58 : (byte)42);
        RenderSidebar();
        foreach (var runtime in _runtimes.Values.Where(r => r.View.CoreWebView2 is not null))
            SetWebTheme(runtime.View.CoreWebView2);
    }

    private static FontIcon Icon(string glyph, double size = 14) => new() { Glyph = glyph, FontFamily = new("Segoe Fluent Icons"), FontSize = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private static Grid IconSlot(UIElement icon, double slotSize = 20) => new() { Width = slotSize, Height = slotSize, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { icon } };
    private static Grid IconLabel(string glyph, string label, double slotSize = 20, double spacing = 10)
    {
        var grid = new Grid { ColumnSpacing = spacing, HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new() { Width = new(slotSize) });
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var icon = IconSlot(Icon(glyph), slotSize);
        var text = new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }
    private Button IconButton(string glyph, string label, Action action)
    {
        var button = new Button { Content = Icon(glyph), MinWidth = 34, Height = 34, Padding = new(8, 0, 8, 0), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new(0), CornerRadius = new(SlateTheme.RadiusSmall) };
        ToolTipService.SetToolTip(button, label); AutomationProperties.SetName(button, label);
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { Notify(ex.Message); } }; return button;
    }
    private Button IconButton(string glyph, string label, Func<Task> action) => IconButton(glyph, label, (Action)(async () => { try { await action(); } catch (Exception ex) { Notify(ex.Message); } }));

    private void RenderSidebar()
    {
        _rendering = true;
        _body.ColumnDefinitions[0].Width = new(Collapsed ? 64 : 228);
        _workspacePicker.ItemsSource = null; _workspacePicker.ItemsSource = _session.State.Workspaces;
        _workspacePicker.SelectedItem = _session.ActiveWorkspace;
        _workspacePicker.Visibility = Collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (Collapsed)
        {
            Grid.SetColumn(_workspaceMenu, 0);
            Grid.SetColumnSpan(_workspaceMenu, 2);
            _workspaceMenu.HorizontalAlignment = HorizontalAlignment.Stretch;
            _workspaceMenu.HorizontalContentAlignment = HorizontalAlignment.Center;
            _workspaceMenu.Padding = new(0);
            _workspaceMenu.Content = IconSlot(Icon("\uE712"));
        }
        else
        {
            Grid.SetColumn(_workspaceMenu, 1);
            Grid.SetColumnSpan(_workspaceMenu, 1);
            _workspaceMenu.HorizontalAlignment = HorizontalAlignment.Right;
            _workspaceMenu.HorizontalContentAlignment = HorizontalAlignment.Center;
            _workspaceMenu.Padding = new(8, 0, 8, 0);
            _workspaceMenu.Content = IconSlot(Icon("\uE712"));
        }
        foreach (var element in _sidebar.Children)
        {
            if (element is Button b && b.Tag is "new")
            {
                b.HorizontalAlignment = HorizontalAlignment.Stretch;
                b.HorizontalContentAlignment = Collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
                b.Padding = Collapsed ? new(0) : new(8, 0, 8, 0);
                b.Content = Collapsed ? IconSlot(Icon("\uE710")) : IconLabel("\uE710", "New tab");
            }
            if (element is StackPanel panel && panel.Tag is "bottom")
            {
                foreach (var button in panel.Children.OfType<Button>())
                {
                    if (button.Tag is ValueTuple<string, string> meta)
                    {
                        button.HorizontalAlignment = HorizontalAlignment.Stretch;
                        button.HorizontalContentAlignment = Collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
                        button.Padding = Collapsed ? new(0) : new(8, 0, 8, 0);
                        button.Content = Collapsed ? IconSlot(Icon(meta.Item1)) : IconLabel(meta.Item1, meta.Item2);
                    }
                }
            }
        }
        _tabList.Children.Clear();
        bool regularHeader = false, pinnedHeader = false;
        foreach (var tab in _session.VisibleTabs)
        {
            if (tab.IsPinned && !pinnedHeader) { AddTabHeading("PINNED"); pinnedHeader = true; }
            if (!tab.IsPinned && !regularHeader) { AddTabHeading("TABS"); regularHeader = true; }
            var row = new Grid { CornerRadius = new(SlateTheme.RadiusSmall), Height = 38 };
            bool active = tab.Id == _session.ActiveTab.Id;
            row.Background = active ? _theme.SurfaceSelectedBrush : new SolidColorBrush(Colors.Transparent);
            row.PointerEntered += (_, _) => row.Background = active ? _theme.AccentSoftHoverBrush : _theme.SurfaceInteractiveHoverBrush;
            row.PointerExited += (_, _) => row.Background = active ? _theme.SurfaceSelectedBrush : new SolidColorBrush(Colors.Transparent);
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var button = IconButton(tab.Url == Navigation.NewTab ? "\uE710" : "\uE774", tab.Title, () => ActivateTabAsync(tab.Id));
            button.Height = 38; button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = Collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            button.Padding = Collapsed ? new(0) : new(8, 0, 8, 0);
            var tabContent = new Grid { ColumnSpacing = 10, HorizontalAlignment = Collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Stretch };
            tabContent.ColumnDefinitions.Add(new() { Width = new(20) });
            if (!Collapsed) tabContent.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            var iconBox = new Grid { Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var expectedKey = FaviconStore.GetKey(tab.Url);
            iconBox.Tag = expectedKey;
            UIElement initialFavicon;
            if (tab.IsPrivate && tab.Url == Navigation.NewTab)
            {
                initialFavicon = Icon("\uE727");
            }
            else if (tab.Url == Navigation.NewTab)
            {
                initialFavicon = Icon("\uE710");
            }
            else if (_runtimes.GetValueOrDefault(tab.Id)?.FaviconImage is { } image)
            {
                initialFavicon = new Image { Source = image, Width = 16, Height = 16, Opacity = tab.IsSleeping ? .5 : 1.0, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            }
            else if (_uiFaviconCache.TryGetValue(expectedKey, out var cached))
            {
                initialFavicon = new Image { Source = cached, Width = 16, Height = 16, Opacity = tab.IsSleeping ? .5 : 1.0, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            }
            else
            {
                initialFavicon = Icon(tab.IsPrivate ? "\uE727" : tab.IsSleeping ? "\uE708" : tab.IsPinned ? "\uE718" : "\uE774");
                var tabId = tab.Id;
                var isSleeping = tab.IsSleeping;
                _ = AttachFaviconAsync(iconBox, tab.Url, expectedKey, () => _session.State.Tabs.Any(t => t.Id == tabId), isSleeping ? .5 : 1.0, 16);
            }
            if (tab.IsLoading) initialFavicon = new ProgressRing { Width = 16, Height = 16, IsActive = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            iconBox.Children.Add(initialFavicon);
            tabContent.Children.Add(iconBox);
            if (!Collapsed)
            {
                var label = new TextBlock { Text = tab.Title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Opacity = tab.IsSleeping ? .5 : 1,
                    FontStyle = tab.IsTemporary ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal };
                if (tab.IsPrivate) label.Foreground = _theme.AccentPrimaryBrush;
                if (active) label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                Grid.SetColumn(label, 1); tabContent.Children.Add(label);
            }
            button.Content = tabContent;
            button.ContextFlyout = TabMenu(tab);
            row.CanDrag = true;
            row.DragStarting += (_, args) =>
            {
                args.Data.Properties.Add("TabId", tab.Id);
                args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
            };
            row.AllowDrop = true;
            row.DragOver += (_, args) =>
            {
                if (args.DataView.Properties.ContainsKey("TabId"))
                {
                    args.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                    args.DragUIOverride.IsCaptionVisible = false;
                }
            };
            row.Drop += async (_, args) =>
            {
                if (args.DataView.Properties.TryGetValue("TabId", out var sObj) && sObj is Guid sId && sId != tab.Id)
                {
                    _session.ReorderTab(sId, tab.Id);
                    await RefreshAsync();
                    QueueSave();
                }
            };
            row.Children.Add(button);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            if (_runtimes.TryGetValue(tab.Id, out var tabRuntime) && tabRuntime.View.CoreWebView2 is { } tabCore && (tabCore.IsDocumentPlayingAudio || tabCore.IsMuted))
            {
                var audioIcon = tabCore.IsMuted ? "\uE74F" : "\uE767";
                var audioLabel = tabCore.IsMuted ? "Unmute tab" : "Mute tab";
                var audioBtn = IconButton(audioIcon, audioLabel, () =>
                {
                    tabCore.IsMuted = !tabCore.IsMuted;
                    RenderSidebar();
                });
                audioBtn.MinWidth = 26; audioBtn.Width = 26; audioBtn.Padding = new(4);
                audioBtn.Foreground = tabCore.IsMuted ? _theme.TextMutedBrush : _theme.AccentPrimaryBrush;
                actions.Children.Add(audioBtn);
            }
            if (!Collapsed && !tab.IsPinned)
            {
                var close = IconButton("\uE711", "Close " + tab.Title, () => CloseTabAsync(tab.Id));
                close.MinWidth = 26; close.Width = 26; close.Padding = new(4); close.Opacity = active ? .8 : .25;
                row.PointerEntered += (_, _) => close.Opacity = 1;
                row.PointerExited += (_, _) => close.Opacity = active ? .8 : .25;
                actions.Children.Add(close);
            }
            if (actions.Children.Count > 0)
            {
                Grid.SetColumn(actions, 1);
                row.Children.Add(actions);
            }
            _tabList.Children.Add(row);
        }
        _rendering = false;
    }

    private void AddTabHeading(string title)
    {
        if (!Collapsed) _tabList.Children.Add(new TextBlock { Text = title, FontSize = 9, CharacterSpacing = 160, Opacity = .4, Margin = new(9, 10, 0, 6) });
    }

    private MenuFlyout TabMenu(BrowserTab tab)
    {
        var menu = new MenuFlyout();
        void Add(string text, Func<Task> action) { var item = new MenuFlyoutItem { Text = text }; item.Click += async (_, _) => await RunAsync(action); menu.Items.Add(item); }
        Add(tab.IsPinned ? "Unpin tab" : "Pin tab", async () => { _session.Pin(tab.Id); await RefreshAsync(); QueueSave(); });
        Add("Reload tab", () => { ReloadTab(tab.Id); return Task.CompletedTask; });
        Add("Duplicate tab", () => DuplicateTabAsync(tab.Id));
        Add("View page source", () => ViewSourceAsync(tab.Url));
        Add("Move tab up", () => MoveTabAsync(tab.Id, -1));
        Add("Move tab down", () => MoveTabAsync(tab.Id, 1));
        Add("Open beside current tab", async () => { if (tab.Id != _session.ActiveTab.Id) { _splitTabId = tab.Id; await RefreshAsync(); } });
        if (_runtimes.TryGetValue(tab.Id, out var tr) && tr.View.CoreWebView2 is { } tc)
        {
            Add(tc.IsMuted ? "Unmute tab" : "Mute tab", () => { tc.IsMuted = !tc.IsMuted; RenderSidebar(); return Task.CompletedTask; });
        }
        Add("Put tab to sleep", () => SleepTabAsync(tab.Id, true));
        var move = new MenuFlyoutSubItem { Text = "Move to workspace" };
        foreach (var workspace in _session.State.Workspaces.Where(w => w.Id != tab.WorkspaceId))
        {
            var item = new MenuFlyoutItem { Text = workspace.Name };
            item.Click += async (_, _) => await RunAsync(async () =>
            {
                var old = _session.ActiveWorkspace;
                bool needsReplacement = _session.State.Tabs.Count(t => t.WorkspaceId == old.Id) == 1;
                if (needsReplacement && _session.State.Tabs.Count >= BrowserSession.MaximumTabs)
                {
                    Notify($"Close a tab before moving the only tab in this workspace. Slate supports up to {BrowserSession.MaximumTabs} open tabs.");
                    return;
                }
                tab.WorkspaceId = workspace.Id;
                if (old.ActiveTabId == tab.Id)
                {
                    var next = _session.State.Tabs.FirstOrDefault(t => t.WorkspaceId == old.Id);
                    if (next is null) _session.AddTab(); else _session.Activate(next.Id);
                }
                _splitTabId = null; _focusedTabId = null; await RefreshAsync(); QueueSave();
            });
            move.Items.Add(item);
        }
        menu.Items.Add(move);
        if (tab.IsTemporary) Add("Keep tab", () => { tab.IsTemporary = false; RenderSidebar(); QueueSave(); return Task.CompletedTask; });
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Close other tabs", () => CloseOtherTabsAsync(tab.Id));
        Add("Close tabs below", () => CloseTabsBelowAsync(tab.Id));
        Add("Close tab", () => CloseTabAsync(tab.Id));
        return menu;
    }

    private void ToggleSidebar()
    {
        var startWidth = _body.ColumnDefinitions[0].ActualWidth;
        _session.State.Settings.SidebarCollapsed = !Collapsed;
        RenderSidebar(); Animate(_sidebar, true); QueueSave();
        _sidebarAnimation?.Stop();
        if (_session.State.Settings.ReduceMotion || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        double target = Collapsed ? 64 : 228;
        var start = System.Diagnostics.Stopwatch.StartNew();
        _body.ColumnDefinitions[0].Width = new(startWidth);
        _sidebarAnimation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _sidebarAnimation.Tick += (_, _) =>
        {
            double t = Math.Min(1, start.Elapsed.TotalMilliseconds / 180);
            _body.ColumnDefinitions[0].Width = new(startWidth + (target - startWidth) * (1 - Math.Pow(1 - t, 3)));
            if (t >= 1) _sidebarAnimation.Stop();
        };
        _sidebarAnimation.Start();
    }

    private void Animate(UIElement element, bool slide = false)
    {
        if (_session.State.Settings.ReduceMotion || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation(); fade.InsertKeyFrame(0, .5f); fade.InsertKeyFrame(1, 1); fade.Duration = TimeSpan.FromMilliseconds(160); visual.StartAnimation("Opacity", fade);
        if (slide)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            var animation = visual.Compositor.CreateVector3KeyFrameAnimation(); animation.InsertKeyFrame(0, new Vector3(-8, 0, 0)); animation.InsertKeyFrame(1, Vector3.Zero); animation.Duration = TimeSpan.FromMilliseconds(160); visual.StartAnimation("Translation", animation);
        }
    }

    private void AddShortcuts()
    {
        Task Sync(Action action) { action(); return Task.CompletedTask; }
        Task SelectTabByIndexAsync(int index)
        {
            var tabs = _session.VisibleTabs.ToList();
            if (tabs.Count == 0) return Task.CompletedTask;
            int target = index == -1 ? tabs.Count - 1 : Math.Min(index, tabs.Count - 1);
            return ActivateTabAsync(tabs[target].Id);
        }

        _commandRouter.RegisterAction(BrowserCommand.FocusAddress, () => Sync(FocusAddress));
        _commandRouter.RegisterAction(BrowserCommand.NewTab, () => NewTabAsync());
        _commandRouter.RegisterAction(BrowserCommand.RestoreClosedTab, () => RestoreClosedAsync());
        _commandRouter.RegisterAction(BrowserCommand.CloseTab, () => CloseTabAsync(FocusedTab.Id));
        _commandRouter.RegisterAction(BrowserCommand.NextTab, () => CycleTabAsync(1));
        _commandRouter.RegisterAction(BrowserCommand.PreviousTab, () => CycleTabAsync(-1));
        _commandRouter.RegisterAction(BrowserCommand.GoBack, () => Sync(() => { if (CurrentCore()?.CanGoBack == true) CurrentCore()!.GoBack(); }));
        _commandRouter.RegisterAction(BrowserCommand.GoForward, () => Sync(() => { if (CurrentCore()?.CanGoForward == true) CurrentCore()!.GoForward(); }));
        _commandRouter.RegisterAction(BrowserCommand.Reload, () => Sync(Reload));
        _commandRouter.RegisterAction(BrowserCommand.ToggleSidebar, () => Sync(ToggleSidebar));
        _commandRouter.RegisterAction(BrowserCommand.CommandPalette, () => ShowPaletteAsync());
        _commandRouter.RegisterAction(BrowserCommand.History, () => ShowHistoryAsync());
        _commandRouter.RegisterAction(BrowserCommand.Downloads, () => ShowDownloadsAsync());
        _commandRouter.RegisterAction(BrowserCommand.Settings, () => ShowSettingsAsync());
        _commandRouter.RegisterAction(BrowserCommand.ToggleSplitView, () => ToggleSplitAsync());
        _commandRouter.RegisterAction(BrowserCommand.NewTemporaryTab, () => NewTabAsync(Navigation.NewTab, true));
        _commandRouter.RegisterAction(BrowserCommand.SelectTab1, () => SelectTabByIndexAsync(0));
        _commandRouter.RegisterAction(BrowserCommand.SelectTab2, () => SelectTabByIndexAsync(1));
        _commandRouter.RegisterAction(BrowserCommand.SelectTab3, () => SelectTabByIndexAsync(2));
        _commandRouter.RegisterAction(BrowserCommand.SelectTab4, () => SelectTabByIndexAsync(3));
        _commandRouter.RegisterAction(BrowserCommand.SelectTab5, () => SelectTabByIndexAsync(4));
        _commandRouter.RegisterAction(BrowserCommand.SelectTab6, () => SelectTabByIndexAsync(5));
        _commandRouter.RegisterAction(BrowserCommand.SelectTab7, () => SelectTabByIndexAsync(6));
        _commandRouter.RegisterAction(BrowserCommand.SelectTab8, () => SelectTabByIndexAsync(7));
        _commandRouter.RegisterAction(BrowserCommand.SelectLastTab, () => SelectTabByIndexAsync(-1));
        _commandRouter.RegisterAction(BrowserCommand.FindInPage, () => Sync(OpenFindBar));
        _commandRouter.RegisterAction(BrowserCommand.FindNext, () => Sync(FindNext));
        _commandRouter.RegisterAction(BrowserCommand.FindPrevious, () => Sync(FindPrevious));
        _commandRouter.RegisterAction(BrowserCommand.ZoomIn, () => Sync(ZoomIn));
        _commandRouter.RegisterAction(BrowserCommand.ZoomOut, () => Sync(ZoomOut));
        _commandRouter.RegisterAction(BrowserCommand.ZoomReset, () => Sync(ZoomReset));
        _commandRouter.RegisterAction(BrowserCommand.ToggleFullScreen, () => Sync(() => ToggleFullScreen()));
        _commandRouter.RegisterAction(BrowserCommand.Print, () => Sync(PrintPage));
        _commandRouter.RegisterAction(BrowserCommand.HardReload, HardReloadAsync);
        _commandRouter.RegisterAction(BrowserCommand.OpenDevTools, () => Sync(OpenDevTools));
        _commandRouter.RegisterAction(BrowserCommand.StopLoading, () => Sync(StopLoading));
        _commandRouter.RegisterAction(BrowserCommand.MoveTabUp, () => MoveTabAsync(FocusedTab.Id, -1));
        _commandRouter.RegisterAction(BrowserCommand.MoveTabDown, () => MoveTabAsync(FocusedTab.Id, 1));
        _commandRouter.RegisterAction(BrowserCommand.DuplicateTab, () => DuplicateTabAsync(FocusedTab.Id));
        _commandRouter.RegisterAction(BrowserCommand.CloseOtherTabs, () => CloseOtherTabsAsync(FocusedTab.Id));
        _commandRouter.RegisterAction(BrowserCommand.CloseTabsBelow, () => CloseTabsBelowAsync(FocusedTab.Id));
        _commandRouter.RegisterAction(BrowserCommand.BookmarkPage, () => ToggleBookmarkAsync());
        _commandRouter.RegisterAction(BrowserCommand.OpenBookmarks, () => ShowBookmarksAsync());
        _commandRouter.RegisterAction(BrowserCommand.NewPrivateTab, () => NewTabAsync(Navigation.NewTab, isPrivate: true));
        _commandRouter.RegisterAction(BrowserCommand.OpenFile, OpenLocalFileAsync);
        _commandRouter.RegisterAction(BrowserCommand.SavePage, SavePageAsync);
        _commandRouter.RegisterAction(BrowserCommand.ViewSource, () => ViewSourceAsync());

        _root.KeyboardAccelerators.Clear();
        _root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        foreach (var def in BrowserCommandRegistry.AllShortcuts)
        {
            var key = (VirtualKey)def.VirtualKey;
            var mods = VirtualKeyModifiers.None;
            if (def.Modifiers.HasFlag(ShortcutModifiers.Control)) mods |= VirtualKeyModifiers.Control;
            if (def.Modifiers.HasFlag(ShortcutModifiers.Alt)) mods |= VirtualKeyModifiers.Menu;
            if (def.Modifiers.HasFlag(ShortcutModifiers.Shift)) mods |= VirtualKeyModifiers.Shift;

            var shortcut = new KeyboardAccelerator { Key = key, Modifiers = mods };
            var cmd = def.Command;
            shortcut.Invoked += async (_, e) =>
            {
                if (_commandRouter.CanRoute((int)key, def.Modifiers))
                {
                    e.Handled = true;
                    await _commandRouter.RouteCommandAsync(cmd);
                }
            };
            _root.KeyboardAccelerators.Add(shortcut);
        }
    }

    private async Task RunAsync(Func<Task> action) { try { await action(); } catch (Exception ex) { Notify(ex.Message); } }
    private void Notify(string message) { _status.Text = message; }
    private void QueueSave() { if (!_closing) { _saveTimer.Stop(); _saveTimer.Start(); } }
    private void Save() { if (!_store.Save(_session.State)) Notify(_store.LastError ?? "Session could not be saved."); }
    private void FocusAddress() { _address.Focus(FocusState.Programmatic); _address.SelectAll(); }
    private void FocusPage() { if (_runtimes.TryGetValue(FocusedTab.Id, out var runtime)) runtime.View.Focus(FocusState.Programmatic); else _primary.Focus(FocusState.Programmatic); }
    private BitmapImage? GetCachedFavicon(string urlOrOrigin)
    {
        var key = FaviconStore.GetKey(urlOrOrigin);
        return !string.IsNullOrEmpty(key) && _uiFaviconCache.TryGetValue(key, out var image) ? image : null;
    }

    private Task<BitmapImage?> GetOrLoadFaviconAsync(string urlOrOrigin)
    {
        var key = FaviconStore.GetKey(urlOrOrigin);
        if (string.IsNullOrEmpty(key)) return Task.FromResult<BitmapImage?>(null);

        if (_uiFaviconCache.TryGetValue(key, out var cached))
            return Task.FromResult<BitmapImage?>(cached);

        if (_inFlightFaviconLoads.TryGetValue(key, out var inFlight))
            return inFlight;

        int currentGen = _uiFaviconGeneration;
        var task = LoadAndDecodeFaviconAsync(key, urlOrOrigin, currentGen);
        _inFlightFaviconLoads[key] = task;
        return task;
    }

    private async Task<BitmapImage?> LoadAndDecodeFaviconAsync(string key, string urlOrOrigin, int capturedGen)
    {
        try
        {
            var bytes = await _faviconStore.GetFaviconBytesAsync(urlOrOrigin);
            if (capturedGen != _uiFaviconGeneration || bytes is null || !FaviconStore.IsValidPng(bytes))
                return null;

            using var ms = new MemoryStream(bytes);
            using var stream = ms.AsRandomAccessStream();
            var bitmap = new BitmapImage { DecodePixelWidth = 32 };
            await bitmap.SetSourceAsync(stream);

            if (capturedGen != _uiFaviconGeneration)
                return null;

            _uiFaviconCache[key] = bitmap;
            return bitmap;
        }
        catch
        {
            return null;
        }
        finally
        {
            _inFlightFaviconLoads.Remove(key);
        }
    }

    private async Task AttachFaviconAsync(Grid targetContainer, string urlOrOrigin, string expectedKey, Func<bool> isStillValid, double opacity, double size)
    {
        var image = await GetOrLoadFaviconAsync(urlOrOrigin);
        if (image is null) return;
        if (!Equals(targetContainer.Tag, expectedKey) || !isStillValid()) return;
        targetContainer.Children.Clear();
        targetContainer.Children.Add(new Image
        {
            Source = image,
            Width = size,
            Height = size,
            Opacity = opacity,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
    }

    private void UpdateSuggestions(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            CloseSuggestions();
            return;
        }

        _currentSuggestions = OmniboxService.GetSuggestions(
            text,
            _session.State.Tabs,
            _session.State.History,
            _session.State.Settings.SearchEngine,
            _session.State.Bookmarks,
            maxSuggestions: 6);

        if (_currentSuggestions.Count == 0)
        {
            CloseSuggestions();
            return;
        }

        _selectedSuggestionIndex = -1;
        RenderSuggestions();

        try
        {
            if (_root.XamlRoot is not null)
                _suggestionsPopup.XamlRoot = _root.XamlRoot;

            var transform = _address.TransformToVisual(_root);
            var point = transform.TransformPoint(new Windows.Foundation.Point(0, _address.ActualHeight + 4));
            _suggestionsPopup.HorizontalOffset = point.X;
            _suggestionsPopup.VerticalOffset = point.Y;
            _suggestionsBorder.Width = Math.Max(320, _address.ActualWidth);
            _suggestionsPopup.IsOpen = true;
        }
        catch
        {
            // View transform might not be ready yet
        }
    }

    private void CloseSuggestions()
    {
        _suggestionsPopup.IsOpen = false;
        _currentSuggestions.Clear();
        _selectedSuggestionIndex = -1;
        _suggestionsList.Children.Clear();
    }

    private void RenderSuggestions()
    {
        _suggestionsList.Children.Clear();
        for (int i = 0; i < _currentSuggestions.Count; i++)
        {
            _suggestionsList.Children.Add(CreateSuggestionItem(_currentSuggestions[i], i));
        }
    }

    private FrameworkElement CreateSuggestionItem(OmniboxSuggestion suggestion, int index)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = (index == _selectedSuggestionIndex) ? _theme.SurfaceInteractiveBrush : new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(SlateTheme.RadiusSmall),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 1, 0, 1)
        };

        var grid = new Grid { ColumnSpacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        string glyph = suggestion.Kind switch
        {
            OmniboxSuggestionKind.OpenTab => "\uE737",
            OmniboxSuggestionKind.Bookmark => "\uE735",
            OmniboxSuggestionKind.History => "\uE81C",
            _ => "\uE721"
        };

        var icon = IconSlot(Icon(glyph, 13));
        icon.Opacity = 0.75;
        grid.Children.Add(icon);

        var textStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock
        {
            Text = suggestion.Title,
            FontSize = 12,
            Foreground = _theme.TextPrimaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var subtitle = new TextBlock
        {
            Text = suggestion.Subtitle,
            FontSize = 10.5,
            Foreground = _theme.TextSecondaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        textStack.Children.Add(title);
        textStack.Children.Add(subtitle);
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        if (suggestion.Kind == OmniboxSuggestionKind.OpenTab)
        {
            var badge = new TextBlock
            {
                Text = "Switch to tab",
                FontSize = 10,
                Foreground = _theme.AccentPrimaryBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0)
            };
            Grid.SetColumn(badge, 2);
            grid.Children.Add(badge);
        }
        else if (suggestion.Kind == OmniboxSuggestionKind.Bookmark)
        {
            var badge = new TextBlock
            {
                Text = "Bookmark",
                FontSize = 10,
                Foreground = _theme.AccentPrimaryBrush,
                Opacity = 0.9,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0)
            };
            Grid.SetColumn(badge, 2);
            grid.Children.Add(badge);
        }
        else if (suggestion.Kind == OmniboxSuggestionKind.History)
        {
            var badge = new TextBlock
            {
                Text = "History",
                FontSize = 10,
                Foreground = _theme.TextSecondaryBrush,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0)
            };
            Grid.SetColumn(badge, 2);
            grid.Children.Add(badge);
        }

        button.Content = grid;
        button.Click += async (_, _) =>
        {
            CloseSuggestions();
            await ExecuteSuggestionAsync(suggestion);
        };

        return button;
    }

    private void HighlightSuggestion(int index)
    {
        for (int i = 0; i < _suggestionsList.Children.Count; i++)
        {
            if (_suggestionsList.Children[i] is Button btn)
            {
                btn.Background = (i == index)
                    ? _theme.SurfaceInteractiveBrush
                    : new SolidColorBrush(Colors.Transparent);
            }
        }
    }

    private async Task ExecuteSuggestionAsync(OmniboxSuggestion suggestion)
    {
        if (suggestion.Kind == OmniboxSuggestionKind.OpenTab && suggestion.TabId.HasValue)
        {
            var existingTab = _session.State.Tabs.FirstOrDefault(t => t.Id == suggestion.TabId.Value);
            if (existingTab is not null)
            {
                await ActivateTabAsync(existingTab.Id);
                FocusPage();
                return;
            }
        }

        await NavigateAsync(suggestion.Target);
        FocusPage();
    }

    private void RenderBookmarksBar()
    {
        if (!_session.State.Settings.ShowBookmarksBar)
        {
            _bookmarksBar.Visibility = Visibility.Collapsed;
            return;
        }

        _bookmarksBar.Visibility = Visibility.Visible;
        _bookmarksList.Children.Clear();

        if (_session.State.Bookmarks.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = "Bookmarks bar is empty. Press Ctrl+D to bookmark this page.",
                FontSize = 11,
                Foreground = _theme.TextMutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            _bookmarksList.Children.Add(hint);
            return;
        }

        foreach (var bookmark in _session.State.Bookmarks)
        {
            var btn = new Button
            {
                Height = 26,
                Padding = new Thickness(6, 0, 8, 0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(SlateTheme.RadiusSmall)
            };

            var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var icon = Icon("\uE735", 11);
            icon.Foreground = _theme.AccentPrimaryBrush;
            stack.Children.Add(icon);

            var title = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(bookmark.Title) ? bookmark.Url : bookmark.Title,
                FontSize = 11.5,
                Foreground = _theme.TextPrimaryBrush,
                MaxWidth = 130,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(title);
            btn.Content = stack;

            var targetUrl = bookmark.Url;
            ToolTipService.SetToolTip(btn, $"{bookmark.Title}\n{targetUrl}");
            AutomationProperties.SetName(btn, bookmark.Title);

            btn.Click += async (_, _) => await NavigateAsync(targetUrl);

            var menu = new MenuFlyout();
            var openNewTab = new MenuFlyoutItem { Text = "Open in new tab" };
            openNewTab.Click += async (_, _) => await NewTabAsync(targetUrl);
            menu.Items.Add(openNewTab);

            var openPrivateTab = new MenuFlyoutItem { Text = "Open in new private tab" };
            openPrivateTab.Click += async (_, _) => await NewTabAsync(targetUrl, isPrivate: true);
            menu.Items.Add(openPrivateTab);

            var copyUrl = new MenuFlyoutItem { Text = "Copy link address" };
            copyUrl.Click += (_, _) =>
            {
                try
                {
                    var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dp.SetText(targetUrl);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                    Notify("Link copied to clipboard.");
                }
                catch { }
            };
            menu.Items.Add(copyUrl);

            menu.Items.Add(new MenuFlyoutSeparator());

            var deleteItem = new MenuFlyoutItem { Text = "Delete bookmark" };
            var bookmarkId = bookmark.Id;
            deleteItem.Click += (_, _) =>
            {
                _session.RemoveBookmark(bookmarkId);
                RenderBookmarksBar();
                UpdateBookmarkIndicator();
                QueueSave();
            };
            menu.Items.Add(deleteItem);

            btn.ContextFlyout = menu;
            _bookmarksList.Children.Add(btn);
        }
    }

    private void Shutdown()
    {
        if (_closing) return;
        _closing = true; _saveTimer.Stop(); _sleepTimer.Stop(); _sidebarAnimation?.Stop();
        _commandRouter.Dispose();
        _root.ActualThemeChanged -= OnThemeChanged;
        _uiSettings.ColorValuesChanged -= OnSystemColorValuesChanged;
        foreach (var download in _session.State.Downloads.Where(item => _downloadOperations.ContainsKey(item.Id)))
        {
            try
            {
                var operation = _downloadOperations[download.Id];
                if (operation.State != Microsoft.Web.WebView2.Core.CoreWebView2DownloadState.Completed)
                {
                    operation.Cancel();
                    DownloadSafety.TryDeleteIncompleteFile(download.Path);
                    download.Path = "";
                    download.Status = "Interrupted";
                }
            }
            catch
            {
                DownloadSafety.TryDeleteIncompleteFile(download.Path);
                download.Path = "";
                download.Status = "Interrupted";
            }
        }
        _downloadOperations.Clear();
        foreach (var id in _runtimes.Keys.ToList()) { DisposeRuntime(id); }
        _runtimes.Clear();
        _gamingEfficiencyService?.Dispose();
        Save();
    }
}
