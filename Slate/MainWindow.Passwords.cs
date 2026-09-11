using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Slate.Core;

namespace Slate;

public sealed partial class MainWindow
{
    private readonly CredentialVault _credentialVault;
    private Button _passwordButton = null!;
    private readonly Button _passwordOfferAcceptButton = new() { Content = "Save" };
    private readonly InfoBar _passwordNotice = new() { IsOpen = false, Severity = InfoBarSeverity.Informational, Margin = new(0, 0, 0, 6) };
    private PasswordController? _offeringController;

    private Task ShowPasswordManagerAsync() => new PasswordManagerUi(_credentialVault).ShowAsync(
        (title, content) => Dialog(title, content), ShowDialogAsync);

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
        _passwordOfferAcceptButton.Content = controller.IsUpdate ? "Update" : "Save";
        _passwordNotice.Message = controller.OfferOrigin + " · " + (controller.OfferUsername is { Length: > 0 } user ? user : "(no username)") + " — Only save if sign-in succeeded. Close to choose Not now.";
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
            else if (accounts.Count > 1) panel.Children.Add(new TextBlock
            {
                Text = "Choose an account. Slate will not select one automatically.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12
            });
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
            var manage = new Button { Content = "Manage passwords", HorizontalAlignment = HorizontalAlignment.Stretch };
            manage.Click += async (_, _) => { flyout.Hide(); await ShowPasswordManagerAsync(); }; panel.Children.Add(manage);
            flyout.ShowAt(_passwordButton);
        }
        catch { Notify("The password vault is unavailable. Its files have been preserved."); }
    }
}
