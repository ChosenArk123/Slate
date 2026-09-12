using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Slate.Core;
using Windows.ApplicationModel.DataTransfer;

namespace Slate;

internal sealed class PasswordManagerUi(CredentialVault vault)
{
    public async Task ShowAsync(Func<string, object, ContentDialog> createDialog, Func<ContentDialog, Task<ContentDialogResult>> showDialog,
        string? initialOrigin = null)
    {
        var search = new TextBox { PlaceholderText = "Search sites or usernames", MaxLength = 1024, Text = initialOrigin ?? "" };
        var list = new ListView { Height = 240, SelectionMode = ListViewSelectionMode.Single };
        var origin = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var username = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var password = new TextBlock { Text = "••••••••••••", IsTextSelectionEnabled = false };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var reveal = new Button { Content = "Reveal" };
        var copyUser = new Button { Content = "Copy username" };
        var copyPassword = new Button { Content = "Copy password" };
        var delete = new Button { Content = "Delete" };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { reveal, copyUser, copyPassword, delete } };
        var panel = new StackPanel { Width = 500, Spacing = 10, Children = { search, list, origin, username, password, actions, note } };
        var dialog = createDialog("Passwords", panel);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        bool closed = false, working = false, confirmingDelete = false;
        long selection = 0;
        IReadOnlyList<CredentialMetadata> entries = [];
        CredentialMetadata? Selected() => (list.SelectedItem as ListViewItem)?.Tag as CredentialMetadata;
        void Mask() { timer.Stop(); password.Text = "••••••••••••"; reveal.Content = "Reveal"; }
        void EnableActions()
        {
            foreach (var button in new[] { reveal, copyUser, copyPassword, delete }) button.IsEnabled = Selected() is not null && !working;
            delete.IsEnabled &= !vault.RecoveredFromBackup;
        }
        void Selection()
        {
            selection++; Mask(); confirmingDelete = false; delete.Content = "Delete";
            var item = Selected(); origin.Text = item?.Origin ?? "Select a credential"; username.Text = item?.Username ?? "";
            EnableActions();
        }
        void Filter()
        {
            list.Items.Clear();
            var matches = entries.Where(e => e.Origin.Contains(search.Text, StringComparison.OrdinalIgnoreCase) || e.Username.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var entry in matches)
                list.Items.Add(new ListViewItem { Tag = entry, Content = new StackPanel { Children = {
                    new TextBlock { Text = entry.Origin, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = entry.Username.Length == 0 ? "(no username)" : entry.Username, FontSize = 12, Opacity = .7, TextTrimming = TextTrimming.CharacterEllipsis } } } });
            if (matches.Length == 0)
                list.Items.Add(new ListViewItem { Content = entries.Count == 0 ? "No saved passwords yet." : "No saved passwords match this search.", IsEnabled = false });
            Selection();
        }
        async Task Load()
        {
            try
            {
                entries = await vault.ListAsync(); if (closed) return;
                note.Text = vault.RecoveredFromBackup ? "Recovered backup (read only). The damaged vault is preserved; restore it after review."
                    : "Stored locally with Windows user protection. Reveal and copy do not require Windows Hello.";
                Filter();
            }
            catch { if (!closed) note.Text = "The password vault is unavailable. Its files have been preserved."; }
        }
        async Task SecretAction(bool copy)
        {
            if (working || Selected() is not { } item) return;
            if (!copy && password.Text != "••••••••••••") { Mask(); return; }
            long token = selection; working = true; EnableActions();
            string? secret = null;
            try
            {
                secret = await vault.RevealAsync(item.Id, item.Revision);
                if (closed || selection != token) return;
                if (copy) { Copy(secret); note.Text = "Password copied. Clipboard contents are not automatically cleared."; }
                else { password.Text = secret; reveal.Content = "Hide"; timer.Start(); }
                secret = "";
            }
            catch { if (!closed) note.Text = "The credential could not be opened."; }
            finally { secret = null; working = false; if (!closed) EnableActions(); }
        }
        timer.Tick += (_, _) => Mask();
        list.SelectionChanged += (_, _) => Selection(); search.TextChanged += (_, _) => Filter();
        reveal.Click += async (_, _) => await SecretAction(false);
        copyPassword.Click += async (_, _) => await SecretAction(true);
        copyUser.Click += (_, _) => { if (Selected() is { } item) try { Copy(item.Username); note.Text = "Username copied."; } catch { note.Text = "Clipboard is unavailable."; } };
        delete.Click += async (_, _) =>
        {
            if (working || Selected() is not { } item) return;
            if (!confirmingDelete) { confirmingDelete = true; delete.Content = "Confirm delete"; return; }
            working = true; Mask();
            try { await vault.DeleteAsync(item.Id, item.Revision); await Load(); }
            catch { if (!closed) note.Text = "The credential could not be deleted. Review the current entry and try again."; }
            finally { working = false; if (!closed) Selection(); }
        };
        dialog.Closed += (_, _) => { closed = true; selection++; Mask(); entries = []; list.Items.Clear(); };
        Selection(); await Load();
        try { await showDialog(dialog); }
        finally { closed = true; Mask(); }
    }

    private static void Copy(string value)
    {
        var content = new DataPackage(); content.SetText(value);
        if (!Clipboard.SetContentWithOptions(content, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false }))
            throw new InvalidOperationException();
    }
}
