using System.Net;
using System.Net.Http;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Xaml.Media.Imaging;
using Slate.Core;
using Slate.Engine;
using Slate.Services;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace Slate;

public sealed partial class MainWindow
{
    private Dictionary<Guid, TabRuntime> _runtimes => _tabManager.Runtimes;
    private Dictionary<Guid, CoreWebView2DownloadOperation> _downloadOperations => _downloadCoordinator.ActiveOperations;
    private int _renderVersion;
    private const int MaximumFaviconBytes = 256 * 1024;
    private const uint MaximumFaviconDimension = 1024;

    private CoreWebView2? CurrentCore() => _runtimes.TryGetValue(FocusedTab.Id, out var runtime) ? runtime.View.CoreWebView2 : null;
    private void OpenFindBar()
    {
        _findBarOpen = true;
        _findBar.Visibility = Visibility.Visible;
        _findInput.Focus(FocusState.Programmatic);
        _findInput.SelectAll();
        if (!string.IsNullOrWhiteSpace(_findInput.Text))
            _ = StartFindAsync(_findInput.Text);
    }

    private void CloseFindBar()
    {
        if (!_findBarOpen) return;
        _findBarOpen = false;
        _findBar.Visibility = Visibility.Collapsed;
        StopFind();
        FocusPage();
    }

    private void StopFind()
    {
        try { CurrentCore()?.Find?.Stop(); } catch { }
        _findStatus.Text = "";
    }

    private async Task StartFindAsync(string term)
    {
        var core = CurrentCore();
        if (core?.Find is not { } find) return;
        if (string.IsNullOrWhiteSpace(term))
        {
            StopFind();
            return;
        }

        try
        {
            var env = await GetEnvironmentAsync();
            var options = env.CreateFindOptions();
            options.FindTerm = term;
            options.ShouldHighlightAllMatches = true;
            options.SuppressDefaultFindDialog = true;
            await find.StartAsync(options);
            UpdateFindCount(find);
        }
        catch (Exception ex)
        {
            _findStatus.Text = "";
            System.Diagnostics.Debug.WriteLine("Find error: " + ex.Message);
        }
    }

    private void FindNext()
    {
        try { CurrentCore()?.Find?.FindNext(); } catch { }
    }

    private void FindPrevious()
    {
        try { CurrentCore()?.Find?.FindPrevious(); } catch { }
    }

    private void UpdateFindCount(CoreWebView2Find find)
    {
        if (string.IsNullOrEmpty(_findInput.Text))
        {
            _findStatus.Text = "";
            return;
        }
        _findStatus.Text = find.MatchCount > 0 ? $"{find.ActiveMatchIndex + 1} of {find.MatchCount}" : "0 of 0";
    }

    private TabRuntime? CurrentRuntime() => _runtimes.GetValueOrDefault(FocusedTab.Id);

