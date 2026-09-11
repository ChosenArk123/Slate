using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Slate.Core;
using Windows.ApplicationModel.DataTransfer;

namespace Slate;

internal sealed class PasswordManagerUi(CredentialVault vault)
{
    public async Task ShowAsync(Func<string, object, ContentDialog> createDialog, Func<ContentDialog, Task<ContentDialogResult>> showDialog)
    {
        const string mask = "••••••••••••";
        var search = new TextBox { PlaceholderText = "Search sites or usernames", MaxLength = 1024 };
        var list = new ListView { Height = 230, SelectionMode = ListViewSelectionMode.Single };
        var origin = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var username = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var password = new TextBlock { Text = mask, IsTextSelectionEnabled = false };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var reveal = new Button { Content = "Reveal" };
        var copyUser = new Button { Content = "Copy username" };
        var copyPassword = new Button { Content = "Copy password" };
        var edit = new Button { Content = "Edit" };
        var delete = new Button { Content = "Delete" };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { reveal, copyUser, copyPassword, edit, delete } };
        var details = new StackPanel { Spacing = 6, Children = { origin, username, password, actions } };

        var editUsername = new TextBox { Header = "Username", MaxLength = 1024 };
        var editPassword = new PasswordBox { Header = "Password", MaxLength = 4096 };
        var saveEdit = new Button { Content = "Save changes" };
        var cancelEdit = new Button { Content = "Cancel" };
        var editActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { saveEdit, cancelEdit } };
        var editor = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed, Children = {
            new TextBlock { Text = "Edit this account", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
            editUsername, editPassword, editActions } };

        var panel = new StackPanel { Width = 540, Spacing = 10, Children = { search, list, details, editor, note } };
        var dialog = createDialog("Passwords", panel);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        bool closed = false, working = false, confirmingDelete = false, editing = false;
        long selection = 0;
        IReadOnlyList<CredentialMetadata> entries = [];
        CredentialMetadata? Selected() => (list.SelectedItem as ListViewItem)?.Tag as CredentialMetadata;
        void Mask() { timer.Stop(); password.Text = mask; reveal.Content = "Reveal"; }
        void SetEditing(bool value)
        {
            Mask();
            editing = value;
            details.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            editor.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (!value) { editUsername.Text = ""; editPassword.Password = ""; }
        }
        void EnableActions()
        {
            bool selected = Selected() is not null;
            foreach (var button in new[] { reveal, copyUser, copyPassword, edit, delete }) button.IsEnabled = selected && !working && !editing;
            delete.IsEnabled &= !vault.RecoveredFromBackup;
            saveEdit.IsEnabled = selected && editing && !working && !vault.RecoveredFromBackup;
            cancelEdit.IsEnabled = editing && !working;
        }
        void Selection()
        {
            selection++;
            Mask();
            SetEditing(false);
            confirmingDelete = false;
            delete.Content = "Delete";
            var item = Selected();
            origin.Text = item?.Origin ?? "Select a credential";
            username.Text = item?.Username ?? "";
            EnableActions();
        }
        void Filter(Guid? select = null)
        {
            list.Items.Clear();
            foreach (var entry in entries.Where(e => e.Origin.Contains(search.Text, StringComparison.OrdinalIgnoreCase) || e.Username.Contains(search.Text, StringComparison.OrdinalIgnoreCase)))
            {
                var item = new ListViewItem { Tag = entry, Content = new StackPanel { Children = {
                    new TextBlock { Text = entry.Origin, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = entry.Username.Length == 0 ? "(no username)" : entry.Username, FontSize = 12, Opacity = .7, TextTrimming = TextTrimming.CharacterEllipsis } } } };
                list.Items.Add(item);
                if (entry.Id == select) list.SelectedItem = item;
            }
            Selection();
        }
        async Task Load(Guid? select = null)
        {
            try
            {
                entries = await vault.ListAsync();
                if (closed) return;
                note.Text = vault.RecoveredFromBackup ? "Recovered backup (read only). The damaged vault is preserved; restore it after review."
                    : "Stored locally with Windows user protection. Reveal, copy, and edit are deliberate actions; Windows Hello confirmation is not currently available.";
                Filter(select);
            }
            catch { if (!closed) note.Text = "The password vault is unavailable. Its files have been preserved."; }
        }
        async Task SecretAction(bool copy)
        {
            if (working || editing || Selected() is not { } item) return;
            if (!copy && password.Text != mask) { Mask(); return; }
            long token = selection;
            working = true;
            EnableActions();
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
        list.SelectionChanged += (_, _) => Selection();
        search.TextChanged += (_, _) => Filter();
        reveal.Click += async (_, _) => await SecretAction(false);
        copyPassword.Click += async (_, _) => await SecretAction(true);
        copyUser.Click += (_, _) =>
        {
            if (Selected() is { } item) try { Copy(item.Username); note.Text = "Username copied."; }
            catch { note.Text = "Clipboard is unavailable."; }
        };
        edit.Click += async (_, _) =>
        {
            if (working || editing || Selected() is not { } item) return;
            long token = selection;
            working = true;
            EnableActions();
            string? secret = null;
            try
            {
                secret = await vault.RevealAsync(item.Id, item.Revision);
                if (closed || selection != token) return;
                editUsername.Text = item.Username;
                editPassword.Password = secret;
                secret = "";
                SetEditing(true);
                note.Text = "The site scope cannot be changed. Saving updates this account in the same protected vault.";
            }
            catch { if (!closed) note.Text = "The credential could not be opened for editing."; }
            finally { secret = null; working = false; if (!closed) EnableActions(); }
        };
        cancelEdit.Click += (_, _) => Selection();
        saveEdit.Click += async (_, _) =>
        {
            if (working || !editing || Selected() is not { } item) return;
            if (!CredentialVault.IsValidCredential(item.Origin, editUsername.Text, editPassword.Password))
            {
                note.Text = "Enter a valid username and a non-empty password.";
                return;
            }
            working = true;
            EnableActions();
            try
            {
                var updated = await vault.UpdateAsync(item.Id, item.Revision, editUsername.Text, editPassword.Password);
                editPassword.Password = "";
                SetEditing(false);
                await Load(updated.Id);
                if (!closed) note.Text = "Credential updated.";
            }
            catch { if (!closed) note.Text = "The credential could not be updated. Review the current entry and try again."; }
            finally { working = false; if (!closed) EnableActions(); }
        };
        delete.Click += async (_, _) =>
        {
            if (working || editing || Selected() is not { } item) return;
            if (!confirmingDelete) { confirmingDelete = true; delete.Content = "Confirm delete"; return; }
            working = true;
            Mask();
            try { await vault.DeleteAsync(item.Id, item.Revision); await Load(); }
            catch { if (!closed) note.Text = "The credential could not be deleted. Review the current entry and try again."; }
            finally { working = false; if (!closed) Selection(); }
        };
        dialog.Closed += (_, _) => { closed = true; selection++; Mask(); SetEditing(false); entries = []; list.Items.Clear(); };
        Selection();
        await Load();
        try { await showDialog(dialog); }
        finally { closed = true; Mask(); SetEditing(false); }
    }

    private static void Copy(string value)
    {
        var content = new DataPackage();
        content.SetText(value);
        if (!Clipboard.SetContentWithOptions(content, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false }))
            throw new InvalidOperationException();
    }
}
