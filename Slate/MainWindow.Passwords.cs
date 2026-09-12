using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Slate.Core;
using Windows.Storage.Pickers;

namespace Slate;

public sealed partial class MainWindow
{
    private readonly CredentialVault _credentialVault;
    private Button _passwordButton = null!;
    private readonly InfoBar _passwordNotice = new() { IsOpen = false, Severity = InfoBarSeverity.Informational, Margin = new(0, 0, 0, 6) };
    private readonly Button _passwordOfferAcceptButton = new() { Content = "Save" };
    private PasswordController? _offeringController;

    private Task ShowPasswordManagerAsync(string? initialOrigin = null) => new PasswordManagerUi(_credentialVault).ShowAsync(
        (title, content) => Dialog(title, content), ShowDialogAsync, initialOrigin);

    private void UpdatePasswordOffer()
    {
        if (_closing) return;
        var controller = _runtimes.GetValueOrDefault(FocusedTab.Id)?.Passwords;
        _offeringController = controller?.OfferOrigin is not null ? controller : null;
        _passwordNotice.IsOpen = _offeringController is not null;
        if (_offeringController is null) return;
        _passwordNotice.RequestedTheme = _root.RequestedTheme;
        _passwordNotice.Background = _theme.SurfaceRaisedBrush;
        _passwordNotice.Foreground = _theme.TextPrimaryBrush;
        _passwordNotice.Resources["InfoBarInformationalSeverityBackgroundBrush"] = _theme.SurfaceRaisedBrush;
        _passwordNotice.Resources["InfoBarTitleForeground"] = _theme.TextPrimaryBrush;
        _passwordNotice.Resources["InfoBarMessageForeground"] = _theme.TextSecondaryBrush;
        _passwordNotice.Title = controller!.IsUpdate ? "Update password?" : "Save password?";
        _passwordNotice.Message = controller.OfferOrigin + " · " + (controller.OfferUsername is { Length: > 0 } user ? user : "(no username)") + " — Only save if sign-in succeeded.";
    }

    private void UpdatePasswordIndicator()
    {
        if (_closing || _passwordButton is null) return;
        int count = _runtimes.GetValueOrDefault(FocusedTab.Id)?.Passwords?.AccountCount ?? 0;
        string label = count == 0 ? "Passwords" : count == 1 ? "Passwords · 1 saved account for this site" : $"Passwords · {count:N0} saved accounts for this site";
        ToolTipService.SetToolTip(_passwordButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_passwordButton, label);
        if (_passwordButton.Content is Microsoft.UI.Xaml.Controls.FontIcon icon)
            icon.Foreground = count > 0 ? _theme.AccentPrimaryBrush : null;
    }

