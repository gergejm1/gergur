namespace Gergur.Tabs;

/// <summary>
/// A page's prompt(), asked of the person in a plain box: for the moment after an agent's
/// window becomes theirs and before the engine shows its own again (see
/// <see cref="Tab.RefreshDialogMode"/>). Null when they cancel.
/// </summary>
internal static class PromptBox
{
    internal static string? Ask(IWin32Window? owner, string title, string message, string? defaultText)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoScaleDimensions = new SizeF(96f, 96f),
            ClientSize = new Size(420, 150),
        };
        var label = new Label { Text = message, Left = 12, Top = 12, Width = 396, Height = 48, AutoEllipsis = true };
        var box = new TextBox { Text = defaultText ?? "", Left = 12, Top = 66, Width = 396 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 252, Top = 108, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 333, Top = 108, Width = 75 };
        form.Controls.AddRange([label, box, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
    }
}
