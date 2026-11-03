using Gergur.App;

namespace Gergur.UI;

/// <summary>
/// The bar across the person's window when an agent is waiting on them: who is asking and
/// what, a box for a reply, Done, Can't, and, when the agent has a window, a button to look
/// at it (a captcha or a code to type is usually in there). In the agents' blue, like the
/// band on an agent's window, so it reads as an agent talking and not the page.
/// One question at a time, the oldest first, with how many more are waiting.
/// </summary>
public sealed class AskBar : Panel
{
    private readonly Label _robot = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Label _question = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false };
    private readonly TextBox _reply = new() { PlaceholderText = "Reply (optional)", BorderStyle = BorderStyle.FixedSingle };
    private readonly Button _done = MakeButton("Done", primary: true);
    private readonly Button _cant = MakeButton("Can't", primary: false);
    private readonly Button _show = MakeButton("Show its window", primary: false);
    private readonly Label _more = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleRight };
    private readonly ToolTip _tip = new();
    private AgentQuestion? _current;

    /// <summary>The person answered the question with this id: Done or Can't, and what they typed.</summary>
    public event Action<string, bool, string>? Answered;

    /// <summary>The person wants to see the asking agent's window. Carries the agent's key.</summary>
    public event Action<string>? ShowAgentRequested;

    public AskBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.AgentBg;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9f);
        Height = 40;   // 96 dpi units, scaled with the form
        Padding = new Padding(0, 0, 0, 2);
        _robot.Text = AgentGlyphs.Robot;
        _robot.Font = Theme.IconFont(11f);
        _robot.ForeColor = Theme.Text;
        _question.ForeColor = Theme.Text;
        _more.ForeColor = Theme.AgentTextDim;
        _reply.BackColor = Theme.WindowBg;
        _reply.ForeColor = Theme.Text;
        _reply.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                Answer(done: true);
            }
        };
        _done.Click += (_, _) => Answer(done: true);
        _cant.Click += (_, _) => Answer(done: false);
        _show.Click += (_, _) =>
        {
            if (_current is { } q)
                ShowAgentRequested?.Invoke(q.AgentKey);
        };
        _done.AccessibleName = "Done: tell the agent it is done";
        _cant.AccessibleName = "Can't: tell the agent you cannot do this";
        _show.AccessibleName = "Show the asking agent's window";
        Controls.AddRange([_robot, _question, _reply, _done, _cant, _show, _more]);
        AccessibleRole = AccessibleRole.Alert;
    }

    private static Button MakeButton(string text, bool primary)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            AutoSize = false,
            UseMnemonic = false,
            BackColor = primary ? Theme.Agent : Theme.AgentBg,
            ForeColor = Theme.Text,
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = Theme.Agent;
        b.FlatAppearance.MouseOverBackColor = primary ? Theme.AgentHover : Theme.AgentPressed;
        return b;
    }

    /// <summary>The question shown, if any.</summary>
    internal AgentQuestion? Current => _current;

    internal TextBox ReplyBoxForTest => _reply;
    internal Button ShowButtonForTest => _show;

    /// <summary>What the bar reads: for tests and screen readers.</summary>
    internal string Caption => _current is null ? "" : Describe(_current);

    internal static string Describe(AgentQuestion q) => $"{q.AgentName} asks: {q.Question}";

    /// <summary>
    /// Shows the oldest waiting question, or nothing. A different question clears the reply
    /// box; the same one keeps what the person has typed so far.
    /// </summary>
    /// <param name="canShow">Whether the asking agent has a window or tab to show.</param>
    public void SetQuestions(IReadOnlyList<AgentQuestion> waiting, bool canShow)
    {
        var first = waiting.Count > 0 ? waiting[0] : null;
        if (first?.Id != _current?.Id)
            _reply.Text = "";
        _current = first;
        if (first is null)
            return;
        _question.Text = Describe(first);
        _tip.SetToolTip(_question, first.Question);
        AccessibleName = "An agent is asking: " + Describe(first);
        _show.Visible = canShow;
        _more.Text = waiting.Count > 1 ? $"{waiting.Count - 1} more" : "";
        PerformLayout();
    }

    private void Answer(bool done)
    {
        if (_current is not { } q)
            return;
        string text = _reply.Text;
        _reply.Text = "";
        Answered?.Invoke(q.Id, done, text);
    }

    /// <summary>Puts the keyboard in the reply box, for the person who comes to the window to answer.</summary>
    public void FocusReply() => _reply.Focus();

    private int S(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int h = Height - S(2);
        int buttonH = Math.Max(S(24), h - S(12));
        int top = (h - buttonH) / 2;
        int right = Width - S(8);

        void PlaceRight(Control c, int width)
        {
            if (!c.Visible)
                return;
            right -= width;
            c.SetBounds(right, top, width, buttonH);
            right -= S(6);
        }

        // A narrow window keeps room for the question, which is the part that matters:
        // the buttons say less and the reply box is smaller.
        bool tight = Width < S(820);
        _show.Text = tight ? "Show" : "Show its window";
        PlaceRight(_more, _more.Text.Length == 0 ? 0 : S(52));
        PlaceRight(_show, tight ? S(56) : S(118));
        PlaceRight(_cant, S(64));
        PlaceRight(_done, S(64));
        int replyWidth = tight ? S(110) : Math.Clamp(Width / 4, S(140), S(280));
        right -= replyWidth;
        _reply.SetBounds(right, top + Math.Max(0, (buttonH - _reply.Height) / 2), replyWidth, _reply.Height);
        right -= S(10);

        _robot.SetBounds(S(8), 0, S(22), h);
        int left = _robot.Right + S(6);
        _question.SetBounds(left, 0, Math.Max(0, right - left), h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var line = new SolidBrush(Theme.Agent);
        e.Graphics.FillRectangle(line, 0, Height - S(2), Width, S(2));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
            _robot.Font?.Dispose();
            Font?.Dispose();
        }
        base.Dispose(disposing);
    }
}
