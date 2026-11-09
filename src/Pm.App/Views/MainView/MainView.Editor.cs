namespace Pm.App.Views.MainView;

public sealed partial class MainView
{
    private void SelectEntry(Entry entry)
    {
        ResetDelete();
        ResetReveal();
        _titleError.IsVisible = false;
        _selected = entry;
        _formTitle.Text = _s.Editing;
        _title.Text = entry.Title;
        _login.Text = entry.Login;
        _password.Text = entry.Password;
        _url.Text = entry.Url;
        _notes.Text = entry.Notes;
        RefreshList();
    }

    private void PrepareNewEntry()
    {
        ResetDelete();
        ResetReveal();
        _titleError.IsVisible = false;
        _selected = null;
        _formTitle.Text = _s.NewEntry;
        _title.Text = _login.Text = _password.Text = _url.Text = _notes.Text = "";
        RefreshList();
        _title.Focus();
        _title.BringIntoView();
    }

    private void NewEntry()
    {
        PrepareNewEntry();
        SetStatus(_s.NewEntry);
    }

    private void SaveEntry()
    {
        if (_session is null) return;
        var title = _title.Text?.Trim() ?? "";
        if (title.Length == 0)
        {
            _title.BorderBrush = Ui.Danger;
            _titleError.Text = _s.NeedTitle;
            _titleError.IsVisible = true;
            _title.Focus();
            SetStatus(_s.NeedTitle);
            return;
        }

        try
        {
            if (_selected is null)
            {
                _selected = _session.AddEntry(title, _login.Text ?? "", _password.Text ?? "", _url.Text ?? "", _notes.Text ?? "");
            }
            else
            {
                var updated = _selected.Clone();
                updated.Title = title;
                updated.Login = _login.Text ?? "";
                updated.Password = _password.Text ?? "";
                updated.Url = _url.Text ?? "";
                updated.Notes = _notes.Text ?? "";
                _selected = _session.UpdateEntry(updated);
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            return;
        }

        PrepareNewEntry();
        SetStatus(_s.Saved);
        _ = SyncAsync(false);
    }

    private void DeleteEntry()
    {
        if (_session is null || _selected is null) return;

        if (!_confirmingDelete)
        {
            _confirmingDelete = true;
            if (_deleteButton is not null) _deleteButton.IsVisible = false;
            if (_confirmDeleteButton is not null) _confirmDeleteButton.IsVisible = true;
            if (_cancelDeleteButton is not null) _cancelDeleteButton.IsVisible = true;
            return;
        }

        _session.DeleteEntry(_selected.Id);
        _selected = null;
        _formTitle.Text = _s.NewEntry;
        _title.Text = _login.Text = _password.Text = _url.Text = _notes.Text = "";
        ResetDelete();
        ResetReveal();
        RefreshList();
        SetStatus(_s.Deleted);
        _ = SyncAsync(false);
    }

    private void ResetDelete()
    {
        _confirmingDelete = false;
        if (_deleteButton is not null) _deleteButton.IsVisible = true;
        if (_confirmDeleteButton is not null) _confirmDeleteButton.IsVisible = false;
        if (_cancelDeleteButton is not null) _cancelDeleteButton.IsVisible = false;
    }

    private void ToggleReveal()
    {
        _password.RevealPassword = !_password.RevealPassword;
        if (_revealButton is not null)
            _revealButton.Content = _password.RevealPassword ? _s.HidePassword : _s.Reveal;
    }

    private void ResetReveal()
    {
        _password.RevealPassword = false;
        if (_revealButton is not null) _revealButton.Content = _s.Reveal;
    }
}