    private async Task ShowPasswordsForPageAsync()
    {
        var controller = _runtimes.GetValueOrDefault(FocusedTab.Id)?.Passwords;
        if (controller?.CaptureTicket() is not { } ticket) { Notify("Passwords are unavailable for this page or it is still loading."); return; }
        try
        {
            var accounts = await controller.AccountsAsync(ticket);
            if (_closing || controller.CaptureTicket() != ticket) return;
            var panel = new StackPanel { Width = 320, Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = ticket.Origin, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            if (ticket.Origin.StartsWith("http:", StringComparison.Ordinal)) panel.Children.Add(new TextBlock { Text = "HTTP connection: this site can transmit your password without encryption.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            var flyout = new Flyout { Content = new ScrollViewer { Content = panel, MaxHeight = 400 } };
            if (accounts.Count == 0) panel.Children.Add(new TextBlock { Text = "No saved accounts for this origin." });
            foreach (var account in accounts)
            {
                var fill = new Button { Content = "Fill · " + (account.Username.Length == 0 ? "(no username)" : account.Username), HorizontalAlignment = HorizontalAlignment.Stretch };
                fill.Click += async (_, _) => { flyout.Hide(); Notify(await controller.FillAsync(ticket, account) ? "Password filled. Submit the form when ready." : "Could not fill: the page changed or its form is not supported."); };
                panel.Children.Add(fill);
            }
            var generate = new Button { Content = "Generate password", HorizontalAlignment = HorizontalAlignment.Stretch };
            bool temporary = FocusedTab.IsTemporary;
            generate.Click += async (_, _) => { flyout.Hide(); Notify(await controller.GenerateAsync(ticket) ? temporary ? "Generated password filled in this temporary tab. Slate will not offer to save it." : "Generated password filled. Submit successfully to receive a save offer." : "Use an empty form marked for a new password. Reload to retry a filled document."); };
            panel.Children.Add(generate);
            var manage = new Button { Content = "Manage passwords for this site", HorizontalAlignment = HorizontalAlignment.Stretch };
            manage.Click += async (_, _) => { flyout.Hide(); await ShowPasswordManagerAsync(ticket.Origin); }; panel.Children.Add(manage);
            flyout.ShowAt(_passwordButton);
        }
        catch { Notify("The password vault is unavailable. Its files have been preserved."); }
    }

    private async Task ShowPasswordImportAsync()
    {
        var warning = Dialog("Import passwords from CSV", new StackPanel
        {
            Width = 430,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Browser password exports contain plaintext passwords.", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "Slate reads the selected file locally, imports only normalized site credentials into its Windows-protected vault, and does not keep the CSV. Delete the export yourself when you no longer need it.", TextWrapping = TextWrapping.Wrap }
            }
        }, "Cancel");
        warning.PrimaryButtonText = "Choose CSV";
        warning.DefaultButton = ContentDialogButton.Primary;
        if (await ShowDialogAsync(warning) != ContentDialogResult.Primary) return;

        Windows.Storage.StorageFile? file;
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".csv");
            file = await picker.PickSingleFileAsync();
        }
        catch
        {
            await ShowPasswordImportMessageAsync("Import unavailable", "Slate could not open the file picker. No passwords were imported.");
            return;
        }
        if (file is null) return;

        try
        {
            var parser = new BrowserDataImportService();
            using var stream = await file.OpenStreamForReadAsync();
            using var package = parser.ParseCsv(stream);
            var importer = new CredentialImportCoordinator(_credentialVault);
            var review = await importer.ReviewAsync(package);

            string summary = PasswordImportSummary(review);
            if (review.ReadyToImport == 0)
            {
                await ShowPasswordImportMessageAsync("Nothing to import", summary + "\n\nNo new valid credentials will be added.");
                return;
            }

            var confirm = Dialog("Review password import", new TextBlock
            {
                Width = 430,
                Text = summary + "\n\nSlate supports credentials without a username. Existing credentials with a different password are reported as conflicts and are not overwritten.",
                TextWrapping = TextWrapping.Wrap
            }, "Cancel");
            confirm.PrimaryButtonText = $"Import {review.ReadyToImport:N0}";
            confirm.DefaultButton = ContentDialogButton.Primary;
            if (await ShowDialogAsync(confirm) != ContentDialogResult.Primary) return;

            var result = await importer.ImportAsync(package);
            await ShowPasswordImportMessageAsync("Password import complete",
                $"Total rows: {result.Total:N0}\nImported: {result.Imported:N0}\nSkipped: {result.Skipped:N0}\nDuplicates: {result.Duplicates:N0}\nConflicts: {result.Conflicts:N0}\nInvalid: {result.Invalid:N0}\nFailed: {result.Failed:N0}");
        }
        catch (BrowserDataImportException ex)
        {
            await ShowPasswordImportMessageAsync("Could not read this CSV", ex.Message);
        }
        catch (CredentialVaultException)
        {
            await ShowPasswordImportMessageAsync("Password vault unavailable", "The vault could not be opened or is read only. Its files were preserved and no error details contain passwords.");
        }
        catch (UnauthorizedAccessException)
        {
            await ShowPasswordImportMessageAsync("Access denied", "Slate could not read the selected file. Choose a file you can access and try again.");
        }
        catch
        {
            await ShowPasswordImportMessageAsync("Import failed", "Slate could not complete the import. The selected CSV was not copied or retained.");
        }
    }

    private async Task ShowPasswordImportMessageAsync(string title, string message) =>
        await ShowDialogAsync(Dialog(title, new TextBlock { Width = 430, Text = message, TextWrapping = TextWrapping.Wrap }, "Close"));

    private static string PasswordImportSummary(CredentialImportReview review) =>
        $"Total rows: {review.Total:N0}\nReady to import: {review.ReadyToImport:N0}\nDuplicates: {review.Duplicates:N0}\nConflicts: {review.Conflicts:N0}\n" +
        $"Invalid URLs: {review.InvalidUrls:N0}\nMissing usernames: {review.MissingUsernames:N0}\nMissing passwords: {review.MissingPasswords:N0}\nUnsupported rows: {review.UnsupportedRows:N0}";
}
