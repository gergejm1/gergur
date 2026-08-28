namespace Gergur.UI;

/// <summary>
/// One place for every keyboard shortcut, fed from two directions: the form's
/// ProcessCmdKey (chrome has focus) and each WebView2's forwarded KeyDown (page
/// has focus - those keys never reach the form).
/// </summary>
public sealed class ShortcutRouter
{
    private readonly MainForm _form;

    public ShortcutRouter(MainForm form)
    {
        _form = form;
    }

    /// <summary>Returns true when the key is ours; the caller marks the event handled.</summary>
    public bool Handle(Keys keyData)
    {
        // Work is queued via BeginInvoke: WebView2 blocks the browser process while
        // KeyDown handlers run, and some of its APIs throw if called inline here.
        switch (keyData)
        {
            case Keys.Control | Keys.T:
                Post(() => _form.NewTabAsync());
                return true;
            case Keys.Control | Keys.W:
            case Keys.Control | Keys.F4:
                Post(() => _form.CloseActiveTabAsync());
                return true;
            case Keys.Control | Keys.Shift | Keys.T:
                Post(() => _form.ReopenClosedTabAsync());
                return true;
            case Keys.Control | Keys.L:
            case Keys.Alt | Keys.D:
                Post(_form.FocusAddressBar);
                return true;
            case Keys.Control | Keys.Tab:
            case Keys.Control | Keys.PageDown:
                Post(() => _form.CycleTabAsync(1));
                return true;
            case Keys.Control | Keys.Shift | Keys.Tab:
            case Keys.Control | Keys.PageUp:
                Post(() => _form.CycleTabAsync(-1));
                return true;
            case Keys.Control | Keys.R:
            case Keys.F5:
                Post(_form.ReloadActive);
                return true;
            case Keys.Alt | Keys.Left:
                Post(_form.BackActive);
                return true;
            case Keys.Alt | Keys.Right:
                Post(_form.ForwardActive);
                return true;
            case Keys.Control | Keys.D:
                Post(_form.ToggleBookmark);
                return true;
            case Keys.F12:
                Post(_form.OpenDevTools);
                return true;
        }

        if ((keyData & Keys.Modifiers) == Keys.Control)
        {
            var key = keyData & Keys.KeyCode;
            if (key is >= Keys.D1 and <= Keys.D9)
            {
                int index = key == Keys.D9 ? -1 : key - Keys.D1; // Ctrl+9 = last tab
                Post(() => _form.ActivateTabIndexAsync(index));
                return true;
            }
        }
        return false;
    }

    private void Post(Action action)
        => _form.BeginInvoke(action);

    private void Post(Func<Task> action)
        => _form.BeginInvoke(() => _ = action());
}
