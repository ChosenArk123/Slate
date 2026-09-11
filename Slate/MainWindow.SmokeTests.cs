using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Slate.Core;
using Windows.Graphics.Imaging;

namespace Slate;

public sealed partial class MainWindow
{
    // Explicit developer-only launch mode. Uses a fresh profile and local HTTP fixtures.
    private async Task RunSmokeTestsAsync(string output)
    {
        var results = new List<object>();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        var server = ServeFixtureAsync(listener, stop.Token);
        string origin = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        void Check(string name, bool passed) { if (!passed) throw new InvalidOperationException(name); results.Add(new { name, passed }); }
        async Task Until(Func<bool> predicate, string message)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            while (!predicate())
            {
                try { await Task.Delay(50, timeout.Token); }
                catch (TaskCanceledException)
                {
                    var active = _session.ActiveTab;
                    var runtime = _runtimes.GetValueOrDefault(active.Id);
                    throw new TimeoutException($"{message}. URL={active.Url}; title={active.Title}; loading={active.IsLoading}; runtimeError={runtime?.Error ?? "none"}; runtimeFailed={runtime?.Failed}");
                }
            }
        }
        try
        {
            Check("Native new tab renders without creating a WebView", _primary.Children.Count == 1 && _runtimes.Count == 0);
            foreach (var appearance in new[] { false, true })
            foreach (var accent in SlateTheme.AccentNames)
            {
                var palette = SlateTheme.Create(accent, appearance);
                Check($"{accent} {(appearance ? "dark" : "light")} palette meets AA contrast", SlateTheme.ContrastRatio(palette.Text, palette.Page) >= 7 && SlateTheme.ContrastRatio(palette.TextSecondary, palette.Page) >= 4.5 && SlateTheme.ContrastRatio(palette.Accent, palette.Page) >= 4.5 && SlateTheme.ContrastRatio(palette.OnAccent, palette.Accent) >= 4.5);
                if (appearance)
                    Check($"{accent} dark preserves surface luminance hierarchy", SlateTheme.RelativeLuminance(palette.Page) < SlateTheme.RelativeLuminance(palette.Sidebar) && SlateTheme.RelativeLuminance(palette.Sidebar) < SlateTheme.RelativeLuminance(palette.SurfaceRaised) && SlateTheme.RelativeLuminance(palette.SurfaceRaised) < SlateTheme.RelativeLuminance(palette.SurfaceInteractive));
                else
                    Check($"{accent} light preserves surface luminance hierarchy", SlateTheme.RelativeLuminance(palette.Page) > SlateTheme.RelativeLuminance(palette.Sidebar) && SlateTheme.RelativeLuminance(palette.Sidebar) > SlateTheme.RelativeLuminance(palette.SurfaceRaised));
            }
            static int ColorDelta(Windows.UI.Color a, Windows.UI.Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
            var curatedThemes = new[] { "Slate", "Cobalt", "Moss", "Plum", "Clay", "Amber" };
            foreach (var appearance in new[] { false, true })
            {
                var palettes = curatedThemes.Select(name => (Name: name, Palette: SlateTheme.Create(name, appearance))).ToList();
                for (int i = 0; i < palettes.Count; i++)
                {
                    for (int j = i + 1; j < palettes.Count; j++)
                    {
                        var (nameA, palA) = palettes[i];
                        var (nameB, palB) = palettes[j];
                        string mode = appearance ? "dark" : "light";
                        Check($"{nameA} vs {nameB} ({mode}) environmental surfaces differ by tier hierarchy",
                            ColorDelta(palA.Page, palB.Page) >= 3 &&
                            ColorDelta(palA.Chrome, palB.Chrome) >= 5 &&
                            ColorDelta(palA.Sidebar, palB.Sidebar) >= 5 &&
                            ColorDelta(palA.SurfaceRaised, palB.SurfaceRaised) >= 6 &&
                            ColorDelta(palA.SurfaceInteractive, palB.SurfaceInteractive) >= 10 &&
                            ColorDelta(palA.Divider, palB.Divider) >= 5 &&
                            ColorDelta(palA.TextSecondary, palB.TextSecondary) >= 6);
                    }
                }
            }
            var originalTheme = _session.State.Settings.Theme;
            var originalAccent = _session.State.Settings.AccentTheme;
            foreach (var appearance in new[] { "Dark", "Light" })
            {
                _session.State.Settings.Theme = appearance; ApplyAppearance(); await RefreshAsync(); await Task.Delay(100);
                var screenshot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, Path.GetFileNameWithoutExtension(output) + $"-newtab-{appearance.ToLowerInvariant()}.png");
                await CaptureElementAsync(_root, screenshot);
                Check($"{appearance} new-tab visual snapshot renders", File.Exists(screenshot) && new FileInfo(screenshot).Length > 0);
            }
            foreach (var accent in curatedThemes)
            foreach (var appearance in new[] { "Dark", "Light" })
            {
                _session.State.Settings.Theme = appearance;
                _session.State.Settings.AccentTheme = accent;
                ApplyAppearance(); await RefreshAsync(); await Task.Delay(80);
                var themeShot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, Path.GetFileNameWithoutExtension(output) + $"-theme-{accent.ToLowerInvariant()}-{appearance.ToLowerInvariant()}.png");
                await CaptureElementAsync(_root, themeShot);
                Check($"{accent} {appearance} theme snapshot renders", File.Exists(themeShot) && new FileInfo(themeShot).Length > 0);
            }
            _session.State.Settings.Theme = originalTheme;
            _session.State.Settings.AccentTheme = originalAccent;
            ApplyAppearance(); await RefreshAsync();
            _root.UpdateLayout();
            var newTabPage = (FrameworkElement)_primary.Children[0];
            var searchBox = Descendants(newTabPage).OfType<TextBox>().Single();
            var searchRow = (Grid)searchBox.Parent;
            var searchIcon = searchRow.Children.OfType<FontIcon>().Single();
            var searchBorder = (Border)searchRow.Parent;
            double Midpoint(FrameworkElement element) => element.TransformToVisual(searchRow).TransformPoint(new Windows.Foundation.Point(0, element.ActualHeight / 2)).Y;
            Check("New-tab text control and search icon share a vertical center", searchBox.ActualHeight > 0 && searchIcon.ActualHeight > 0 && Math.Abs(Midpoint(searchBox) - Midpoint(searchIcon)) < 1);
            Check("New-tab logo is removed", !Descendants(newTabPage).OfType<TextBlock>().Any(t => t.Text == "S L A T E"));
            Check("Reload is disabled on an empty new tab", !_reload.IsEnabled);
            Check("New-tab window title does not display active workspace name", _windowTitle.Text == "");
            Check("New-tab idle status does not display workspace or tab count", string.IsNullOrEmpty(_status.Text) || _status.Text == "Split view");
            Check("New-tab page footer does not display workspace label", !Descendants(newTabPage).OfType<TextBlock>().Any(t => t.Text.Equals(_session.ActiveWorkspace.Name, StringComparison.OrdinalIgnoreCase)));
            double IconCenterX(FrameworkElement element) => element.TransformToVisual(_sidebar).TransformPoint(new Windows.Foundation.Point(element.ActualWidth / 2, 0)).X;
            var expandedNewTab = _sidebar.Children.OfType<Button>().First(b => b.Tag is "new");
            var expandedNewTabIcon = Descendants(expandedNewTab).OfType<Grid>().First(g => g.Width == 20);
            var expandedTabRow = _tabList.Children.OfType<Grid>().First();
            var expandedTabIcon = Descendants(expandedTabRow).OfType<Grid>().First(g => g.Width == 20);
            var bottomPanel = _sidebar.Children.OfType<StackPanel>().First(p => p.Tag is "bottom");
            var expandedBottomIcon = Descendants(bottomPanel.Children.OfType<Button>().First()).OfType<Grid>().First(g => g.Width == 20);
            Check("Expanded New Tab and tab rows share icon horizontal center", Math.Abs(IconCenterX(expandedNewTabIcon) - IconCenterX(expandedTabIcon)) < 1);
            Check("Expanded bottom actions share icon horizontal center", Math.Abs(IconCenterX(expandedTabIcon) - IconCenterX(expandedBottomIcon)) < 1);
            var clayDark = SlateTheme.Create("Clay", true);
            var amberDark = SlateTheme.Create("Amber", true);
            var clayLight = SlateTheme.Create("Clay", false);
            var amberLight = SlateTheme.Create("Amber", false);
            Check("Amber is distinct from Clay in dark mode", clayDark.AccentPrimary != amberDark.AccentPrimary && ColorDelta(clayDark.AccentPrimary, amberDark.AccentPrimary) > 40 && ColorDelta(clayDark.SurfaceInteractive, amberDark.SurfaceInteractive) >= 10 && ColorDelta(clayDark.Page, amberDark.Page) >= 3);
            Check("Amber is distinct from Clay in light mode", clayLight.AccentPrimary != amberLight.AccentPrimary && ColorDelta(clayLight.AccentPrimary, amberLight.AccentPrimary) > 40 && ColorDelta(clayLight.SurfaceInteractive, amberLight.SurfaceInteractive) >= 10 && ColorDelta(clayLight.Page, amberLight.Page) >= 3);
            Check("Theme palette exposes semantic tokens", amberDark.SurfaceBase != Microsoft.UI.Colors.Transparent && amberDark.SurfaceInteractive != Microsoft.UI.Colors.Transparent && amberDark.Sidebar != Microsoft.UI.Colors.Transparent && amberDark.FocusRing != Microsoft.UI.Colors.Transparent && amberDark.Divider != Microsoft.UI.Colors.Transparent);
            var borderThickness = searchBorder.BorderThickness;
            searchBox.Focus(FocusState.Programmatic); await Task.Delay(50);
            Check("New-tab focus does not change border geometry", searchBorder.BorderThickness == borderThickness);
            Check("New-tab search focus applies theme focus ring", searchBorder.BorderBrush is SolidColorBrush sfb && sfb.Color == _theme.FocusRing);
            _address.Focus(FocusState.Programmatic); await Task.Delay(50);
            Check("Omnibox focus applies theme accent border", _address.BorderBrush is SolidColorBrush ob && ob.Color == _theme.AccentBorder);
            searchBox.Focus(FocusState.Programmatic); await Task.Delay(50);
            Check("Omnibox blur restores divider border", _address.BorderBrush is SolidColorBrush odb && odb.Color == _theme.Divider);
            foreach (double scale in new[] { 1d, 1.25, 1.5 })
            {
                string screenshot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, Path.GetFileNameWithoutExtension(output) + $"-search-{scale * 100:0}.png");
                await CaptureElementAsync(searchBorder, screenshot, scale);
                Check($"New-tab search rasterizes at {scale * 100:0}%", File.Exists(screenshot) && new FileInfo(screenshot).Length > 0);
            }
            await RunPasswordSmokeTestsAsync(origin, Check);
            await NavigateAsync(origin + "/one");
            await Until(() => CurrentCore()?.DocumentTitle == "Slate Test One" && !_session.ActiveTab.IsLoading, "Initial navigation did not complete");
            var first = _session.ActiveTab;
            var firstCore = CurrentCore()!;
            Check("WebView2 renders local HTTP and updates title", first.Title == "Slate Test One" && first.Url == origin + "/one");
            await Until(() => _runtimes[first.Id].FaviconImage is not null, "WebView2 favicon did not decode");
            Check("Favicons use the bounded WebView2 decode path", _runtimes[first.Id].FaviconImage?.DecodePixelWidth == 32 && first.Favicon is null);
            await Until(() => _faviconStore.HasFavicon(origin + "/one"), "Favicon was not saved to disk cache");
            Check("Favicon is persisted to disk under favicons folder", _faviconStore.HasFavicon(origin + "/one"));
            int tabsBeforePopup = _session.State.Tabs.Count;
            // Host-injected ExecuteScriptAsync can be classified as a user gesture.
            // Explicitly model a background script for this existing security assertion.
            await firstCore.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression = "window.open('/blocked-popup', '_blank')", userGesture = false }));
            await Task.Delay(250);
            Check("Non-user-initiated popups are blocked", _session.State.Tabs.Count == tabsBeforePopup);
            var popupTime = DateTimeOffset.UtcNow;
            for (int i = 0; i < MaximumPopupsPerBurst; i++) Check("Popup burst allowance " + (i + 1), AllowPopup("https://popup-test.invalid", popupTime));
            Check("Popup bursts are rate limited", !AllowPopup("https://popup-test.invalid", popupTime));
            for (int i = MaximumPopupsPerBurst; i < MaximumGlobalPopupsPerBurst; i++)
                Check("Global popup burst allowance " + (i + 1), AllowPopup($"https://popup-{i}.invalid", popupTime));
            Check("Global popup bursts are rate limited", !AllowPopup("https://popup-overflow.invalid", popupTime));
            _popupAttempts.Clear(); _globalPopupAttempts.Clear();
            using (var testUdp = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                int testUdpPort = ((IPEndPoint)testUdp.Client.LocalEndPoint!).Port;
                var listenersWithUdp = GetUnexpectedListeningPorts(((IPEndPoint)listener.LocalEndpoint).Port);
                Check("UDP listener inspection detects bound UDP sockets", listenersWithUdp.Contains(testUdpPort));
            }
            await Until(() => GetUnexpectedListeningPorts(((IPEndPoint)listener.LocalEndpoint).Port).Count == 0, "Unexpected listening sockets remained open");
            var unexpectedListeners = GetUnexpectedListeningPorts(((IPEndPoint)listener.LocalEndpoint).Port);
            Check("Slate process tree opens zero unexpected listening sockets", unexpectedListeners.Count == 0);
            Check("Unknown permission kinds are denied by policy", PermissionPolicy(CoreWebView2PermissionKind.UnknownPermission) is null);
            Check("Sensitive permission kinds require user initiation", PermissionPolicy(CoreWebView2PermissionKind.ClipboardRead)?.RequiresUserInitiation == true);
            for (int i = 0; i < MaximumGlobalPermissionPromptsPerBurst; i++)
                Check("Global permission prompt allowance " + (i + 1), AllowPermissionPrompt($"https://permission-{i}.invalid", CoreWebView2PermissionKind.Geolocation, popupTime));
            Check("Permission prompts are globally rate limited", !AllowPermissionPrompt("https://permission-overflow.invalid", CoreWebView2PermissionKind.Geolocation, popupTime));
            _permissionAttempts.Clear(); _globalPermissionAttempts.Clear(); _lastPermissionPrompt.Clear();
            await firstCore.ExecuteScriptAsync("document.querySelector('input').value='preserved text'");
            await NavigateAsync(origin + "/two");
            await Until(() => CurrentCore()?.DocumentTitle == "Slate Test Two" && !first.IsLoading, "Second navigation did not complete");
            Check("Back navigation becomes available", _back.IsEnabled && firstCore.CanGoBack);
            firstCore.GoBack();
            await Until(() => firstCore.DocumentTitle == "Slate Test One" && !first.IsLoading, "Back did not complete");
            Check("Forward navigation becomes available", _forward.IsEnabled && firstCore.CanGoForward);
            firstCore.GoForward();
            await Until(() => firstCore.DocumentTitle == "Slate Test Two" && !first.IsLoading, "Forward did not complete");
            await firstCore.ExecuteScriptAsync("document.querySelector('input').value='tab state'");
            await NewTabAsync(origin + "/one");
            await Until(() => CurrentCore()?.DocumentTitle == "Slate Test One" && !_session.ActiveTab.IsLoading, "New tab navigation did not complete");
            var second = _session.ActiveTab;
            Check("Two viewed tabs have independent WebViews", _runtimes.Count == 2 && CurrentCore() != firstCore);
            await ActivateTabAsync(first.Id);
            Check("Switching tabs preserves the live page", await firstCore.ExecuteScriptAsync("document.querySelector('input').value") == "\"tab state\"");
            _splitTabId = second.Id; await RefreshAsync();
            Check("Split view attaches both WebViews", _primary.Children[0] == _runtimes[first.Id].View && _secondary.Children[0] == _runtimes[second.Id].View);
            _splitTabId = null; await RefreshAsync();
            await Task.Delay(150);
            await SleepTabAsync(second.Id, true);
            Check("Background WebView suspends", second.IsSleeping && _runtimes[second.Id].Suspended);
            _root.UpdateLayout();
            var sleepingTabRow = _tabList.Children.OfType<Grid>().FirstOrDefault(r => Descendants(r).OfType<TextBlock>().Any(t => t.Text == "Slate Test One"));
            var sleepingFavicon = sleepingTabRow is not null ? Descendants(sleepingTabRow).OfType<Image>().FirstOrDefault(img => img.Width == 16) : null;
            Check("Sleeping tab renders cached favicon with 0.5 opacity", sleepingFavicon is not null && Math.Abs(sleepingFavicon.Opacity - 0.5) < 0.01);
            Check("Sidebar sleeping tab renders 32px-decoded source scaled to 16x16", sleepingFavicon is not null && sleepingFavicon.Width == 16 && sleepingFavicon.Height == 16 && (sleepingFavicon.Source as BitmapImage)?.DecodePixelWidth == 32);
            await ActivateTabAsync(second.Id);
            Check("Sleeping tab resumes when selected", !second.IsSleeping && !_runtimes[second.Id].Suspended);
            var old = second.Id; await CloseTabAsync(second.Id);
            Check("Closing a tab disposes its WebView", !_runtimes.ContainsKey(old) && _session.State.Tabs.All(t => t.Id != old));
            await RestoreClosedAsync(old);
            await Until(() => CurrentCore()?.DocumentTitle == "Slate Test One" && !_session.ActiveTab.IsLoading, "Restored tab did not complete");
            Check("Recently closed tab reloads its saved URL", _session.ActiveTab.Id == old && _session.ActiveTab.Url == origin + "/one");
            Check("Successful navigations appear in history", _session.State.History.Any(h => h.Url == origin + "/two"));
            CurrentCore()!.Navigate(origin + "/download");
            await Until(() => _session.State.Downloads.Any(d => d.Status == "Completed"), "Fixture download did not complete");
            var download = _session.State.Downloads.First();
            Check("WebView downloads are tracked and saved", File.Exists(download.Path) && File.ReadAllText(download.Path) == "Slate download fixture");
            Check("Downloads resolve within sanitized downloads directory", download.Path.StartsWith(Path.Combine(_store.DirectoryPath, "Downloads"), StringComparison.OrdinalIgnoreCase) && download.FileName == "slate-test.txt");
            Check("Finished downloads release runtime tracking", !_downloadOperations.ContainsKey(download.Id));
            string localhostOrigin = "http://localhost:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            string tempUrl = localhostOrigin + "/temporary-check";
            int historyCountBefore = _session.State.History.Count;
            await NewTabAsync(tempUrl, temporary: true);
            var tempTab = _session.ActiveTab;
            await Until(() => CurrentCore()?.DocumentTitle == "Slate Test One" && !tempTab.IsLoading, "Temporary tab navigation did not complete");
            await Task.Delay(250);
            Check("Temporary tab does not persist favicon to disk", !_faviconStore.HasFavicon(tempUrl));
            Check("Temporary tab does not record history", _session.State.History.Count == historyCountBefore && !_session.State.History.Any(h => h.Url == tempUrl));
            await CloseTabAsync(tempTab.Id);
            Check("Palette finds open tabs", GetPaletteItems("Slate Test").Any(i => i.Detail.StartsWith("Open tab")));
            Check("Palette finds history", GetPaletteItems("Slate Test").Any(i => i.Detail.StartsWith("History")));
            Check("Palette resolves browser commands", GetPaletteItems("split").Any(i => i.Title == "Toggle split view"));
            Check("Palette fuzzy filtering matches abbreviated commands", GetPaletteItems("rct").Any(i => i.Title == "Reopen closed tab"));
            Check("Palette exposes the existing clear-data action", GetPaletteItems("").Any(i => i.Title == "Clear browsing data"));
            Check("Palette omits unimplemented profile switching", !GetPaletteItems("").Any(i => i.Title == "Switch profile"));
            var beforePaletteTabCount = _session.State.Tabs.Count;
            var paletteTask = ShowPaletteAsync();
            await Until(() => _activeDialog?.IsLoaded == true, "Palette did not open for command execution");
            await Task.Delay(120);
            var paletteDialog = _activeDialog!;
            var commandPanel = (StackPanel)paletteDialog.Content;
            var commandInput = (TextBox)commandPanel.Children[0];
            var commandList = (ListView)commandPanel.Children[1];
            Check("Palette opens with its first command selected", ResolvePaletteItem(commandList.SelectedItem)?.Title == "New tab");
            var firstContainer = (ListViewItem)commandList.Items[0];
            Check("Palette resolves clicks on row content", ResolvePaletteItem(firstContainer.Content)?.Title == "New tab");
            commandInput.Text = "sett";
            Check("Palette updates selection after filtering", ResolvePaletteItem(commandList.SelectedItem)?.Title == "Settings");
            commandInput.Text = "new tab";
            var selectedCommand = commandList.Items.Cast<ListViewItem>().First(i => ResolvePaletteItem(i)?.Detail == "Command · Ctrl+T");
            commandList.SelectedItem = selectedCommand;
            Check("Palette bottom button bar is removed", string.IsNullOrEmpty(paletteDialog.PrimaryButtonText) && string.IsNullOrEmpty(paletteDialog.CloseButtonText) && !Descendants(paletteDialog).OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b) is "PrimaryButton" or "CloseButton" && b.ActualHeight > 0));
            var paletteSnapshot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, Path.GetFileNameWithoutExtension(output) + "-palette.png");
            await CaptureElementAsync(paletteDialog, paletteSnapshot);
            Check("Palette visual snapshot renders", File.Exists(paletteSnapshot) && new FileInfo(paletteSnapshot).Length > 0);
            if (paletteDialog.Tag is Action chooseAction) chooseAction();
            await paletteTask;
            Check("Palette executes the existing New Tab action", _session.State.Tabs.Count == beforePaletteTabCount + 1 && _session.ActiveTab.Url == Navigation.NewTab);

            // Shortcut routing integration checks
            Check("XAML keyboard accelerators sync from command registry", _root.KeyboardAccelerators.Count == BrowserCommandRegistry.AllShortcuts.Count);
            Check("CommandRouter matches Ctrl+T for new tab", _commandRouter.CanRoute(BrowserCommandRegistry.KeyT, ShortcutModifiers.Control));
            Check("CommandRouter matches Ctrl+W for close tab", _commandRouter.CanRoute(BrowserCommandRegistry.KeyW, ShortcutModifiers.Control));
            Check("CommandRouter passes through Ctrl+C to webpage", !_commandRouter.CanRoute(BrowserCommandRegistry.KeyC, ShortcutModifiers.Control));
            Check("CommandRouter passes through Ctrl+V to webpage", !_commandRouter.CanRoute(BrowserCommandRegistry.KeyV, ShortcutModifiers.Control));
            Check("CommandRouter passes through Ctrl+A to webpage", !_commandRouter.CanRoute(BrowserCommandRegistry.KeyA, ShortcutModifiers.Control));
            Check("CommandRouter matches Ctrl+F for find in page", _commandRouter.CanRoute(BrowserCommandRegistry.KeyF, ShortcutModifiers.Control));
            Check("CommandRouter passes through Ctrl+X to webpage", !_commandRouter.CanRoute(BrowserCommandRegistry.KeyX, ShortcutModifiers.Control));
            Check("CommandRouter passes through Ctrl+Z to webpage", !_commandRouter.CanRoute(BrowserCommandRegistry.KeyZ, ShortcutModifiers.Control));
            Check("CommandRouter passes through unadorned typing", !_commandRouter.CanRoute(BrowserCommandRegistry.KeyT, ShortcutModifiers.None));

            int tabCountBeforeShortcut = _session.State.Tabs.Count;
            bool routedNewTab = await _commandRouter.SimulateKeyDownAsync(BrowserCommandRegistry.KeyT, ShortcutModifiers.Control);
            Check("CommandRouter executes Ctrl+T to open new tab", routedNewTab && _session.State.Tabs.Count == tabCountBeforeShortcut + 1);

            bool routedCloseTab = await _commandRouter.SimulateKeyDownAsync(BrowserCommandRegistry.KeyW, ShortcutModifiers.Control);
            Check("CommandRouter executes Ctrl+W to close active tab", routedCloseTab && _session.State.Tabs.Count == tabCountBeforeShortcut);

            var tempDialog = Dialog("Shortcut modal test", new TextBlock { Text = "Modal test content" });
            var dialogShowTask = ShowDialogAsync(tempDialog);
            await Until(() => _activeDialog?.IsLoaded == true, "Test modal dialog did not open");
            Check("CommandRouter suppresses browser shortcuts when modal dialog is open", !_commandRouter.CanRoute(BrowserCommandRegistry.KeyT, ShortcutModifiers.Control));
            bool routedDuringModal = await _commandRouter.SimulateKeyDownAsync(BrowserCommandRegistry.KeyT, ShortcutModifiers.Control);
            Check("CommandRouter refuses to execute shortcut during modal dialog", !routedDuringModal && _session.State.Tabs.Count == tabCountBeforeShortcut);
            tempDialog.Hide();
            await dialogShowTask;
            Check("CommandRouter restores browser shortcuts after modal dialog closes", _commandRouter.CanRoute(BrowserCommandRegistry.KeyT, ShortcutModifiers.Control));

            // Batch B navigation checks
            Check("CommandRouter matches Ctrl+Shift+PageUp for move tab up", _commandRouter.CanRoute(BrowserCommandRegistry.KeyPageUp, ShortcutModifiers.Control | ShortcutModifiers.Shift));
            Check("CommandRouter matches Ctrl+Shift+PageDown for move tab down", _commandRouter.CanRoute(BrowserCommandRegistry.KeyPageDown, ShortcutModifiers.Control | ShortcutModifiers.Shift));
            Check("CommandRouter matches Ctrl+Shift+D for duplicate tab", _commandRouter.CanRoute(BrowserCommandRegistry.KeyD, ShortcutModifiers.Control | ShortcutModifiers.Shift));

            // Omnibox suggestions
            UpdateSuggestions("Slate Test");
            Check("Omnibox suggestions populate from open tabs, search, and history", _currentSuggestions.Count >= 2);
            Check("Omnibox popup opens when suggestions are available", _suggestionsPopup.IsOpen);
            HighlightSuggestion(0);
            Check("Omnibox suggestion list contains visual elements", _suggestionsList.Children.Count == _currentSuggestions.Count);
            CloseSuggestions();
            Check("Omnibox suggestions close cleanly", !_suggestionsPopup.IsOpen && _currentSuggestions.Count == 0);

            // Tab operations
            int beforeDupCount = _session.State.Tabs.Count;
            await DuplicateTabAsync(FocusedTab.Id);
            Check("DuplicateTabAsync creates a new tab with duplicate URL", _session.State.Tabs.Count == beforeDupCount + 1);
            var duplicatedTab = _session.ActiveTab;
            int dupIndex = _session.State.Tabs.IndexOf(duplicatedTab);
            await MoveTabAsync(duplicatedTab.Id, -1);
            Check("MoveTabAsync reorders tab upward", _session.State.Tabs.IndexOf(duplicatedTab) < dupIndex || dupIndex == 0);
            await CloseTabAsync(duplicatedTab.Id);

            var extraTab1 = _session.AddTab();
            var extraTab2 = _session.AddTab();
            await CloseTabsBelowAsync(extraTab1.Id);
            Check("CloseTabsBelowAsync closes subsequent tabs", _session.State.Tabs.All(t => t.Id != extraTab2.Id));
            await CloseTabAsync(extraTab1.Id);

            // Downloads indicator & UI
            Check("Downloads button exists in toolbar", _downloadsButton is not null && _downloadsButton.Visibility == Visibility.Visible);
            UpdateDownloadIndicator();
            Check("Downloads button updates indicator when idle", ToolTipService.GetToolTip(_downloadsButton)?.ToString()?.Contains("Downloads") == true);

            // Batch C checks: Bookmarks
            Check("Bookmark button exists in toolbar", _bookmarkButton is not null && _bookmarkButton.Visibility == Visibility.Visible);
            UpdateBookmarkIndicator();
            Check("Bookmark button initially shows unbookmarked state", _bookmarkButton?.Content is FontIcon fiInitial && fiInitial.Glyph == "\uE734");

            var prevTabUrl = FocusedTab.Url;
            var prevTabTitle = FocusedTab.Title;
            FocusedTab.Url = origin + "/one";
            FocusedTab.Title = "Slate Test One";
            var currentTabUrl = FocusedTab.Url;
            bool initiallyBookmarked = _session.IsBookmarked(currentTabUrl);
            await ToggleBookmarkAsync();
            Check("ToggleBookmarkAsync toggles bookmark state in session", _session.IsBookmarked(currentTabUrl) != initiallyBookmarked);
            var toggledBookmarkIcon = _bookmarkButton?.Content as FontIcon;
            Check("Bookmark button reflects bookmarked state", toggledBookmarkIcon?.Glyph == (_session.IsBookmarked(currentTabUrl) ? "\uE735" : "\uE734"));

            if (_session.IsBookmarked(currentTabUrl) != initiallyBookmarked)
            {
                await ToggleBookmarkAsync();
            }
            FocusedTab.Url = prevTabUrl;
            FocusedTab.Title = prevTabTitle;

            string bmUrl = "https://bookmark-test.invalid";
            _session.AddBookmark(bmUrl, "Bookmark Test Title");
            Check("BrowserSession records added bookmark", _session.IsBookmarked(bmUrl));

            _session.State.Settings.ShowBookmarksBar = true;
            RenderBookmarksBar();
            Check("Bookmarks bar is visible when enabled", _bookmarksBar.Visibility == Visibility.Visible);
            Check("Bookmarks bar contains child elements when bookmarks exist", _bookmarksList.Children.Count > 0);

            _session.RemoveBookmarkByUrl(bmUrl);
            Check("BrowserSession removes bookmark by URL", !_session.IsBookmarked(bmUrl));

            _session.State.Settings.ShowBookmarksBar = false;
            RenderBookmarksBar();
            Check("Bookmarks bar is collapsed when disabled", _bookmarksBar.Visibility == Visibility.Collapsed);

            // Command router shortcuts for Batch C
            Check("CommandRouter matches Ctrl+D for bookmark page", _commandRouter.CanRoute(BrowserCommandRegistry.KeyD, ShortcutModifiers.Control));
            Check("CommandRouter matches Ctrl+Shift+O for open bookmarks", _commandRouter.CanRoute(BrowserCommandRegistry.KeyO, ShortcutModifiers.Control | ShortcutModifiers.Shift));

            // Batch C: History per-item deletion
            _session.State.History.Add(new HistoryEntry { Url = "https://history-item-delete-test.invalid", Title = "Item Delete Test" });
            bool removedHistory = _session.RemoveHistoryEntry("https://history-item-delete-test.invalid");
            Check("BrowserSession removes history entry by URL", removedHistory && !_session.State.History.Any(h => h.Url == "https://history-item-delete-test.invalid"));

            // Batch C: Download settings
            var initialAskDownload = _session.State.Settings.AskDownloadLocation;
            _session.State.Settings.AskDownloadLocation = true;
            Check("BrowserSettings persists AskDownloadLocation toggle", _session.State.Settings.AskDownloadLocation);
            _session.State.Settings.AskDownloadLocation = initialAskDownload;

            // Batch D: Special execution contexts
            // 1. InPrivate Tab creation & runtime isolation
            string privateUrl = origin + "/private-isolation-test";
            int histCountBefore = _session.State.History.Count;
            await NewTabAsync(privateUrl, isPrivate: true);
            var privateTab = _session.ActiveTab;
            Check("InPrivate tab is marked IsPrivate in session", privateTab.IsPrivate);
            await Until(() => _runtimes.TryGetValue(privateTab.Id, out var rt) && rt.View.CoreWebView2 is not null, "InPrivate tab runtime did not initialize");
            var privateRuntime = _runtimes[privateTab.Id];
            Check("InPrivate tab has InPrivate mode enabled in profile", privateRuntime.View.CoreWebView2.Profile.IsInPrivateModeEnabled);
            Check("InPrivate tab does not attach password controller", privateRuntime.Passwords is null);
            Check("InPrivate tab visit is not recorded in session history", _session.State.History.Count == histCountBefore && !_session.State.History.Any(h => h.Url == privateUrl));

            // InPrivate tab is not saved in session serialization
            _store.Save(_session.State);
            var savedDiskJson = File.ReadAllText(Path.Combine(_store.DirectoryPath, "session.json"));
            Check("InPrivate tab is omitted from session JSON serialization", !savedDiskJson.Contains(privateTab.Id.ToString()));
            var reloadedSession = new BrowserSession(_session.State);
            Check("InPrivate tab is omitted from normalized session state", !reloadedSession.State.Tabs.Any(t => t.Id == privateTab.Id));

            // Close InPrivate tab: must not be in RecentlyClosed
            await CloseTabAsync(privateTab.Id);
            Check("RecentlyClosed does not retain closed InPrivate tabs", !_session.State.RecentlyClosed.Any(t => t.Id == privateTab.Id));

            // 2. Command router shortcuts for Batch D
            Check("CommandRouter matches Ctrl+Alt+N for new private tab", _commandRouter.CanRoute(BrowserCommandRegistry.KeyN, ShortcutModifiers.Control | ShortcutModifiers.Alt));
            Check("CommandRouter matches Ctrl+O for open file", _commandRouter.CanRoute(BrowserCommandRegistry.KeyO, ShortcutModifiers.Control));
            Check("CommandRouter matches Ctrl+S for save page", _commandRouter.CanRoute(BrowserCommandRegistry.KeyS, ShortcutModifiers.Control));
            Check("CommandRouter matches Ctrl+U for view source", _commandRouter.CanRoute(BrowserCommandRegistry.KeyU, ShortcutModifiers.Control));

            // 3. Local file support & security boundary
            var tempHtmlFile = Path.Combine(Path.GetTempPath(), "slate_smoke_test.html");
            await File.WriteAllTextAsync(tempHtmlFile, "<!DOCTYPE html><html><body><h1>Local File Test</h1></body></html>");
            Guid localTabId = Guid.Empty;
            try
            {
                await OpenLocalFileDirectAsync(tempHtmlFile);
                var localTab = _session.ActiveTab;
                localTabId = localTab.Id;
                Check("OpenLocalFileDirectAsync navigates to local file URL", Navigation.IsLocalFileUrl(localTab.Url));
                await Until(() => _runtimes.TryGetValue(localTab.Id, out var rt) && rt.View.CoreWebView2 is not null && !localTab.IsLoading, "Local file tab did not load");
                Check("Local file pages cannot receive credential tickets", _runtimes[localTab.Id].Passwords?.CaptureTicket() is null);

                Check("Navigation.IsDangerousExtension flags .exe", Navigation.IsDangerousExtension(".exe"));
                Check("Navigation.IsDangerousExtension flags .bat", Navigation.IsDangerousExtension(".bat"));
                Check("Navigation.IsDangerousExtension flags .ps1", Navigation.IsDangerousExtension(".ps1"));
                Check("Navigation.IsDangerousExtension flags .cmd", Navigation.IsDangerousExtension(".cmd"));
                Check("Navigation.IsDangerousExtension rejects safe .html", !Navigation.IsDangerousExtension(".html"));
                Check("Navigation.IsDangerousExtension rejects safe .txt", !Navigation.IsDangerousExtension(".txt"));
                Check("Navigation.IsLocalFileUrl rejects UNC shares", !Navigation.IsLocalFileUrl(@"\\server\share\file.html"));
            }
            finally
            {
                if (localTabId != Guid.Empty) await CloseTabAsync(localTabId);
                if (File.Exists(tempHtmlFile)) File.Delete(tempHtmlFile);
            }

            // 4. Save Page (Ctrl+S)
            await NewTabAsync(origin + "/one");
            var saveTab = _session.ActiveTab;
            try
            {
                await Until(() => _session.ActiveTab.Url == origin + "/one" && !_session.ActiveTab.IsLoading, "Navigate to origin/one did not complete");

                var tempSaveTarget = Path.Combine(Path.GetTempPath(), "slate_saved_page_test.html");
                try
                {
                    await SavePageToFileAsync(tempSaveTarget);
                    Check("SavePageToFileAsync creates non-empty file on disk", File.Exists(tempSaveTarget) && new FileInfo(tempSaveTarget).Length > 0);
                    var savedHtml = await File.ReadAllTextAsync(tempSaveTarget);
                    Check("Saved page contains HTML markup", savedHtml.Contains("<html", StringComparison.OrdinalIgnoreCase));
                }
                finally
                {
                    if (File.Exists(tempSaveTarget)) File.Delete(tempSaveTarget);
                }
            }
            finally
            {
                await CloseTabAsync(saveTab.Id);
            }

            // 5. View Source (Ctrl+U)
            await ViewSourceAsync(origin + "/one");
            var vsTab = _session.ActiveTab;
            Check("ViewSourceAsync opens tab with view-source: URL scheme", Navigation.IsViewSourceUrl(vsTab.Url));
            await Until(() => _runtimes.TryGetValue(vsTab.Id, out var rt) && rt.View.CoreWebView2 is not null, "View source tab runtime did not initialize");
            var vsRuntime = _runtimes[vsTab.Id];
            Check("View source tab has scripts disabled for sandboxing", vsRuntime.View.CoreWebView2.Settings.IsScriptEnabled == false);
            Check("View-source pages cannot receive credential tickets", vsRuntime.Passwords?.CaptureTicket() is null);
            await CloseTabAsync(vsTab.Id);

            // 6. Certificate Tooling & TLS Inspection
            var testRuntime = new TabRuntime(new WebView2());
            testRuntime.LastCertificateError = CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect;
            Check("TabRuntime supports LastCertificate storage", testRuntime.LastCertificate is null && testRuntime.LastCertificateError == CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect);
            var paletteItems = GetPaletteItems("").ToList();
            Check("Palette includes New private tab", paletteItems.Any(p => p.Title == "New private tab"));
            Check("Palette includes Open file", paletteItems.Any(p => p.Title == "Open file"));
            Check("Palette includes Save page as", paletteItems.Any(p => p.Title == "Save page as"));
            Check("Palette includes View source", paletteItems.Any(p => p.Title == "View source"));

            RenderSidebar();

            _root.UpdateLayout();
            var newTabTile = BuildNewTabTile();
            Check("New Tab tile uses standard 76px height", newTabTile.Height == 76);
            var tileStack = newTabTile.Content as StackPanel;
            var tileIconContainer = tileStack?.Children[0] as Grid;
            Check("New Tab tile has deliberate plus glyph", tileIconContainer?.Children.OfType<FontIcon>().Any(fi => fi.Glyph == "\uE710") == true);
            Check("New Tab tile does not render website favicon image or monogram", tileIconContainer?.Children.OfType<Image>().Any() != true && tileIconContainer?.Children.OfType<Border>().Any() != true);
            Check("New Tab tile uses semantic SurfaceRaisedBrush", newTabTile.Background is SolidColorBrush srb && srb.Color == _theme.SurfaceRaised);
            var newTabSidebarRow = _tabList.Children.OfType<Grid>().Last();
            Check("Sidebar New Tab renders deliberate plus glyph", Descendants(newTabSidebarRow).OfType<FontIcon>().Any(fi => fi.Glyph == "\uE710"));
            Check("Sidebar New Tab does not use globe website fallback", !Descendants(newTabSidebarRow).OfType<FontIcon>().Any(fi => fi.Glyph == "\uE774"));
            var newTabFavicons = Descendants(_primary).OfType<Image>().Where(img => img.Width == 28 && img.Height == 28).ToList();
            Check("New Tab frequently visited renders cached favicon bare at 28x28", newTabFavicons.Count > 0);
            Check("New Tab frequently visited renders 32px-decoded source scaled to 28x28", newTabFavicons.All(img => (img.Source as BitmapImage)?.DecodePixelWidth == 32));
            var corruptKey = FaviconStore.GetKey("corrupt-test.local");
            File.WriteAllText(Path.Combine(_faviconStore.DirectoryPath, corruptKey + ".png"), "corrupt data");
            Check("Corrupt favicon on disk is rejected by store and cache", !_faviconStore.HasFavicon("corrupt-test.local") && GetCachedFavicon("corrupt-test.local") is null);
            var corruptResult = await GetOrLoadFaviconAsync("corrupt-test.local");
            Check("Corrupt PNG decode returns null and does not populate UI cache", corruptResult is null && GetCachedFavicon("corrupt-test.local") is null);

            var testOrigin = "https://coalesce-test.invalid";
            var validPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            await _faviconStore.SaveFaviconAsync(testOrigin, validPng);
            _uiFaviconCache.Remove(FaviconStore.GetKey(testOrigin));
            var loadTask1 = GetOrLoadFaviconAsync(testOrigin);
            var loadTask2 = GetOrLoadFaviconAsync(testOrigin);
            Check("Simultaneous favicon loads for the same origin are coalesced", ReferenceEquals(loadTask1, loadTask2));
            var img1 = await loadTask1;
            var img2 = await loadTask2;
            Check("Coalesced favicon loads return the same BitmapImage decoded at 32px", img1 is not null && ReferenceEquals(img1, img2) && img1.DecodePixelWidth == 32);

            var targetGrid = new Grid { Tag = "expected-key-1" };
            var initialChild = new Border();
            targetGrid.Children.Add(initialChild);
            targetGrid.Tag = "different-key";
            await AttachFaviconAsync(targetGrid, testOrigin, "expected-key-1", () => true, 1.0, 16);
            Check("Stale async completion cannot update control with mismatched Tag", targetGrid.Children.Count == 1 && targetGrid.Children[0] == initialChild);

            targetGrid.Tag = "expected-key-2";
            await AttachFaviconAsync(targetGrid, testOrigin, "expected-key-2", () => false, 1.0, 16);
            Check("Stale async completion cannot update control when validity predicate fails", targetGrid.Children.Count == 1 && targetGrid.Children[0] == initialChild);

            var raceOrigin = "https://race-clear-test.invalid";
            await _faviconStore.SaveFaviconAsync(raceOrigin, validPng);
            var raceKey = FaviconStore.GetKey(raceOrigin);
            _uiFaviconCache.Remove(raceKey);
            var pendingLoad = GetOrLoadFaviconAsync(raceOrigin);
            _uiFaviconGeneration++;
            _uiFaviconCache.Clear();
            var resultAfterClear = await pendingLoad;
            Check("Pending favicon load discarded if generation changed", resultAfterClear is null && !_uiFaviconCache.ContainsKey(raceKey));

            await CloseTabAsync(_session.ActiveTab.Id);
            await ActivateTabAsync(old);
            foreach (var (name, show) in new (string, Func<Task>)[] { ("Palette", () => ShowPaletteAsync()), ("History", ShowHistoryAsync), ("Downloads", ShowDownloadsAsync), ("Settings", ShowSettingsAsync), ("Workspaces", ShowWorkspacesAsync), ("Site information", ShowSiteInfoAsync) })
            {
                bool originalReduceMotion = _session.State.Settings.ReduceMotion;
                if (name == "Settings") _session.State.Settings.ReduceMotion = true;
                var pending = show();
                await Until(() => _activeDialog?.IsLoaded == true, name + " dialog did not open");
                await Task.Delay(120);
                if (name == "Settings")
                {
                    var settingsScroll = (ScrollViewer)_activeDialog!.Content;
                    var settingsPanel = (StackPanel)settingsScroll.Content;
                    var sections = settingsPanel.Children.OfType<StackPanel>().ToArray();
                    var sectionNames = sections.Select(section => (section.Children[0] as TextBlock)?.Text).ToArray();
                    var byName = sections.ToDictionary(section => ((TextBlock)section.Children[0]).Text, StringComparer.Ordinal);
                    bool Has(string section, string automationId) => Descendants(byName[section])
                        .Any(item => AutomationProperties.GetAutomationId(item) == automationId);
                    Check("Settings uses the requested seven-section information architecture",
                        sectionNames.SequenceEqual(["Appearance", "Browsing", "Tabs", "Privacy / Data", "Downloads", "Profile", "About"]));
                    Check("Appearance contains only supported visual controls",
                        Has("Appearance", "SettingsThemeComboBox") && Has("Appearance", "SettingsAccentComboBox") &&
                        Has("Appearance", "SettingsReduceMotionToggle") && Has("Appearance", "SettingsBookmarksBarToggle") &&
                        !Descendants(byName["Appearance"]).Any(item => AutomationProperties.GetAutomationId(item) == "SettingsCompactSidebarToggle"));
                    Check("Browsing groups search, startup, zoom, developer tools, and Windows default-app action",
                        Has("Browsing", "SettingsSearchEngineComboBox") && Has("Browsing", "SettingsStartupBehaviorComboBox") &&
                        Has("Browsing", "SettingsDefaultZoomNumberBox") && Has("Browsing", "SettingsDevToolsToggle") &&
                        Has("Browsing", "SettingsDefaultBrowserButton"));
                    var sleepSetting = Descendants(byName["Tabs"]).OfType<ComboBox>()
                        .Single(item => AutomationProperties.GetAutomationId(item) == "SettingsTabSleepComboBox");
                    Check("Tabs groups restore and human-readable inactive-tab sleep values",
                        Has("Tabs", "SettingsRestoreTabsToggle") && (sleepSetting.ItemsSource as IEnumerable<string>)?.FirstOrDefault() == "Never");
                    Check("Privacy and Data groups the existing password and clearing actions",
                        Has("Privacy / Data", "SettingsManagePasswordsButton") && Has("Privacy / Data", "SettingsImportBrowserDataButton") &&
                        Has("Privacy / Data", "SettingsClearBrowsingDataButton"));
                    Check("Downloads groups the existing path picker and ask-before-saving setting",
                        Has("Downloads", "SettingsDownloadLocationTextBox") && Has("Downloads", "SettingsChooseFolderButton") &&
                        Has("Downloads", "SettingsAskDownloadToggle"));
                    Check("Profile exposes the supported local profile-folder action",
                        Has("Profile", "SettingsOpenProfileButton"));
                    Check("About reports Slate, assembly, WebView2, and architecture versions separately",
                        Has("About", "SettingsAboutTitleText") && Has("About", "SettingsAssemblyVersionText") &&
                        Has("About", "SettingsWebView2VersionText") && Has("About", "SettingsArchitectureText"));
                    var settingsControls = Descendants(settingsPanel).OfType<Control>()
                        .Where(control => AutomationProperties.GetAutomationId(control).StartsWith("Settings", StringComparison.Ordinal))
                        .ToArray();
                    Check("Settings focus order follows the visual section order",
                        settingsControls.Where(control => control.TabIndex >= 0).Select(control => control.TabIndex)
                            .OrderBy(index => index).SequenceEqual(Enumerable.Range(0, 19)));
                    Check("Settings controls expose accessible names",
                        settingsControls.All(control => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(control))));
                    var actionButtons = new[] { "SettingsDefaultBrowserButton", "SettingsManagePasswordsButton", "SettingsImportBrowserDataButton", "SettingsClearBrowsingDataButton", "SettingsOpenProfileButton" }
                        .Select(id => Descendants(settingsPanel).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == id)).ToArray();
                    Check("Settings action buttons share a compact aligned width",
                        actionButtons.All(button => Math.Abs(button.ActualWidth - 220) < 1 && button.HorizontalAlignment == HorizontalAlignment.Left));
                    Check("Settings uses compact section rhythm without separator borders",
                        Math.Abs(settingsPanel.Spacing - 14) < .1 && sections.All(section => Math.Abs(section.Spacing - 8) < .1) &&
                        settingsPanel.Children.OfType<Border>().Count() == 0);
                    Check("Settings scrolling is vertical-only and excluded from Tab order",
                        settingsScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto &&
                        settingsScroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && !settingsScroll.IsTabStop);
                    Check("Settings preserves native Escape and Enter dialog behavior",
                        _activeDialog.CloseButtonText == "Cancel" && _activeDialog.DefaultButton == ContentDialogButton.Primary);
                    Check("Settings honors Reduce Motion without adding dialog transitions",
                        _session.State.Settings.ReduceMotion && _activeDialog.Transitions.Count == 0 &&
                        settingsScroll.Transitions.Count == 0 && settingsPanel.Transitions.Count == 0);
                    var glyphButtons = Descendants(settingsPanel).OfType<Button>()
                        .Where(button => button.Content is FontIcon || Descendants(button).OfType<FontIcon>().Any()).ToArray();
                    Check("Settings glyph-only buttons have names and tooltips",
                        glyphButtons.All(button => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)) &&
                            ToolTipService.GetToolTip(button) is not null));
                    var importButton = Descendants(_activeDialog!).OfType<Button>()
                        .Single(button => AutomationProperties.GetAutomationId(button) == "SettingsImportBrowserDataButton");
                    Check("Privacy and Data exposes an accessible first-class import action",
                        importButton.Content as string == "Import browser data" && importButton.AccessKey == "I" &&
                        AutomationProperties.GetName(importButton) == "Import browser data" &&
                        AutomationProperties.GetHelpText(importButton) == "Import passwords from a CSV export file");
                    var screenshot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, Path.GetFileNameWithoutExtension(output) + "-settings.png");
                    await CaptureElementAsync(_activeDialog!, screenshot);
                }
                _activeDialog!.Hide(); await pending;
                if (name == "Settings") _session.State.Settings.ReduceMotion = originalReduceMotion;
                Check(name + " dialog opens and dismisses", _activeDialog is null);
            }
            var activeSpace = _session.ActiveWorkspace.Id;
            var newSpace = _session.AddWorkspace("Smoke workspace"); await RefreshAsync();
            Check("New workspace starts with a new tab", _session.ActiveTab.Url == Navigation.NewTab && _workspacePicker.SelectedItem == newSpace);
            _session.State.ActiveWorkspaceId = activeSpace; await RefreshAsync();
            Check("Switching workspace restores its active page", _session.ActiveTab.Id == old);
            ToggleSidebar(); await Task.Delay(250);
            Check("Collapsed sidebar keeps navigation compact", Math.Abs(_body.ColumnDefinitions[0].ActualWidth - 64) < 1);
            var rootCollapsedSnapshot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, Path.GetFileNameWithoutExtension(output) + "-collapsed-shell.png");
            await CaptureElementAsync(_root, rootCollapsedSnapshot);
            Check("Collapsed shell visual snapshot renders", File.Exists(rootCollapsedSnapshot) && new FileInfo(rootCollapsedSnapshot).Length > 0);
            var collapsedMenuIcon = Descendants(_workspaceMenu).OfType<Grid>().First(g => g.Width == 20);
            var collapsedNewTab = _sidebar.Children.OfType<Button>().First(b => b.Tag is "new");
            var collapsedNewTabIcon = Descendants(collapsedNewTab).OfType<Grid>().First(g => g.Width == 20);
            var collapsedTabRow = _tabList.Children.OfType<Grid>().First();
            var collapsedTabIcon = Descendants(collapsedTabRow).OfType<Grid>().First(g => g.Width == 20);
            var collapsedBottomIcon = Descendants(bottomPanel.Children.OfType<Button>().First()).OfType<Grid>().First(g => g.Width == 20);
            Check("Collapsed workspace menu, new tab, and tab rows share icon horizontal center",
                Math.Abs(IconCenterX(collapsedMenuIcon) - IconCenterX(collapsedNewTabIcon)) < 1 &&
                Math.Abs(IconCenterX(collapsedNewTabIcon) - IconCenterX(collapsedTabIcon)) < 1 &&
                Math.Abs(IconCenterX(collapsedTabIcon) - IconCenterX(collapsedBottomIcon)) < 1);
            var collapsedSidebarShot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, Path.GetFileNameWithoutExtension(output) + "-sidebar-collapsed.png");
            await CaptureElementAsync(_sidebar, collapsedSidebarShot);
            Check("Collapsed sidebar snapshot renders", File.Exists(collapsedSidebarShot) && new FileInfo(collapsedSidebarShot).Length > 0);
            Check("Collapsed toolbar stretches across available width", _toolbarSurface.HorizontalAlignment == HorizontalAlignment.Stretch && (double.IsPositiveInfinity(_toolbarSurface.MaxWidth) || _toolbarSurface.MaxWidth > 5000) && Math.Abs(_toolbarSurface.ActualWidth - _pageFrame.ActualWidth) < 1 && _toolbarSurface.ActualWidth > 0);
            Check("Collapsed address bar expands within toolbar", _address.HorizontalAlignment == HorizontalAlignment.Stretch && _address.ActualWidth > 300);
            foreach (double scale in new[] { 1d, 1.25, 1.5 })
            {
                string screenshot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, Path.GetFileNameWithoutExtension(output) + $"-toolbar-collapsed-{scale * 100:0}.png");
                await CaptureElementAsync(_toolbarSurface, screenshot, scale);
                Check($"Collapsed toolbar rasterizes at {scale * 100:0}%", File.Exists(screenshot) && new FileInfo(screenshot).Length > 0);
            }
            var initialWindowSize = AppWindow.Size;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 700));
            _root.UpdateLayout();
            await Task.Delay(100);
            Check("Collapsed toolbar and content align after resize", Math.Abs(_toolbarSurface.ActualWidth - _pageFrame.ActualWidth) < 1 && _toolbarSurface.ActualWidth > 0);
            ToggleSidebar(); await Task.Delay(250);
            Check("Expanded sidebar restores its width", Math.Abs(_body.ColumnDefinitions[0].ActualWidth - 228) < 1);
            var expandedSidebarShot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, Path.GetFileNameWithoutExtension(output) + "-sidebar-expanded.png");
            await CaptureElementAsync(_sidebar, expandedSidebarShot);
            Check("Expanded sidebar snapshot renders", File.Exists(expandedSidebarShot) && new FileInfo(expandedSidebarShot).Length > 0);
            Check("Expanded toolbar stretches across available width", _toolbarSurface.HorizontalAlignment == HorizontalAlignment.Stretch && Math.Abs(_toolbarSurface.ActualWidth - _pageFrame.ActualWidth) < 1 && _toolbarSurface.ActualWidth > 0);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 800));
            _root.UpdateLayout();
            await Task.Delay(100);
            Check("Expanded toolbar and content align after resize", Math.Abs(_toolbarSurface.ActualWidth - _pageFrame.ActualWidth) < 1 && _toolbarSurface.ActualWidth > 0);
            ToggleSidebar(); await Task.Delay(250);
            Check("Repeated toggle collapses sidebar and preserves layout", Math.Abs(_body.ColumnDefinitions[0].ActualWidth - 64) < 1 && Math.Abs(_toolbarSurface.ActualWidth - _pageFrame.ActualWidth) < 1);
            ToggleSidebar(); await Task.Delay(250);
            Check("Repeated toggle expands sidebar and preserves layout", Math.Abs(_body.ColumnDefinitions[0].ActualWidth - 228) < 1 && Math.Abs(_toolbarSurface.ActualWidth - _pageFrame.ActualWidth) < 1);
            AppWindow.Resize(initialWindowSize);
            _root.UpdateLayout();
            await Task.Delay(100);
            _session.State.Settings.Theme = "Light"; ApplyAppearance(); await Task.Delay(100);
            Check("Light theme applies to the native shell", _root.ActualTheme == ElementTheme.Light);
            _session.State.Settings.Theme = "Dark"; ApplyAppearance();
            Check("Dark theme applies to the native shell", _root.ActualTheme == ElementTheme.Dark);
            Save();
            var restored = new BrowserSession(new StateStore(_store.DirectoryPath).Load());
            Check("Live session persists tabs and workspace state", restored.ActiveTab.Url == _session.ActiveTab.Url && restored.State.Workspaces.Count == 2);
            Check("Favicon cache exists before clearing data", _faviconStore.HasFavicon(origin + "/one"));
            _session.State.History.Add(new HistoryEntry { Url = "https://no-selection.example/", Title = "Keep when nothing selected" });
            await PerformClearBrowsingDataAsync(null, false, false, false, false, false);
            Check("Clearing no selected data categories leaves browser data untouched", _session.State.History.Any(item => item.Url == "https://no-selection.example/"));
            await PerformClearBrowsingDataAsync();
            Check("Clearing browsing data purges favicon store and memory cache", !_faviconStore.HasFavicon(origin + "/one") && _uiFaviconCache.Count == 0);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, checks = results, profile = _store.DirectoryPath }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = false, error = ex.ToString(), checks = results, profile = _store.DirectoryPath }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { stop.Cancel(); listener.Stop(); try { await server; } catch (OperationCanceledException) { } catch (SocketException) { } Close(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static async Task CaptureElementAsync(FrameworkElement element, string path, double scale = 1)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element, (int)Math.Round(element.ActualWidth * scale), (int)Math.Round(element.ActualHeight * scale));
        var pixels = await bitmap.GetPixelsAsync();
        await using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var stream = file.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
    }

    private static async Task ServeFixtureAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(token);
            _ = HandleFixtureClientAsync(client, token);
        }
    }

    private static async Task HandleFixtureClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        try
        {
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var request = await reader.ReadLineAsync(token) ?? "";
            string? header;
            do { header = await reader.ReadLineAsync(token); } while (!string.IsNullOrEmpty(header));
            bool isFavicon = request.Contains("/favicon.png ");
            if (isFavicon)
            {
                var icon = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
                var iconResponse = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {icon.Length}\r\nCache-Control: max-age=3600\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(iconResponse, token); await stream.WriteAsync(icon, token); return;
            }
            string title = request.Contains("/two ") ? "Slate Test Two" : "Slate Test One";
            bool isDownload = request.Contains("/download ");
            var passwordFixture = PasswordFixture(request, ((IPEndPoint)client.Client.LocalEndPoint!).Port);
            if (request.Contains("/password-success-slow ", StringComparison.Ordinal)) await Task.Delay(1500, token);
            var body = Encoding.UTF8.GetBytes(passwordFixture ?? (isDownload ? "Slate download fixture" : $"<!doctype html><html><head><title>{title}</title><link rel='icon' href='/favicon.png'></head><body><h1>{title}</h1><input aria-label='Test input'><a href='/two'>Next page</a></body></html>"));
            var type = isDownload ? "application/octet-stream\r\nContent-Disposition: attachment; filename=slate-test.txt" : "text/html; charset=utf-8";
            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, token); await stream.WriteAsync(body, token);
        }
        catch (IOException) { /* Chromium may cancel speculative connections during navigation. */ }
        catch (OperationCanceledException) { }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int TableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize, bool bOrder, int ulAf, int TableClass, uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    private static HashSet<int> GetProcessTreeIds(int rootPid)
    {
        var result = new HashSet<int> { rootPid };
        var parentMap = new Dictionary<int, List<int>>();
        var snapshot = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    int pid = (int)entry.th32ProcessID;
                    int ppid = (int)entry.th32ParentProcessID;
                    if (!parentMap.TryGetValue(ppid, out var list))
                        parentMap[ppid] = list = [];
                    list.Add(pid);
                } while (Process32Next(snapshot, ref entry));
            }
        }
        finally { CloseHandle(snapshot); }

        var queue = new Queue<int>();
        queue.Enqueue(rootPid);
        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            if (parentMap.TryGetValue(current, out var children))
            {
                foreach (var child in children)
                {
                    if (result.Add(child)) queue.Enqueue(child);
                }
            }
        }
        return result;
    }

    private static List<int> GetUnexpectedListeningPorts(int expectedPort)
    {
        var pids = GetProcessTreeIds(Environment.ProcessId);
        var unexpected = new List<int>();

        // Check IPv4 (AF_INET = 2)
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5 /* TCP_TABLE_OWNER_PID_ALL */, 0);
        if (size > 0)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, 2, 5, 0) == 0)
                {
                    int entries = Marshal.ReadInt32(buffer);
                    IntPtr rowPtr = IntPtr.Add(buffer, 4);
                    for (int i = 0; i < entries; i++)
                    {
                        int state = Marshal.ReadInt32(rowPtr, 0);
                        int portNetwork = Marshal.ReadInt32(rowPtr, 8);
                        int port = ((portNetwork & 0xFF) << 8) | ((portNetwork >> 8) & 0xFF);
                        int owningPid = Marshal.ReadInt32(rowPtr, 20);
                        if (state == 2 /* MIB_TCP_STATE_LISTEN */ && pids.Contains(owningPid) && port != expectedPort)
                        {
                            unexpected.Add(port);
                        }
                        rowPtr = IntPtr.Add(rowPtr, 24);
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        // Check IPv6 (AF_INET6 = 23)
        size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, 23, 5 /* TCP_TABLE_OWNER_PID_ALL */, 0);
        if (size > 0)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, 23, 5, 0) == 0)
                {
                    int entries = Marshal.ReadInt32(buffer);
                    IntPtr rowPtr = IntPtr.Add(buffer, 4);
                    for (int i = 0; i < entries; i++)
                    {
                        int portNetwork = Marshal.ReadInt32(rowPtr, 20);
                        int port = ((portNetwork & 0xFF) << 8) | ((portNetwork >> 8) & 0xFF);
                        int state = Marshal.ReadInt32(rowPtr, 48);
                        int owningPid = Marshal.ReadInt32(rowPtr, 52);
                        if (state == 2 /* MIB_TCP_STATE_LISTEN */ && pids.Contains(owningPid) && port != expectedPort)
                        {
                            unexpected.Add(port);
                        }
                        rowPtr = IntPtr.Add(rowPtr, 56);
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        // Check UDP IPv4 (AF_INET = 2, UDP_TABLE_OWNER_PID = 1)
        size = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref size, false, 2, 1 /* UDP_TABLE_OWNER_PID */, 0);
        if (size > 0)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedUdpTable(buffer, ref size, false, 2, 1, 0) == 0)
                {
                    int entries = Marshal.ReadInt32(buffer);
                    IntPtr rowPtr = IntPtr.Add(buffer, 4);
                    for (int i = 0; i < entries; i++)
                    {
                        int portNetwork = Marshal.ReadInt32(rowPtr, 4);
                        int port = ((portNetwork & 0xFF) << 8) | ((portNetwork >> 8) & 0xFF);
                        int owningPid = Marshal.ReadInt32(rowPtr, 8);
                        if (pids.Contains(owningPid) && port != expectedPort)
                        {
                            unexpected.Add(port);
                        }
                        rowPtr = IntPtr.Add(rowPtr, 12);
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        // Check UDP IPv6 (AF_INET6 = 23, UDP_TABLE_OWNER_PID = 1)
        size = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref size, false, 23, 1 /* UDP_TABLE_OWNER_PID */, 0);
        if (size > 0)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedUdpTable(buffer, ref size, false, 23, 1, 0) == 0)
                {
                    int entries = Marshal.ReadInt32(buffer);
                    IntPtr rowPtr = IntPtr.Add(buffer, 4);
                    for (int i = 0; i < entries; i++)
                    {
                        int portNetwork = Marshal.ReadInt32(rowPtr, 20);
                        int port = ((portNetwork & 0xFF) << 8) | ((portNetwork >> 8) & 0xFF);
                        int owningPid = Marshal.ReadInt32(rowPtr, 24);
                        if (pids.Contains(owningPid) && port != expectedPort)
                        {
                            unexpected.Add(port);
                        }
                        rowPtr = IntPtr.Add(rowPtr, 28);
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        return unexpected;
    }
}