    private async Task ApplyZoomAsync(TabRuntime runtime, double zoom)
    {
        runtime.ZoomFactor = zoom;
        if (runtime.View.CoreWebView2 is { } core)
        {
            var factorStr = zoom.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                await core.CallDevToolsProtocolMethodAsync("Emulation.setPageScaleFactor", $"{{\"pageScaleFactor\":{factorStr}}}");
            }
            catch
            {
                try
                {
                    await core.ExecuteScriptAsync($"document.documentElement.style.zoom = '{factorStr}';");
                }
                catch { }
            }
        }
    }

    private void ZoomIn()
    {
        if (CurrentRuntime() is { } runtime)
        {
            var target = Math.Min(5.0, Math.Round(runtime.ZoomFactor + 0.1, 1));
            _ = ApplyZoomAsync(runtime, target);
            UpdateZoomBadge();
        }
    }

    private void ZoomOut()
    {
        if (CurrentRuntime() is { } runtime)
        {
            var target = Math.Max(0.25, Math.Round(runtime.ZoomFactor - 0.1, 1));
            _ = ApplyZoomAsync(runtime, target);
            UpdateZoomBadge();
        }
    }

    private void ZoomReset()
    {
        if (CurrentRuntime() is { } runtime)
        {
            _ = ApplyZoomAsync(runtime, 1.0);
            UpdateZoomBadge();
        }
    }

    private void UpdateZoomBadge()
    {
        var runtime = CurrentRuntime();
        if (runtime is not null && Math.Abs(runtime.ZoomFactor - 1.0) > 0.01)
        {
            _zoomBadge.Content = $"{(int)Math.Round(runtime.ZoomFactor * 100)}%";
            _zoomBadge.Visibility = Visibility.Visible;
        }
        else
        {
            _zoomBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void ToggleFullScreen(bool? force = null)
    {
        if (_isPageFullScreen)
        {
            ExitPageFullScreen();
            if (force == true) return;
        }
        ToggleUserFullScreen(force);
    }

    private void ToggleUserFullScreen(bool? force = null)
    {
        bool target = force ?? !_isUserFullScreen;
        if (_isUserFullScreen == target) return;
        _isUserFullScreen = target;
        ApplyFullScreenLayout();
    }

    private void HandlePageFullScreenChanged(CoreWebView2 core)
    {
        bool target;
        try { target = core.ContainsFullScreenElement; } catch { return; }
        if (_isPageFullScreen == target) return;
        _isPageFullScreen = target;

        if (target)
        {
            string rawHost = Navigation.DisplayHost(core.Source);
            string host = BrowserText.SanitizeHost(rawHost);
            _pageFullscreenText.Text = $"{host} is fullscreen · Esc to exit";
            _pageFullscreenIndicator.Visibility = Visibility.Visible;
            _pageFullscreenTimer.Stop();
            _pageFullscreenTimer.Start();
        }
        else
        {
            _pageFullscreenTimer.Stop();
            _pageFullscreenIndicator.Visibility = Visibility.Collapsed;
        }

        ApplyFullScreenLayout();
    }

    private void ExitPageFullScreen()
    {
        _pageFullscreenTimer.Stop();
        _pageFullscreenIndicator.Visibility = Visibility.Collapsed;
        if (CurrentCore() is { } core)
        {
            _ = core.ExecuteScriptAsync("if(document.exitFullscreen) document.exitFullscreen();");
        }
        _isPageFullScreen = false;
        ApplyFullScreenLayout();
    }

    private void ApplyFullScreenLayout()
    {
        if (_isPageFullScreen)
        {
            // Webpage element (e.g. YouTube video) requested fullscreen.
            // Hide all browser chrome so the media fills the entire screen edge-to-edge.
            if (AppWindow.Presenter is OverlappedPresenter op)
            {
                _wasMaximizedBeforeFullScreen = op.State == OverlappedPresenterState.Maximized;
            }

            _titleBar.Visibility = Visibility.Collapsed;
            _root.RowDefinitions[0].Height = new(0);

            _sidebar.Visibility = Visibility.Collapsed;
            _body.ColumnDefinitions[0].Width = new(0);

            _toolbarSurface.Visibility = Visibility.Collapsed;
            _status.Visibility = Visibility.Collapsed;
            _content.RowDefinitions[3].Height = new(0);
            _content.Margin = new(0);

            _pageFrame.CornerRadius = new(0);
            _pageFrame.BorderThickness = new(0);
            _topEdgeTrigger.Visibility = Visibility.Visible;

            try { AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen); } catch { }
        }
        else if (_isUserFullScreen)
        {
            // User pressed F11 to enter fullscreen mode.
            // The browser takes the entire screen, but preserves full browser chrome
            // (all tabs on the left, toolbar, titlebar) just like a maximized window.
            if (AppWindow.Presenter is OverlappedPresenter op)
            {
                _wasMaximizedBeforeFullScreen = op.State == OverlappedPresenterState.Maximized;
            }

            _topEdgeTrigger.Visibility = Visibility.Collapsed;
            _pageFullscreenIndicator.Visibility = Visibility.Collapsed;
            _pageFullscreenTimer.Stop();

            _titleBar.Visibility = Visibility.Visible;
            _root.RowDefinitions[0].Height = new(36);

            _sidebar.Visibility = Visibility.Visible;
            _body.ColumnDefinitions[0].Width = new(Collapsed ? 64 : 228);

            _toolbarSurface.Visibility = Visibility.Visible;
            _status.Visibility = Visibility.Visible;
            _content.RowDefinitions[3].Height = new(24);
            _content.Margin = new(0, 0, 10, 0);

            _pageFrame.CornerRadius = new(SlateTheme.RadiusMedium);
            _pageFrame.BorderThickness = new(1);

            try { AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen); } catch { }
        }
        else
        {
            _topEdgeTrigger.Visibility = Visibility.Collapsed;
            _pageFullscreenIndicator.Visibility = Visibility.Collapsed;
            _pageFullscreenTimer.Stop();

            _titleBar.Visibility = Visibility.Visible;
            _root.RowDefinitions[0].Height = new(36);

            _sidebar.Visibility = Visibility.Visible;
            _body.ColumnDefinitions[0].Width = new(Collapsed ? 64 : 228);

            _toolbarSurface.Visibility = Visibility.Visible;
            _status.Visibility = Visibility.Visible;
            _content.RowDefinitions[3].Height = new(24);
            _content.Margin = new(0, 0, 10, 0);

            _pageFrame.CornerRadius = new(SlateTheme.RadiusMedium);
            _pageFrame.BorderThickness = new(1);

            try
            {
                AppWindow.SetPresenter(AppWindowPresenterKind.Default);
                if (_wasMaximizedBeforeFullScreen && AppWindow.Presenter is OverlappedPresenter op)
                {
                    op.Maximize();
                }
            }
            catch { }
        }
    }

    private void PrintPage()
    {
        if (CurrentCore() is { } core) core.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
        else Notify("Open a page to print.");
    }

    private async Task HardReloadAsync()
    {
        var tab = FocusedTab;
        if (_runtimes.TryGetValue(tab.Id, out var runtime) && runtime.Failed)
        {
            DisposeRuntime(tab.Id); _ = RunAsync(RefreshAsync); return;
        }
        if (CurrentCore() is { } core)
        {
            if (Navigation.IsViewSourceUrl(tab.Url) && _runtimes.TryGetValue(tab.Id, out var r))
            {
                await LoadViewSourceAsync(tab, r, core, tab.Url);
                return;
            }
            try
            {
                await core.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}");
            }
            catch
            {
                core.Reload();
            }
        }
    }

    private void OpenDevTools()
    {
        if (_engineService.IsHardenedIsolation)
        {
            Notify("Developer tools are disabled in Hardened Isolation mode.");
            return;
        }
        if (!_session.State.Settings.DeveloperToolsEnabled)
        {
            Notify("Developer tools are disabled. Enable them in Settings.");
            return;
        }
        if (CurrentCore() is { } core)
        {
            core.Settings.AreDevToolsEnabled = true;
            core.OpenDevToolsWindow();
        }
    }

    private void StopLoading()
    {
        if (_findBarOpen) { CloseFindBar(); return; }
        if (_isPageFullScreen)
        {
            ExitPageFullScreen();
            return;
        }
        if (_isUserFullScreen)
        {
            ToggleUserFullScreen(false);
            return;
        }
        if (FocusedTab.IsLoading) CurrentCore()?.Stop();
    }
    private bool IsVisible(Guid id) => id == _session.ActiveTab.Id || id == _splitTabId;
    private bool IsLive(BrowserTab tab, TabRuntime runtime) => !_closing && _runtimes.TryGetValue(tab.Id, out var current) && ReferenceEquals(current, runtime) && _session.State.Tabs.Contains(tab);
    private Task<CoreWebView2Environment> GetEnvironmentAsync() => _engineService.GetEnvironmentAsync();

    private async Task NewTabAsync(string url = Navigation.NewTab, bool temporary = false, bool isPrivate = false)
    {
        if (_session.State.Tabs.Count >= BrowserSession.MaximumTabs)
        {
            Notify($"Slate supports up to {BrowserSession.MaximumTabs} open tabs. Close one before opening another.");
            return;
        }
        string resolvedUrl = url == Navigation.NewTab ? Navigation.NewTab :
            Navigation.IsLocalFileUrl(url) ? url :
            _navigationCoordinator.ResolveInput(url, _session.State.Settings.SearchEngine);
        _session.AddTab(resolvedUrl, temporary, isPrivate); _focusedTabId = null;
        await RefreshAsync(); QueueSave();
        if (resolvedUrl == Navigation.NewTab) FocusAddress();
    }

    private async Task ActivateTabAsync(Guid id)
    {
        if (!_session.Activate(id)) return;
        if (_splitTabId == id) _splitTabId = null;
        _focusedTabId = null; await RefreshAsync(); QueueSave(); FocusPage();
    }

    private async Task CloseTabAsync(Guid id)
    {
        if (_splitTabId == id) _splitTabId = null;
        if (!_runtimes.TryGetValue(id, out var closingRuntime) || closingRuntime.Downloads.Count == 0) DisposeRuntime(id);
        _session.CloseTab(id); _focusedTabId = null;
        await RefreshAsync(); QueueSave();
    }

    private async Task CloseOtherTabsAsync(Guid keepTabId)
    {
        var closed = _session.CloseOtherTabs(keepTabId);
        foreach (var tab in closed)
        {
            if (_splitTabId == tab.Id) _splitTabId = null;
            if (!_runtimes.TryGetValue(tab.Id, out var closingRuntime) || closingRuntime.Downloads.Count == 0) DisposeRuntime(tab.Id);
        }
        _focusedTabId = null;
        await RefreshAsync(); QueueSave();
    }

    private async Task CloseTabsBelowAsync(Guid tabId)
    {
        var closed = _session.CloseTabsBelow(tabId);
        foreach (var tab in closed)
        {
            if (_splitTabId == tab.Id) _splitTabId = null;
            if (!_runtimes.TryGetValue(tab.Id, out var closingRuntime) || closingRuntime.Downloads.Count == 0) DisposeRuntime(tab.Id);
        }
        _focusedTabId = null;
        await RefreshAsync(); QueueSave();
    }

    private async Task MoveTabAsync(Guid tabId, int delta)
    {
        if (_session.MoveTab(tabId, delta))
        {
            await RefreshAsync();
            QueueSave();
        }
    }

    private void ReloadTab(Guid tabId)
    {
        if (_runtimes.TryGetValue(tabId, out var runtime) && runtime.View.CoreWebView2 is { } core)
        {
            if (runtime.Suspended) { core.Resume(); runtime.Suspended = false; }
            var tab = _session.State.Tabs.FirstOrDefault(t => t.Id == tabId);
            if (tab is not null && Navigation.IsViewSourceUrl(tab.Url))
            {
                _ = LoadViewSourceAsync(tab, runtime, core, tab.Url);
            }
            else
            {
                core.Reload();
            }
        }
    }

    private async Task DuplicateTabAsync(Guid tabId)
    {
        var tab = _session.State.Tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab is null) return;
        await NewTabAsync(tab.Url, tab.IsTemporary, tab.IsPrivate);
    }

    private void UpdateDownloadIndicator()
    {
        if (_downloadsButton is null) return;
        bool hasActive = _downloadOperations.Count > 0;
        _downloadsButton.Foreground = hasActive ? _theme.AccentPrimaryBrush : _theme.TextPrimaryBrush;
        ToolTipService.SetToolTip(_downloadsButton, hasActive ? $"Downloads ({_downloadOperations.Count} active) · Ctrl+J" : "Downloads · Ctrl+J");
    }

    private void UpdateBookmarkIndicator()
    {
        if (_bookmarkButton is null) return;
        var tab = FocusedTab;
        bool isBookmarked = _session.IsBookmarked(tab.Url);
        _bookmarkButton.Content = Icon(isBookmarked ? "\uE735" : "\uE734");
        _bookmarkButton.Foreground = isBookmarked ? _theme.AccentPrimaryBrush : _theme.TextPrimaryBrush;
        ToolTipService.SetToolTip(_bookmarkButton, isBookmarked ? "Remove bookmark · Ctrl+D" : "Bookmark this tab · Ctrl+D");
        AutomationProperties.SetName(_bookmarkButton, isBookmarked ? "Remove bookmark" : "Bookmark this tab");
    }

    private async Task ToggleBookmarkAsync()
    {
        var tab = FocusedTab;
        if (tab.Url == Navigation.NewTab || !Navigation.IsWebUrl(tab.Url))
        {
            Notify("Cannot bookmark a blank new tab.");
            return;
        }

        bool added = _session.ToggleBookmark(tab.Url, tab.Title);
        UpdateBookmarkIndicator();
        RenderBookmarksBar();
        QueueSave();
        Notify(added ? $"Bookmarked “{tab.Title}”." : $"Removed bookmark for “{tab.Title}”.");
        await Task.CompletedTask;
    }

    private async Task RestoreClosedAsync(Guid? id = null)
    {
        if (_session.State.Tabs.Count >= BrowserSession.MaximumTabs) { Notify($"Close a tab before restoring another. Slate supports up to {BrowserSession.MaximumTabs} open tabs."); return; }
        if (_session.RestoreClosed(id) is null) { Notify("No recently closed tabs."); return; }
        _focusedTabId = null; await RefreshAsync(); QueueSave();
    }

    private Task CycleTabAsync(int direction)
    {
        var tabs = _session.VisibleTabs.ToList();
        var index = tabs.FindIndex(t => t.Id == _session.ActiveTab.Id);
        return ActivateTabAsync(tabs[(index + direction + tabs.Count) % tabs.Count].Id);
    }

    private async Task NavigateAsync(string input)
    {
        var tab = FocusedTab;
        var previousUrl = tab.Url;
        string url = Navigation.IsLocalFileUrl(input)
            ? input
            : _navigationCoordinator.ResolveInput(input, _session.State.Settings.SearchEngine);
        _addressEditing = false;
        if (url == Navigation.NewTab)
        {
            DisposeRuntime(tab.Id); tab.Url = url; tab.Title = tab.IsPrivate ? "InPrivate tab" : "New tab"; tab.Favicon = null;
            await RefreshAsync(); QueueSave(); return;
        }
        tab.Url = url;
        bool restrictedDocument = Navigation.IsLocalFileUrl(url) || Navigation.IsViewSourceUrl(url);
        bool previousRestrictedDocument = Navigation.IsLocalFileUrl(previousUrl) || Navigation.IsViewSourceUrl(previousUrl);
        if (restrictedDocument != previousRestrictedDocument && _runtimes.ContainsKey(tab.Id))
        {
            DisposeRuntime(tab.Id);
            await RefreshAsync();
            UpdateChrome(); QueueSave(); FocusPage();
            return;
        }
        if (_runtimes.TryGetValue(tab.Id, out var runtime) && runtime.View.CoreWebView2 is { } core && !runtime.Failed)
        {
            runtime.SecureNavigation = false; runtime.CertificateError = false; runtime.FaviconImage = null; runtime.FaviconRevision++;
            if (runtime.Suspended) { core.Resume(); runtime.Suspended = false; }
            if (Navigation.IsViewSourceUrl(url))
            {
                core.Settings.IsScriptEnabled = false;
                await LoadViewSourceAsync(tab, runtime, core, url);
            }
            else
            {
                core.Settings.IsScriptEnabled = !Navigation.IsLocalFileUrl(url);
                core.Navigate(url);
            }
        }
        else { DisposeRuntime(tab.Id); await RefreshAsync(); }
        UpdateChrome(); QueueSave(); FocusPage();
    }

    private async Task RefreshAsync()
    {
        if (_closing) return;
        int version = ++_renderVersion;
        if (_splitTabId == _session.ActiveTab.Id || !_session.State.Tabs.Any(t => t.Id == _splitTabId && t.WorkspaceId == _session.State.ActiveWorkspaceId)) _splitTabId = null;
        if (_focusedTabId != _session.ActiveTab.Id && _focusedTabId != _splitTabId) _focusedTabId = null;
        foreach (var (id, runtime) in _runtimes)
            runtime.View.Visibility = IsVisible(id) ? Visibility.Visible : Visibility.Collapsed;
        // Detach both hosts before attaching either one: a view has one XAML parent.
        _primary.Children.Clear(); _secondary.Children.Clear(); _parkedViews.Children.Clear();
        foreach (var (id, runtime) in _runtimes)
            if (!IsVisible(id)) _parkedViews.Children.Add(runtime.View);
        _panes.ColumnDefinitions[1].Width = _splitTabId.HasValue ? new(1, GridUnitType.Star) : new(0);
        _panes.ColumnSpacing = _splitTabId.HasValue ? 6 : 0;
        _secondary.Visibility = _splitTabId.HasValue ? Visibility.Visible : Visibility.Collapsed;
        RenderSidebar(); UpdateChrome();
        var active = _session.ActiveTab;
        await ShowTabAsync(active, _primary, version);
        if (version != _renderVersion || _closing) return;
        if (_splitTabId is Guid split && _session.State.Tabs.FirstOrDefault(t => t.Id == split) is { } other)
            await ShowTabAsync(other, _secondary, version);
        if (version == _renderVersion) { UpdateChrome(); Animate(_panes); }
    }

    private async Task ShowTabAsync(BrowserTab tab, Grid host, int version)
    {
        tab.LastAccessed = DateTimeOffset.UtcNow;
        if (tab.Url == Navigation.NewTab) { tab.IsSleeping = false; host.Children.Add(BuildNewTab(tab)); return; }
        if (!_runtimes.TryGetValue(tab.Id, out var runtime))
        {
            var view = new WebView2 { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            _commandRouter.AttachWebView2(view);
            runtime = new TabRuntime(view) { ZoomFactor = _session.State.Settings.DefaultZoomPercent / 100.0 };
            _runtimes.Add(tab.Id, runtime);
        }
        runtime.View.Visibility = Visibility.Visible;
        host.Children.Add(runtime.View);
        try
        {
            runtime.Initialization ??= InitializeWebViewAsync(tab, runtime);
            await runtime.Initialization;
            if (!IsLive(tab, runtime) || version != _renderVersion) return;
            if (runtime.Failed) throw new InvalidOperationException(runtime.Error ?? "The web process stopped.");
            if (runtime.Suspended) { runtime.View.CoreWebView2.Resume(); runtime.Suspended = false; }
            tab.IsSleeping = false;
            RenderSidebar();
        }
        catch (Exception ex)
        {
            if (!IsLive(tab, runtime) || version != _renderVersion) return;
            tab.IsLoading = false; runtime.Error = ex.Message; runtime.Failed = true;
            host.Children.Clear(); host.Children.Add(BuildError(tab, ex.Message)); UpdateChrome(); RenderSidebar();
        }
    }

    private async Task InitializeWebViewAsync(BrowserTab tab, TabRuntime runtime)
    {
        var environment = await GetEnvironmentAsync();
        if (!IsLive(tab, runtime)) return;
        if (tab.IsPrivate)
        {
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = true;
            await runtime.View.EnsureCoreWebView2Async(environment, options);
        }
        else
        {
            await runtime.View.EnsureCoreWebView2Async(environment);
        }
        if (!IsLive(tab, runtime)) return;
        var core = runtime.View.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = true;
        // Slate owns browser commands. WebView2 still preserves editing/movement keys when
        // browser accelerators are disabled, while the router handles Slate's registry.
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        _engineService.ConfigureHardenedSettings(core.Settings, _session.State.Settings.DeveloperToolsEnabled);
        core.Settings.IsZoomControlEnabled = true;
        core.Settings.IsSwipeNavigationEnabled = false;
        SetWebTheme(core);
        if (App.SmokeOutput is not null)
        {
            var downloadFolder = Path.Combine(_store.DirectoryPath, "Downloads");
            Directory.CreateDirectory(downloadFolder);
            core.Profile.DefaultDownloadFolderPath = downloadFolder;
        }
        Windows.Foundation.TypedEventHandler<CoreWebView2, object> fullScreenHandler = (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() => HandlePageFullScreenChanged(core));
        };
        core.ContainsFullScreenElementChanged += fullScreenHandler;

        Windows.Foundation.TypedEventHandler<CoreWebView2, object> audioHandler = (_, _) =>
        {
            DispatcherQueue.TryEnqueue(RenderSidebar);
        };
        core.IsDocumentPlayingAudioChanged += audioHandler;

        Windows.Foundation.TypedEventHandler<CoreWebView2, object> mutedHandler = (_, _) =>
        {
            DispatcherQueue.TryEnqueue(RenderSidebar);
        };
        core.IsMutedChanged += mutedHandler;

        Windows.Foundation.TypedEventHandler<CoreWebView2Find, object>? matchCountHandler = null;
        Windows.Foundation.TypedEventHandler<CoreWebView2Find, object>? activeMatchIndexHandler = null;
        if (core.Find is { } find)
        {
            matchCountHandler = (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (IsLive(tab, runtime) && tab.Id == FocusedTab.Id) UpdateFindCount(find);
            });
            find.MatchCountChanged += matchCountHandler;

            activeMatchIndexHandler = (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (IsLive(tab, runtime) && tab.Id == FocusedTab.Id) UpdateFindCount(find);
            });
            find.ActiveMatchIndexChanged += activeMatchIndexHandler;
        }

        Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2NavigationStartingEventArgs> navStartingHandler = (_, args) =>
        {
            if (!IsLive(tab, runtime)) return;
            if (Navigation.IsLocalFileUrl(args.Uri))
            {
                if (Navigation.IsWebUrl(core.Source))
                {
                    args.Cancel = true; Notify("Websites are blocked from accessing local files."); return;
                }
            }
            else if (args.Uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true; Notify("Opening dangerous file types or network shares is blocked."); return;
            }
            else if (Navigation.IsViewSourceUrl(args.Uri))
            {
                args.Cancel = true;
                Notify("Use Slate's View Source command to open source safely.");
                return;
            }
            else if (!Navigation.IsWebUrl(args.Uri) && args.Uri != "about:blank")
            {
                args.Cancel = true; Notify("Slate only opens HTTP, HTTPS, and safe local files. External app links are blocked."); return;
            }

            // Block silent HTTPS downgrade attempts
            if (_navigationCoordinator.IsDowngradeAttempt(core.Source, args.Uri))
            {
                args.Cancel = true;
                Notify("Slate blocked an insecure HTTP downgrade attempt.");
                return;
            }

            // Warn on unencrypted public HTTP navigation
            if (Navigation.IsPublicHttp(args.Uri))
            {
                string host = Navigation.DisplayHost(args.Uri);
                if (!_navigationCoordinator.IsInsecureHttpAllowed(host))
                {
                    Notify($"Warning: {BrowserText.SanitizeHost(host)} is not secure (HTTP). Traffic is unencrypted.");
                }
            }

            tab.IsLoading = true; runtime.Error = null; runtime.SecureNavigation = false; runtime.CertificateError = false;
            runtime.FaviconImage = null; runtime.FaviconRevision++; tab.Favicon = null;
            UpdateChrome(); RenderSidebar();
        };
        core.NavigationStarting += navStartingHandler;

        Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2NavigationStartingEventArgs> frameNavStartingHandler = (_, args) =>
        {
            if (!Navigation.IsAllowedFrameUrl(args.Uri)) args.Cancel = true;
        };
        core.FrameNavigationStarting += frameNavStartingHandler;

        Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2LaunchingExternalUriSchemeEventArgs> launchSchemeHandler = (_, args) =>
        {
            args.Cancel = true;
            Notify("Slate blocked a request to open an external application.");
        };
        core.LaunchingExternalUriScheme += launchSchemeHandler;

        Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2SourceChangedEventArgs> sourceChangedHandler = (_, _) =>
        {
            if (!IsLive(tab, runtime)) return;
            if (Navigation.IsWebUrl(core.Source) || Navigation.IsLocalFileUrl(core.Source) || Navigation.IsViewSourceUrl(core.Source)) { tab.Url = core.Source; runtime.SecureNavigation = false; }
            UpdateChrome(); QueueSave();
        };
        core.SourceChanged += sourceChangedHandler;

        Windows.Foundation.TypedEventHandler<CoreWebView2, object> historyChangedHandler = (_, _) => { if (IsLive(tab, runtime)) UpdateChrome(); };
        core.HistoryChanged += historyChangedHandler;

        Windows.Foundation.TypedEventHandler<CoreWebView2, object> docTitleHandler = (_, _) =>
        {
            if (!IsLive(tab, runtime)) return;
            tab.Title = BrowserText.SanitizeTitle(core.DocumentTitle, Navigation.DisplayHost(tab.Url));
            var visit = _session.State.History.FirstOrDefault(h => h.Url == tab.Url);
            if (visit is not null) visit.Title = tab.Title;
            UpdateChrome(); RenderSidebar(); QueueSave();
        };
        core.DocumentTitleChanged += docTitleHandler;

        Windows.Foundation.TypedEventHandler<CoreWebView2, object> faviconHandler = (_, _) =>
        {
            if (!IsLive(tab, runtime)) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (IsLive(tab, runtime)) _ = UpdateFaviconAsync(tab, runtime, core);
            });
        };
        core.FaviconChanged += faviconHandler;

        Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2ServerCertificateErrorDetectedEventArgs> certErrorHandler = (_, args) =>
        {
            args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
            if (!IsLive(tab, runtime)) return;
            runtime.CertificateError = true; runtime.SecureNavigation = false;
            runtime.LastCertificate = args.ServerCertificate;
            runtime.LastCertificateError = args.ErrorStatus;
            runtime.Error = $"Slate blocked a TLS certificate error ({args.ErrorStatus}).";
            UpdateChrome();
        };
        core.ServerCertificateErrorDetected += certErrorHandler;

        Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs> navCompletedHandler = (sender, args) =>
        {
            if (!IsLive(tab, runtime)) return;
            tab.IsLoading = false;
            if (Navigation.IsWebUrl(core.Source) || Navigation.IsLocalFileUrl(core.Source) || Navigation.IsViewSourceUrl(core.Source)) tab.Url = core.Source;
            runtime.SecureNavigation = args.IsSuccess && !runtime.CertificateError &&
                Uri.TryCreate(core.Source, UriKind.Absolute, out var completedUri) && completedUri.Scheme == Uri.UriSchemeHttps;
            if (args.IsSuccess) { runtime.Error = null; _session.RecordVisit(tab); }
            else if (args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled) runtime.Error = "Page could not load: " + args.WebErrorStatus;
            if (args.IsSuccess)
            {
                _ = UpdateFaviconAsync(tab, runtime, core);
                if (Math.Abs(runtime.ZoomFactor - 1.0) > 0.01)
                {
                    _ = ApplyZoomAsync(runtime, runtime.ZoomFactor);
                }
            }
            UpdateChrome(); RenderSidebar(); QueueSave();
        };
        core.NavigationCompleted += navCompletedHandler;

        Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2NewWindowRequestedEventArgs> newWindowHandler = async (_, args) =>
        {
            args.Handled = true;
            var origin = Navigation.WebOrigin(core.Source) ?? "unknown";
            if (!args.IsUserInitiated) Notify("Slate blocked a popup that was not opened by you.");
            else if (!AllowPopup(origin, DateTimeOffset.UtcNow)) Notify("Slate blocked repeated popups from this site.");
            else if (_navigationCoordinator.IsDowngradeAttempt(core.Source, args.Uri))
            {
                Notify("Slate blocked an insecure HTTP popup downgrade.");
            }
            else if (Navigation.IsWebUrl(args.Uri) || args.Uri == "about:blank") await RunAsync(() => NewTabAsync(args.Uri, tab.IsTemporary, tab.IsPrivate));
            else Notify("This popup uses an unsupported address.");
        };
        core.NewWindowRequested += newWindowHandler;

        Windows.Foundation.TypedEventHandler<CoreWebView2, object> windowCloseHandler = async (_, _) => { if (IsLive(tab, runtime)) await RunAsync(() => CloseTabAsync(tab.Id)); };
        core.WindowCloseRequested += windowCloseHandler;

        Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2PermissionRequestedEventArgs> permissionHandler = async (_, args) =>
        {
            var deferral = args.GetDeferral();
            args.State = CoreWebView2PermissionState.Deny;
            args.SavesInProfile = false;
            runtime.HasActivePermissionPrompt = true;
            try
            {
                if (!IsLive(tab, runtime) || runtime.CertificateError) return;
                var requestedOrigin = Navigation.WebOrigin(args.Uri);
                var policy = PermissionPolicy(args.PermissionKind);
                if (requestedOrigin is null || policy is null ||
                    !string.Equals(Navigation.WebOrigin(core.Source), requestedOrigin, StringComparison.OrdinalIgnoreCase)) return;
                if (policy.Value.RequiresUserInitiation && !Navigation.IsSecureOrigin(requestedOrigin))
                {
                    Notify($"Slate blocked a request for {policy.Value.Name.ToLowerInvariant()} over an insecure connection.");
                    return;
                }
                if (policy.Value.RequiresUserInitiation && !args.IsUserInitiated)
                {
                    Notify($"Slate blocked a non-user-initiated request for {policy.Value.Name.ToLowerInvariant()}.");
                    return;
                }
                if (!AllowPermissionPrompt(requestedOrigin, args.PermissionKind, DateTimeOffset.UtcNow))
                {
                    Notify("Slate blocked repeated permission prompts from this site.");
                    return;
                }
                var decision = await RequestPermissionAsync(requestedOrigin, policy.Value.Name, policy.Value.Description);
                // A page that navigated while waiting cannot inherit another origin's approval.
                if (IsLive(tab, runtime) && !runtime.CertificateError && string.Equals(Navigation.WebOrigin(core.Source), requestedOrigin, StringComparison.OrdinalIgnoreCase))
                {
                    args.State = decision.Allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                    args.SavesInProfile = !tab.IsTemporary && !tab.IsPrivate && decision.Remember;
                }
            }
            catch (Exception ex) { Notify("Permission request dismissed: " + ex.Message); }
            finally
            {
                runtime.HasActivePermissionPrompt = false;
                deferral.Complete();
            }
        };
        core.PermissionRequested += permissionHandler;

        Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2DownloadStartingEventArgs> downloadStartingHandler = async (_, args) =>
        {
            var deferral = args.GetDeferral();
            string? reservedPath = null;
            try
            {
            if (!IsLive(tab, runtime)) { args.Cancel = true; return; }
            var op = args.DownloadOperation;
            string fallbackFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            string? downloadFolder = DownloadSafety.IsSafeLocalDirectory(_session.State.Settings.DownloadPath)
                ? _session.State.Settings.DownloadPath
                : DownloadSafety.IsSafeLocalDirectory(core.Profile.DefaultDownloadFolderPath)
                    ? core.Profile.DefaultDownloadFolderPath
                    : DownloadSafety.IsSafeLocalDirectory(fallbackFolder) ? fallbackFolder : null;
            if (downloadFolder is null) { args.Cancel = true; Notify("Slate could not find a safe local download folder."); return; }
            if (Navigation.IsDangerousExtension(Path.GetExtension(args.ResultFilePath)))
            {
                args.Cancel = true;
                Notify("Slate blocked downloading a file with a dangerous extension.");
                return;
            }
            bool askDownloadLocation = _session.State.Settings.AskDownloadLocation;
            string safePath;
            try
            {
                Directory.CreateDirectory(downloadFolder);
                if (!DownloadSafety.IsSafeLocalDirectory(downloadFolder)) throw new IOException();
                core.Profile.DefaultDownloadFolderPath = downloadFolder;
                var suggestedName = DownloadSafety.SanitizeFileName(Path.GetFileName(args.ResultFilePath));
                if (askDownloadLocation)
                {
                    var extension = Path.GetExtension(suggestedName);
                    if (string.IsNullOrEmpty(extension))
                    {
                        extension = ".download";
                        suggestedName += extension;
                    }
                    var picker = new Windows.Storage.Pickers.FileSavePicker
                    {
                        SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
                        SuggestedFileName = suggestedName
                    };
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                    picker.FileTypeChoices.Add($"{extension.TrimStart('.').ToUpperInvariant()} file", new List<string> { extension });
                    var selected = await picker.PickSaveFileAsync();
                    if (selected is null) { args.Cancel = true; return; }
                    if (!IsLive(tab, runtime) || !DownloadSafety.IsSafeLocalFilePath(selected.Path) ||
                        Navigation.IsDangerousExtension(Path.GetExtension(selected.Path)))
                    {
                        args.Cancel = true;
                        Notify("Slate blocked an unsafe download destination.");
                        return;
                    }
                    var selectedDirectory = Path.GetDirectoryName(selected.Path)!;
                    var selectedName = Path.GetFileName(selected.Path);
                    await selected.DeleteAsync(Windows.Storage.StorageDeleteOption.PermanentDelete);
                    safePath = DownloadSafety.ReserveSafeDestinationPath(selectedDirectory, selectedName);
                }
                else
                {
                    safePath = DownloadSafety.ReserveSafeDestinationPath(downloadFolder, suggestedName);
                }
                reservedPath = safePath;
                if (Navigation.IsDangerousExtension(Path.GetExtension(safePath)))
                {
                    throw new InvalidOperationException("Dangerous extension");
                }
            }
            catch (InvalidOperationException)
            {
                args.Cancel = true;
                Notify("Slate blocked downloading a file with a dangerous extension.");
                return;
            }
            catch
            {
                args.Cancel = true;
                Notify("Slate could not reserve a safe local download destination.");
                return;
            }
            if (!IsLive(tab, runtime))
            {
                args.Cancel = true;
                DownloadSafety.TryDeleteIncompleteFile(reservedPath);
                return;
            }
            args.ResultFilePath = safePath;
            var download = new DownloadEntry
            {
                FileName = Path.GetFileName(safePath), Path = safePath, TotalBytes = op.TotalBytesToReceive, IsPrivate = tab.IsPrivate
            };
            _session.State.Downloads.Insert(0, download); _downloadOperations[download.Id] = op;
            runtime.Downloads.Add(download.Id);
            bool unsafeDestination = false;
            void Update()
            {
                if (_closing) return;
                var currentPath = string.IsNullOrWhiteSpace(op.ResultFilePath) ? safePath : op.ResultFilePath;
                if (!unsafeDestination && (!DownloadSafety.IsSafeLocalFilePath(currentPath) || Navigation.IsDangerousExtension(Path.GetExtension(currentPath))))
                {
                    unsafeDestination = true;
                    try { if (op.State == CoreWebView2DownloadState.InProgress) op.Cancel(); } catch { }
                    Notify("Slate blocked a download destination outside a safe local folder or with a dangerous extension.");
                }
                download.Path = unsafeDestination ? "" : currentPath;
                if (!unsafeDestination) download.FileName = Path.GetFileName(currentPath);
                download.BytesReceived = op.BytesReceived; download.TotalBytes = op.TotalBytesToReceive;
                download.Status = op.State switch
                {
                    CoreWebView2DownloadState.Completed => "Completed",
                    CoreWebView2DownloadState.Interrupted => op.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled ? "Canceled" : op.InterruptReason == CoreWebView2DownloadInterruptReason.UserPaused ? "Paused" : "Interrupted",
                    _ => "Downloading"
                };
                QueueSave(); Notify($"{download.FileName} · {download.Status}");
                DispatcherQueue.TryEnqueue(UpdateDownloadIndicator);
                if (download.Status is "Completed" or "Canceled" || (download.Status == "Interrupted" && !op.CanResume))
                {
                    if (download.Status != "Completed")
                    {
                        DownloadSafety.TryDeleteIncompleteFile(safePath);
                        download.Path = "";
                    }
                    else
                    {
                        DownloadSafety.AttachZoneIdentifier(safePath, tab.Url);
                    }
                    op.BytesReceivedChanged -= Changed; op.StateChanged -= Changed;
                    _downloadOperations.Remove(download.Id); runtime.Downloads.Remove(download.Id);
                    if (!_session.State.Tabs.Contains(tab) && runtime.Downloads.Count == 0)
                        DispatcherQueue.TryEnqueue(() => DisposeRuntime(tab.Id));
                }
            }
            void Changed(CoreWebView2DownloadOperation sender, object eventArgs) => Update();
            op.BytesReceivedChanged += Changed; op.StateChanged += Changed;
            Update();
            args.Handled = true;
            reservedPath = null;
            }
            catch
            {
                args.Cancel = true;
                DownloadSafety.TryDeleteIncompleteFile(reservedPath);
                Notify("Slate could not start this download safely.");
            }
            finally
            {
                deferral.Complete();
            }
        };
        core.DownloadStarting += downloadStartingHandler;

        Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2ProcessFailedEventArgs> processFailedHandler = (sender, args) =>
        {
            if (!IsLive(tab, runtime)) return;
            if (args.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.FrameRenderProcessExited)
            {
                runtime.Failed = true;
                runtime.Error = $"The web process stopped ({args.ProcessFailedKind}, {args.Reason}, exit {args.ExitCode}). Reload this tab to recover.";
                tab.IsLoading = false;
                if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited) _engineService.ResetEnvironment();
                if (IsVisible(tab.Id)) _ = RunAsync(RefreshAsync);
            }
        };
        core.ProcessFailed += processFailedHandler;

        runtime.Teardown = () =>
        {
            try
            {
                if (runtime.Passwords is not null)
                {
                    runtime.Passwords.OfferChanged -= UpdatePasswordOffer;
                    runtime.Passwords.AvailabilityChanged -= UpdatePasswordIndicator;
                    runtime.Passwords.Dispose();
                    runtime.Passwords = null;
                }
                core.ContainsFullScreenElementChanged -= fullScreenHandler;
                core.IsDocumentPlayingAudioChanged -= audioHandler;
                core.IsMutedChanged -= mutedHandler;
                if (core.Find is { } f)
                {
                    if (matchCountHandler is not null) f.MatchCountChanged -= matchCountHandler;
                    if (activeMatchIndexHandler is not null) f.ActiveMatchIndexChanged -= activeMatchIndexHandler;
                }
                core.NavigationStarting -= navStartingHandler;
                core.FrameNavigationStarting -= frameNavStartingHandler;
                core.LaunchingExternalUriScheme -= launchSchemeHandler;
                core.SourceChanged -= sourceChangedHandler;
                core.HistoryChanged -= historyChangedHandler;
                core.DocumentTitleChanged -= docTitleHandler;
                core.FaviconChanged -= faviconHandler;
                core.ServerCertificateErrorDetected -= certErrorHandler;
                core.NavigationCompleted -= navCompletedHandler;
                core.NewWindowRequested -= newWindowHandler;
                core.WindowCloseRequested -= windowCloseHandler;
                core.PermissionRequested -= permissionHandler;
                core.DownloadStarting -= downloadStartingHandler;
                core.ProcessFailed -= processFailedHandler;
            }
            catch { }
        };
        if (!tab.IsPrivate)
        {
            runtime.Passwords = new PasswordController(core, _credentialVault,
                () => IsLive(tab, runtime) && !runtime.Failed && !runtime.Sleeping && !runtime.Suspended && !tab.IsLoading && !runtime.CertificateError,
                () => tab.IsTemporary, () => IsLive(tab, runtime) && !runtime.Failed,
                () => _session.State.Settings.AutofillPasswords, () => _session.State.Settings.OfferToSavePasswords,
                () => tab.IsPrivate, () => tab.Url);
            runtime.Passwords.OfferChanged += UpdatePasswordOffer;
            runtime.Passwords.AvailabilityChanged += UpdatePasswordIndicator;
            await runtime.Passwords.InitializeAsync();
            if (!IsLive(tab, runtime)) { runtime.Passwords.Dispose(); return; }
        }
        if (Navigation.IsViewSourceUrl(tab.Url))
        {
            await LoadViewSourceAsync(tab, runtime, core, tab.Url);
        }
        else
        {
            core.Settings.IsScriptEnabled = !Navigation.IsLocalFileUrl(tab.Url);
            core.Navigate(tab.Url);
        }
    }

    private bool AllowPopup(string origin, DateTimeOffset now) => _engineService.AllowPopup(origin, now);

    private bool AllowPermissionPrompt(string origin, CoreWebView2PermissionKind kind, DateTimeOffset now) => _engineService.AllowPermissionPrompt(origin, kind, now);

    private static (string Name, string Description, bool RequiresUserInitiation)? PermissionPolicy(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Camera => ("Camera", "Capture video from a connected camera.", true),
        CoreWebView2PermissionKind.Microphone => ("Microphone", "Capture audio from a connected microphone.", true),
        CoreWebView2PermissionKind.Geolocation => ("Location", "Learn your approximate or precise location.", true),
        CoreWebView2PermissionKind.Notifications => ("Notifications", "Show notifications outside the page.", true),
        CoreWebView2PermissionKind.OtherSensors => ("Device sensors", "Read supported motion or environmental sensors.", true),
        CoreWebView2PermissionKind.ClipboardRead => ("Clipboard", "Read information currently stored on your clipboard.", true),
        CoreWebView2PermissionKind.MultipleAutomaticDownloads => ("Multiple automatic downloads", "Download multiple files without asking for each one.", true),
        CoreWebView2PermissionKind.FileReadWrite => ("File editing", "Read or change files that you explicitly select.", true),
        CoreWebView2PermissionKind.Autoplay => ("Media autoplay", "Play audio or video without another interaction.", false),
        CoreWebView2PermissionKind.LocalFonts => ("Local fonts", "See the fonts installed on this computer.", true),
        CoreWebView2PermissionKind.MidiSystemExclusiveMessages => ("System-exclusive MIDI", "Send privileged messages to connected MIDI devices.", true),
        CoreWebView2PermissionKind.WindowManagement => ("Window management", "Learn about and position content across connected displays.", true),
        _ => null
    };

    private async Task UpdateFaviconAsync(BrowserTab tab, TabRuntime runtime, CoreWebView2 core)
    {
        long revision = ++runtime.FaviconRevision;
        tab.Favicon = null; runtime.FaviconImage = null;
        if (IsLive(tab, runtime)) RenderSidebar();
        try
        {
            if (string.IsNullOrWhiteSpace(core.FaviconUri))
            {
                if (IsLive(tab, runtime) && revision == runtime.FaviconRevision) { runtime.FaviconImage = null; RenderSidebar(); QueueSave(); }
                return;
            }
            using var source = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            using var sourceStream = source.AsStreamForRead();
            using var bounded = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                int read = await sourceStream.ReadAsync(buffer);
                if (read == 0) break;
                if (bounded.Length + read > MaximumFaviconBytes) return;
                await bounded.WriteAsync(buffer.AsMemory(0, read));
            }
            if (bounded.Length == 0 || !IsLive(tab, runtime) || revision != runtime.FaviconRevision) return;
            bounded.Position = 0;
            using var randomAccess = bounded.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(randomAccess);
            if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0 || decoder.PixelWidth > MaximumFaviconDimension || decoder.PixelHeight > MaximumFaviconDimension) return;
            randomAccess.Seek(0);
            var image = new BitmapImage { DecodePixelWidth = 32 };
            await image.SetSourceAsync(randomAccess);
            if (!IsLive(tab, runtime) || revision != runtime.FaviconRevision) return;
            runtime.FaviconImage = image;
            var key = FaviconStore.GetKey(tab.Url);
            if (!string.IsNullOrEmpty(key) && !tab.IsPrivate)
            {
                _uiFaviconCache[key] = image;
            }
            if (!tab.IsTemporary && !tab.IsPrivate)
            {
                _ = _faviconStore.SaveFaviconAsync(tab.Url, bounded.ToArray());
            }
            RenderSidebar(); QueueSave();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or OperationCanceledException or System.Runtime.InteropServices.COMException)
        {
            if (IsLive(tab, runtime) && revision == runtime.FaviconRevision) { runtime.FaviconImage = null; RenderSidebar(); }
        }
    }

    private void SetWebTheme(CoreWebView2 core) => core.Profile.PreferredColorScheme = _session.State.Settings.Theme switch
    {
        "Light" => CoreWebView2PreferredColorScheme.Light,
        "Dark" => CoreWebView2PreferredColorScheme.Dark,
        _ => CoreWebView2PreferredColorScheme.Auto
    };

    private void UpdateChrome()
    {
        UpdatePasswordOffer();
        UpdatePasswordIndicator();
        if (_closing) return;
        var tab = FocusedTab; var core = CurrentCore();
        if (!_addressEditing) _address.Text = tab.Url == Navigation.NewTab ? "" : tab.Url;
        _back.IsEnabled = core?.CanGoBack == true; _forward.IsEnabled = core?.CanGoForward == true;
        _reload.Content = Icon(tab.IsLoading ? "\uE711" : "\uE72C");
        ToolTipService.SetToolTip(_reload, tab.IsLoading ? "Stop loading · Esc" : "Reload · Ctrl+R");
        AutomationProperties.SetName(_reload, tab.IsLoading ? "Stop loading" : "Reload");
        _reload.IsEnabled = core is not null || _runtimes.GetValueOrDefault(tab.Id)?.Failed == true;
        UpdateZoomBadge();
        UpdateBookmarkIndicator();
        _bookmarkButton.Visibility = tab.Url == Navigation.NewTab ? Visibility.Collapsed : Visibility.Visible;
        _security.Visibility = tab.Url != Navigation.NewTab || tab.IsPrivate ? Visibility.Visible : Visibility.Collapsed;
        _progress.Visibility = tab.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        _windowTitle.Text = tab.Title == "New tab" ? "" : tab.Title;
        Title = tab.Title == "New tab" ? "Slate" : tab.Title + " — Slate";
        var currentRuntime = _runtimes.GetValueOrDefault(tab.Id);
        _security.Content = Icon(tab.IsPrivate ? "\uE727" : currentRuntime?.SecureNavigation == true ? "\uE72E" : "\uE946");
        var error = currentRuntime?.Error;
        _status.Text = error ?? (tab.IsLoading ? "Loading " + Navigation.DisplayHost(tab.Url) + "…" : tab.IsPrivate ? "InPrivate tab · zero disk persistence · isolated session" : tab.IsTemporary ? "Temporary tab · discarded on restart unless kept or pinned · shares regular site data" :
            (_splitTabId.HasValue ? "Split view" : ""));
    }

    private void Reload()
    {
        var tab = FocusedTab;
        if (_runtimes.TryGetValue(tab.Id, out var runtime) && runtime.Failed)
        {
            DisposeRuntime(tab.Id); _ = RunAsync(RefreshAsync); return;
        }
        if (tab.IsLoading) CurrentCore()?.Stop();
        else if (Navigation.IsViewSourceUrl(tab.Url) && _runtimes.TryGetValue(tab.Id, out var r) && r.View.CoreWebView2 is { } c)
        {
            _ = LoadViewSourceAsync(tab, r, c, tab.Url);
        }
        else CurrentCore()?.Reload();
    }

    private async Task ToggleSplitAsync()
    {
        if (_splitTabId.HasValue) { _splitTabId = null; _focusedTabId = null; }
        else
        {
            var other = _session.VisibleTabs.FirstOrDefault(t => t.Id != _session.ActiveTab.Id);
            if (other is null)
            {
                var active = _session.ActiveTab.Id; other = _session.AddTab(); _session.Activate(active);
            }
            _splitTabId = other.Id;
        }
        await RefreshAsync(); QueueSave();
    }

    private Grid BuildNewTab(BrowserTab tab)
    {
        var grid = new Grid { Padding = new(32, 28, 32, 22), Background = _theme.SurfaceBaseBrush };
        var composition = new Grid();
        composition.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        composition.ColumnDefinitions.Add(new() { Width = new(6, GridUnitType.Star) });
        composition.ColumnDefinitions.Add(new() { Width = new(3, GridUnitType.Star) });
        var center = new StackPanel { MaxWidth = 580, Spacing = 0, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(center, 1); composition.Children.Add(center);

        if (tab.IsPrivate)
        {
            var privateBadge = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new(0, 0, 0, 16)
            };
            privateBadge.Children.Add(new FontIcon { Glyph = "\uE727", FontSize = 18, Foreground = _theme.AccentPrimaryBrush });
            privateBadge.Children.Add(new TextBlock { Text = "InPrivate Browsing", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = _theme.AccentPrimaryBrush });
            center.Children.Add(privateBadge);
        }

        var now = DateTime.Now;
        center.Children.Add(new TextBlock
        {
            Text = now.ToString("h:mm"),
            FontFamily = new("Segoe UI Variable Display"),
            FontSize = 40,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiLight,
            CharacterSpacing = -25,
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = _theme.TextPrimaryBrush
        });
        center.Children.Add(new TextBlock
        {
            Text = now.ToString("dddd, MMMM d"),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = _theme.TextSecondaryBrush,
            Margin = new(0, 2, 0, 24)
        });

        // Center the natural-height TextBox, not a stretched template with a top-aligned content host.
        var search = new TextBox { PlaceholderText = "Search or enter an address", FontSize = 14, MinHeight = 0, Padding = new(0, 8, 8, 8), BorderThickness = new(0), Background = new SolidColorBrush(Colors.Transparent), VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        search.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        search.Resources["TextControlBackgroundFocused"] = new SolidColorBrush(Colors.Transparent);
        search.Resources["TextControlBackgroundPointerOver"] = new SolidColorBrush(Colors.Transparent);
        AutomationProperties.SetName(search, "New tab search");
        search.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; _focusedTabId = tab.Id; await RunAsync(() => NavigateAsync(search.Text)); } };
        var searchGrid = new Grid { ColumnSpacing = 10 };
        searchGrid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        searchGrid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        searchGrid.Children.Add(new FontIcon { Glyph = "\uE721", FontFamily = new("Segoe Fluent Icons"), FontSize = 15, Foreground = _theme.AccentPrimaryBrush, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(search, 1); searchGrid.Children.Add(search);
        var searchSurface = new Border
        {
            Child = searchGrid,
            MinHeight = 44,
            Padding = new(12, 0, 8, 0),
            CornerRadius = new(SlateTheme.RadiusMedium),
            BorderThickness = new(1),
            BorderBrush = _theme.DividerBrush,
            Background = _theme.SurfaceInteractiveBrush
        };
        search.GotFocus += (_, _) => searchSurface.BorderBrush = _theme.FocusRingBrush;
        search.LostFocus += (_, _) => searchSurface.BorderBrush = _theme.DividerBrush;
        searchSurface.PointerEntered += (_, _) => { if (search.FocusState == FocusState.Unfocused) searchSurface.BorderBrush = _theme.BorderStrongBrush; };
        searchSurface.PointerExited += (_, _) => { if (search.FocusState == FocusState.Unfocused) searchSurface.BorderBrush = _theme.DividerBrush; };
        center.Children.Add(searchSurface);

        var frequent = _session.State.History
            .Where(entry => entry.Url == Navigation.NewTab || (Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
            .GroupBy(entry => entry.Url == Navigation.NewTab ? Navigation.NewTab : new Uri(entry.Url).Host, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Entry = group.First(), Visits = group.Count() })
            .OrderByDescending(site => site.Visits)
            .ThenByDescending(site => site.Entry.VisitedAt)
            .Take(5)
            .ToList();
        if (frequent.Count > 0)
        {
            center.Children.Add(new TextBlock { Text = "Frequent sites", FontSize = 12, Foreground = _theme.TextMutedBrush, Margin = new(0, 22, 0, 8) });
            var sites = new Grid { ColumnSpacing = 6 };
            for (int i = 0; i < frequent.Count; i++) sites.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            for (int i = 0; i < frequent.Count; i++)
            {
                var entry = frequent[i].Entry;
                Button site;
                if (entry.Url == Navigation.NewTab)
                {
                    site = BuildNewTabTile();
                }
                else
                {
                    var host = new Uri(entry.Url).Host;
                    var label = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
                    var expectedKey = FaviconStore.GetKey(entry.Url);
                    var iconContainer = new Grid { Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Tag = expectedKey };
                    var monogram = new Border
                    {
                        Width = 28,
                        Height = 28,
                        CornerRadius = new(14),
                        Background = _theme.AccentSoftBrush,
                        Child = new TextBlock { Text = label[..1].ToUpperInvariant(), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = _theme.AccentPrimaryBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                    };
                    if (GetCachedFavicon(entry.Url) is { } cachedImage)
                    {
                        iconContainer.Children.Add(new Image
                        {
                            Source = cachedImage,
                            Width = 28,
                            Height = 28,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        });
                    }
                    else
                    {
                        iconContainer.Children.Add(monogram);
                        var capturedTabId = tab.Id;
                        _ = AttachFaviconAsync(iconContainer, entry.Url, expectedKey, () => _session.ActiveTab.Id == capturedTabId && _session.ActiveTab.Url == Navigation.NewTab, 1.0, 28);
                    }
                    var content = new StackPanel { Spacing = 7, HorizontalAlignment = HorizontalAlignment.Center, Children = { iconContainer, new TextBlock { Text = label, FontSize = 11, Foreground = _theme.TextSecondaryBrush, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center } } };
                    site = new Button { Content = content, Height = 76, Padding = new(8), CornerRadius = new(SlateTheme.RadiusMedium), Background = _theme.SurfaceRaisedBrush, BorderBrush = _theme.DividerBrush, BorderThickness = new(1), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
                    AutomationProperties.SetName(site, "Open " + label);
                    site.PointerEntered += (_, _) => site.Background = _theme.SurfaceInteractiveHoverBrush;
                    site.PointerExited += (_, _) => site.Background = _theme.SurfaceRaisedBrush;
                    site.Click += async (_, _) => { _focusedTabId = tab.Id; await RunAsync(() => NavigateAsync(entry.Url)); };
                }
                Grid.SetColumn(site, i); sites.Children.Add(site);
            }
            center.Children.Add(sites);
        }
        grid.Children.Add(composition);
        var footer = new Grid { VerticalAlignment = VerticalAlignment.Bottom };
        footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        if (tab.IsTemporary)
            footer.Children.Add(new TextBlock { Text = "Temporary tab", FontSize = 11, Foreground = _theme.TextMutedBrush, VerticalAlignment = VerticalAlignment.Center });
        var shortcuts = new TextBlock { Text = "Ctrl+L  Address     Ctrl+K  Commands", FontSize = 11, Foreground = _theme.TextMutedBrush };
        Grid.SetColumn(shortcuts, 1); footer.Children.Add(shortcuts);
        grid.Children.Add(footer); return grid;
    }

    internal Button BuildNewTabTile()
    {
        var iconContainer = new Grid { Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        iconContainer.Children.Add(new FontIcon
        {
            Glyph = "\uE710",
            FontFamily = new("Segoe Fluent Icons"),
            FontSize = 20,
            Foreground = _theme.AccentPrimaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        var content = new StackPanel
        {
            Spacing = 7,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                iconContainer,
                new TextBlock
                {
                    Text = "New tab",
                    FontSize = 11,
                    Foreground = _theme.TextSecondaryBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            }
        };
        var site = new Button
        {
            Content = content,
            Height = 76,
            Padding = new(8),
            CornerRadius = new(SlateTheme.RadiusMedium),
            Background = _theme.SurfaceRaisedBrush,
            BorderBrush = _theme.DividerBrush,
            BorderThickness = new(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        AutomationProperties.SetName(site, "New tab");
        site.PointerEntered += (_, _) => site.Background = _theme.SurfaceInteractiveHoverBrush;
        site.PointerExited += (_, _) => site.Background = _theme.SurfaceRaisedBrush;
        site.Click += async (_, _) => await RunAsync(() => NewTabAsync());
        return site;
    }

    private StackPanel BuildError(BrowserTab tab, string message)
    {
        var panel = new StackPanel { MaxWidth = 500, Spacing = 18, Padding = new(28), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = "This page couldn’t load", FontSize = 28 });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Opacity = .7 });
        panel.Children.Add(IconButton("\uE72C", "Reload page", async () => { DisposeRuntime(tab.Id); await RefreshAsync(); }));
        var runtimeLink = new HyperlinkButton { Content = "Install Microsoft WebView2 Runtime", NavigateUri = new Uri("https://developer.microsoft.com/microsoft-edge/webview2/") };
        panel.Children.Add(runtimeLink); return panel;
    }

    private async Task SleepTabAsync(Guid id, bool manual = false)
    {
        var tab = _session.State.Tabs.FirstOrDefault(t => t.Id == id);
        if (tab is null || !_runtimes.TryGetValue(id, out var runtime) || runtime.Sleeping || runtime.Suspended || runtime.View.CoreWebView2 is not { } core) return;
        if (IsVisible(id) || tab.IsLoading || core.IsDocumentPlayingAudio || _downloadOperations.Values.Any(d => d.State == CoreWebView2DownloadState.InProgress))
        {
            if (manual) Notify("Visible, loading, audible, or downloading tabs stay awake."); return;
        }
        runtime.Sleeping = true;
        try
        {
            // WinUI delays hiding the underlying controller by 200 ms to avoid a flash.
            // Suspension requires that controller (not just the XAML element) to be hidden.
            await Task.Delay(250);
            if (!IsLive(tab, runtime) || IsVisible(id) || tab.IsLoading || core.IsDocumentPlayingAudio) return;
            bool suspended = await core.TrySuspendAsync();
            if (!IsLive(tab, runtime)) return;
            if (IsVisible(id)) { if (suspended) core.Resume(); tab.IsSleeping = false; }
            else
            {
                runtime.Suspended = suspended;
                tab.IsSleeping = suspended;
                if (suspended) runtime.Passwords?.InvalidateForSleep();
            }
            RenderSidebar(); QueueSave();
        }
        catch (Exception ex) { if (manual) Notify("This tab could not sleep: " + ex.Message); }
        finally { runtime.Sleeping = false; }
    }

    private void SleepIdleTabs()
    {
        int minutes = _session.State.Settings.SleepAfterMinutes;
        if (minutes == 0) return;
        var threshold = DateTimeOffset.UtcNow.AddMinutes(-minutes);
        foreach (var tab in _session.State.Tabs.Where(t => t.LastAccessed < threshold && !t.IsSleeping).ToList()) _ = SleepTabAsync(tab.Id);
    }

    private void DisposeRuntime(Guid id) => _tabManager.DisposeRuntime(id);

    private async Task SavePageAsync()
    {
        var tab = FocusedTab;
        if (CurrentCore() is not { } core || tab.Url == Navigation.NewTab)
        {
            Notify("Open a webpage to save.");
            return;
        }

        var title = string.IsNullOrWhiteSpace(tab.Title) ? "page" : tab.Title;
        var defaultName = DownloadSafety.SanitizeFileName(title) + ".mhtml";

        var picker = new Windows.Storage.Pickers.FileSavePicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
        picker.SuggestedFileName = defaultName;
        picker.FileTypeChoices.Add("Web Archive (*.mhtml)", new List<string> { ".mhtml" });
        picker.FileTypeChoices.Add("HTML Document (*.html)", new List<string> { ".html", ".htm" });

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        await SavePageToFileAsync(file.Path);
    }

    internal async Task SavePageToFileAsync(string targetPath)
    {
        var tab = FocusedTab;
        if (CurrentCore() is not { } core || tab.Url == Navigation.NewTab)
        {
            Notify("Open a webpage to save.");
            return;
        }

        var dir = Path.GetDirectoryName(targetPath);
        if (!DownloadSafety.IsSafeLocalDirectory(dir))
        {
            Notify("Cannot save page to an unsafe location.");
            return;
        }

        var ext = Path.GetExtension(targetPath).ToLowerInvariant();
        if (!DownloadSafety.IsSafeLocalFilePath(targetPath) || ext is not (".mhtml" or ".html" or ".htm"))
        {
            Notify("Pages can only be saved as HTML or MHTML in a safe local folder.");
            return;
        }
        targetPath = Path.GetFullPath(targetPath.Trim());

        try
        {
            if (ext is ".mhtml")
            {
                try
                {
                    var json = await core.CallDevToolsProtocolMethodAsync("Page.captureSnapshot", "{\"format\":\"mhtml\"}");
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("data", out var dataElem))
                    {
                        var mhtml = dataElem.GetString();
                        if (!string.IsNullOrEmpty(mhtml))
                        {
                            await WritePageFileSafelyAsync(targetPath, mhtml);
                            Notify($"Saved page to {Path.GetFileName(targetPath)}");
                            return;
                        }
                    }
                }
                catch { }
            }

            var rawHtml = await core.ExecuteScriptAsync("document.documentElement.outerHTML");
            var html = System.Text.Json.JsonSerializer.Deserialize<string>(rawHtml) ?? "";
            var fullDoc = "<!DOCTYPE html>\n" + html;
            var htmlTarget = targetPath;
            if (ext == ".mhtml")
                htmlTarget = DownloadSafety.ReserveSafeDestinationPath(dir!, Path.ChangeExtension(Path.GetFileName(targetPath), ".html"), "page.html");
            try
            {
                await WritePageFileSafelyAsync(htmlTarget, fullDoc);
            }
            catch
            {
                if (!string.Equals(htmlTarget, targetPath, StringComparison.OrdinalIgnoreCase))
                    DownloadSafety.TryDeleteIncompleteFile(htmlTarget);
                throw;
            }
            Notify(ext == ".mhtml"
                ? $"MHTML capture was unavailable. Saved HTML to {Path.GetFileName(htmlTarget)}"
                : $"Saved page to {Path.GetFileName(htmlTarget)}");
        }
        catch (Exception ex)
        {
            Notify($"Failed to save page: {ex.Message}");
        }
    }

    private static async Task WritePageFileSafelyAsync(string targetPath, string content)
    {
        if (!DownloadSafety.IsSafeLocalFilePath(targetPath)) throw new IOException("Unsafe page destination.");
        string directory = Path.GetDirectoryName(targetPath)!;
        string temporary = DownloadSafety.ReserveSafeDestinationPath(directory, Path.GetFileName(targetPath) + ".slate-part", "page.slate-part");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Truncate, FileAccess.Write, FileShare.None, 8192,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                await writer.WriteAsync(content);
                await writer.FlushAsync();
                stream.Flush(true);
            }
            if (!DownloadSafety.IsSafeLocalFilePath(targetPath) || !DownloadSafety.IsSafeLocalDirectory(directory))
                throw new IOException("The page destination changed while it was being written.");
            File.Move(temporary, targetPath, true);
        }
        finally
        {
            DownloadSafety.TryDeleteIncompleteFile(temporary);
        }
    }

    private const int MaximumViewSourceCharacters = 4 * 1024 * 1024;

    private async Task ViewSourceAsync(string? targetUrl = null)
    {
        var tab = FocusedTab;
        var url = targetUrl ?? tab.Url;
        if (string.IsNullOrWhiteSpace(url) || url == Navigation.NewTab)
        {
            Notify("Open a page before viewing source.");
            return;
        }

        if (Navigation.IsViewSourceUrl(url)) return;

        var viewSourceUrl = "view-source:" + url;
        await NewTabAsync(viewSourceUrl, tab.IsTemporary, tab.IsPrivate);
    }

    private async Task LoadViewSourceAsync(BrowserTab tab, TabRuntime runtime, CoreWebView2 core, string viewSourceUrl)
    {
        core.Settings.IsScriptEnabled = false;
        var targetUrl = viewSourceUrl["view-source:".Length..].Trim();
        tab.Title = "Source: " + (Navigation.IsWebUrl(targetUrl) || Navigation.IsLocalFileUrl(targetUrl) ? Navigation.DisplayHost(targetUrl) : targetUrl);

        string sourceContent;
        try
        {
            if (Navigation.IsLocalFileUrl(targetUrl))
            {
                if (!Navigation.TryGetSafeLocalFilePath(targetUrl, out var filePath)) throw new IOException("Unsafe local source path.");
                await using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
                sourceContent = await ReadBoundedSourceAsync(file);
            }
            else if (Navigation.IsWebUrl(targetUrl))
            {
                var existingTab = _session.State.Tabs.FirstOrDefault(t => t.Id != tab.Id &&
                    (string.Equals(t.Url, targetUrl, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(t.Url.TrimEnd('/'), targetUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)));

                if (existingTab is not null && _runtimes.TryGetValue(existingTab.Id, out var existingRuntime) && existingRuntime.View.CoreWebView2 is { } existingCore)
                {
                    string? retrievedSource = null;
                    try
                    {
                        var treeJson = await existingCore.CallDevToolsProtocolMethodAsync("Page.getResourceTree", "{}");
                        using var treeDoc = System.Text.Json.JsonDocument.Parse(treeJson);
                        if (treeDoc.RootElement.TryGetProperty("frameTree", out var ft) &&
                            ft.TryGetProperty("frame", out var frm) &&
                            frm.TryGetProperty("id", out var idProp))
                        {
                            var frameId = idProp.GetString();
                            if (!string.IsNullOrEmpty(frameId))
                            {
                                var paramsJson = System.Text.Json.JsonSerializer.Serialize(new { frameId = frameId, url = targetUrl });
                                var contentJson = await existingCore.CallDevToolsProtocolMethodAsync("Page.getResourceContent", paramsJson);
                                using var contentDoc = System.Text.Json.JsonDocument.Parse(contentJson);
                                if (contentDoc.RootElement.TryGetProperty("content", out var contentProp))
                                {
                                    retrievedSource = contentProp.GetString();
                                }
                            }
                        }
                    }
                    catch
                    {
                        // CDP extraction failed or unsupported; fall back to live DOM extraction
                    }

                    if (!string.IsNullOrEmpty(retrievedSource))
                    {
                        sourceContent = BoundViewSource(retrievedSource);
                    }
                    else
                    {
                        var raw = await existingCore.ExecuteScriptAsync($"document.documentElement.outerHTML.slice(0,{MaximumViewSourceCharacters + 1})");
                        sourceContent = "<!DOCTYPE html>\n" + BoundViewSource(System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? "");
                    }
                }
                else
                {
                    // No live browsing context exists for this target.
                    // Fall back to a strictly constrained, size-bounded fetch using standard browser headers.
                    using var handler = new HttpClientHandler
                    {
                        CheckCertificateRevocationList = true,
                        AutomaticDecompression = DecompressionMethods.All
                    };
                    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(core.Settings.UserAgent.Length > 0 ? core.Settings.UserAgent : "Mozilla/5.0 Slate/1.0");
                    using var response = await client.GetAsync(targetUrl, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync();
                    sourceContent = await ReadBoundedSourceAsync(stream);
                }
            }
            else
            {
                sourceContent = "<!-- Empty or unsupported source scheme -->";
            }
        }
        catch (Exception ex)
        {
            sourceContent = "<!-- Failed to load source: " + WebUtility.HtmlEncode(ex.Message) + " -->";
        }

        var formattedHtml = _navigationCoordinator.BuildViewSourceHtml(targetUrl, sourceContent);
        core.NavigateToString(formattedHtml);
        UpdateChrome();
        RenderSidebar();
    }

    private static string BoundViewSource(string source)
        => source.Length <= MaximumViewSourceCharacters ? source :
            source[..MaximumViewSourceCharacters] + "\n<!-- Source truncated by Slate -->";

    private static async Task<string> ReadBoundedSourceAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var buffer = new char[8192];
        var builder = new System.Text.StringBuilder();
        while (builder.Length <= MaximumViewSourceCharacters)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaximumViewSourceCharacters + 1 - builder.Length)));
            if (read == 0) break;
            builder.Append(buffer, 0, read);
        }
        return BoundViewSource(builder.ToString());
    }

    private async Task OpenLocalFileAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".html");
        picker.FileTypeFilter.Add(".htm");
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add(".svg");
        picker.FileTypeFilter.Add(".pdf");
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add("*");

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        await OpenLocalFileDirectAsync(file.Path);
    }

    internal async Task OpenLocalFileDirectAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        var candidateUrl = Uri.TryCreate(filePath, UriKind.Absolute, out var existingUri) && existingUri.IsFile
            ? existingUri.AbsoluteUri
            : null;
        if (candidateUrl is null || !Navigation.TryGetSafeLocalFilePath(candidateUrl, out var safePath))
        {
            Notify("Opening unsafe local, network, device, or executable paths is blocked.");
            return;
        }

        if (!File.Exists(safePath))
        {
            Notify("File does not exist: " + Path.GetFileName(safePath));
            return;
        }

        var ext = Path.GetExtension(safePath);
        if (Navigation.IsDangerousExtension(ext))
        {
            Notify($"Opening dangerous file type ({ext}) is blocked.");
            return;
        }

        var fileUrl = new Uri(safePath).AbsoluteUri;
        await NavigateAsync(fileUrl);
    }
}
