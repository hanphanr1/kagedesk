using System.Drawing;
using KageDesk.App.Native;
using KageDesk.Core.Models;

namespace KageDesk.App.UI;

public class AgentDetailToast : Form
{
    private static readonly Font TitleFont = new("Segoe UI", 9.5f, FontStyle.Bold);
    private static readonly Font BadgeFont = new("Segoe UI", 7.5f, FontStyle.Bold);
    private static readonly Font BodyFont = new("Segoe UI", 8.5f, FontStyle.Regular);
    private static readonly Font DetailFont = new("Segoe UI", 8f, FontStyle.Regular);
    private static readonly Font ButtonFont = new("Segoe UI", 8f, FontStyle.Bold);
    private static readonly Font CloseFont = new("Segoe UI", 9f, FontStyle.Bold);

    private static AgentDetailToast? _activeToast;
    private static readonly object _toastLock = new();

    private readonly System.Windows.Forms.Timer _dismissTimer;
    private readonly AgentSnapshot _agent;

    public AgentSnapshot Agent => _agent;

    private readonly Rectangle _focusBtnBounds;
    private readonly Rectangle _closeBtnBounds;

    protected override bool ShowWithoutActivation => true;

    public AgentDetailToast(AgentSnapshot agent)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.FromArgb(18, 20, 28);
        Size = new Size(300, 126);

        _focusBtnBounds = new Rectangle(Width - 116, Height - 32, 102, 22);
        _closeBtnBounds = new Rectangle(Width - 26, 4, 22, 22);

        _dismissTimer = new System.Windows.Forms.Timer
        {
            Interval = 5000 // 5 seconds
        };
        _dismissTimer.Tick += (_, _) => SafeClose();

        Rectangle workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
        Location = new Point(
            Math.Max(workArea.Left + 20, workArea.Right - Width - 40),
            Math.Max(workArea.Top + 20, workArea.Bottom - Height - 140));
    }

    public void StartDismissTimer()
    {
        _dismissTimer.Start();
    }

    private void SafeClose()
    {
        lock (_toastLock)
        {
            if (ReferenceEquals(_activeToast, this))
            {
                _activeToast = null;
            }
        }

        try
        {
            Close();
        }
        catch { }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button == MouseButtons.Left)
        {
            if (_focusBtnBounds.Contains(e.Location))
            {
                if (string.Equals(_agent.Source, "process", StringComparison.OrdinalIgnoreCase) && _agent.ProcessId.HasValue)
                {
                    WindowFocuser.TryFocusProcessWindow(_agent.ProcessId.Value, _agent.ProcessStartTicks);
                }
                else
                {
                    WindowFocuser.TryFocusOrca();
                }
                SafeClose();
                return;
            }

            if (_closeBtnBounds.Contains(e.Location))
            {
                SafeClose();
                return;
            }

            // Clicking card dismisses
            SafeClose();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;

        // Outer border
        using var pen = new Pen(Color.FromArgb(99, 102, 241), 1.5f);
        g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

        // Header bar
        using var headerBrush = new SolidBrush(Color.FromArgb(28, 32, 48));
        g.FillRectangle(headerBrush, 1, 1, Width - 2, 28);

        // State indicator dot
        Color dotColor = _agent.State switch
        {
            AgentState.Working => Color.FromArgb(34, 197, 94),
            AgentState.NeedsApproval => Color.FromArgb(245, 158, 11),
            AgentState.Done => Color.FromArgb(34, 197, 94),
            AgentState.Failed => Color.FromArgb(239, 68, 68),
            _ => Color.FromArgb(148, 163, 184)
        };
        using var dotBrush = new SolidBrush(dotColor);
        g.FillEllipse(dotBrush, 10, 10, 8, 8);

        // Agent title
        using var sf = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        using var textBrush = new SolidBrush(Color.White);
        var titleRect = new RectangleF(24, 5, Width - 110, 20);
        string cleanName = AgentAppearanceRegistry.SanitizeDisplayName(_agent.Name, _agent.Id, _agent.Source, _agent.Role);
        g.DrawString(cleanName, TitleFont, textBrush, titleRect, sf);

        // Action badge (from AgentActionClassifier, NEVER READ_FIL!)
        var (actionKind, actionLabel) = AgentActionClassifier.Classify(_agent.State, _agent.ToolName, _agent.TaskTitle);
        (Color badgeBg, Color badgeFg) = actionKind switch
        {
            AgentActionKind.Approval => (Color.FromArgb(68, 42, 12), Color.FromArgb(253, 224, 71)),
            AgentActionKind.Failed => (Color.FromArgb(74, 18, 26), Color.FromArgb(239, 68, 68)),
            AgentActionKind.Done => (Color.FromArgb(16, 56, 36), Color.FromArgb(34, 197, 94)),
            AgentActionKind.None => (Color.FromArgb(30, 41, 59), Color.FromArgb(148, 163, 184)),
            _ => (Color.FromArgb(14, 52, 74), Color.FromArgb(34, 211, 238))
        };

        int badgeW = (int)g.MeasureString(actionLabel, BadgeFont).Width + 8;
        var badgeRect = new Rectangle(Width - 30 - badgeW, 6, badgeW, 16);
        using var bBgBrush = new SolidBrush(badgeBg);
        using var bFgBrush = new SolidBrush(badgeFg);
        using var bBorderPen = new Pen(badgeFg);
        g.FillRectangle(bBgBrush, badgeRect);
        g.DrawRectangle(bBorderPen, badgeRect);
        using var centerSf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString(actionLabel, BadgeFont, bFgBrush, badgeRect, centerSf);

        // Close mark [x]
        using var closeBrush = new SolidBrush(Color.FromArgb(156, 163, 175));
        g.DrawString("\u00D7", CloseFont, closeBrush, _closeBtnBounds, centerSf);

        // Source label + details
        using var dimBrush = new SolidBrush(Color.FromArgb(209, 213, 219));
        using var subBrush = new SolidBrush(Color.FromArgb(148, 163, 184));

        string sourceLabel = FormatSourceLabel(_agent);

        var sourceRect = new RectangleF(10, 34, Width - 20, 16);
        g.DrawString(sourceLabel, DetailFont, subBrush, sourceRect, sf);

        // State / Role / Elapsed
        string stateRoleText = $"State: {_agent.State}   Role: {_agent.Role}";
        if (_agent.WorkStartedAt.HasValue)
        {
            int elapsedSec = (int)Math.Max(0, (DateTimeOffset.UtcNow - _agent.WorkStartedAt.Value).TotalSeconds);
            int mm = elapsedSec / 60;
            int ss = elapsedSec % 60;
            stateRoleText += $"   Elapsed: {mm:D2}:{ss:D2}";
        }
        var stateRect = new RectangleF(10, 52, Width - 20, 16);
        g.DrawString(stateRoleText, BodyFont, dimBrush, stateRect, sf);

        // Task / Prompt / Tool extra info
        string extra = !string.IsNullOrEmpty(_agent.QuestionPrompt)
            ? _agent.QuestionPrompt!
            : (!string.IsNullOrEmpty(_agent.TaskTitle) ? _agent.TaskTitle! : (!string.IsNullOrEmpty(_agent.ToolName) ? $"Tool: {_agent.ToolName}" : ""));

        if (!string.IsNullOrEmpty(extra))
        {
            using var extraBrush = new SolidBrush(Color.FromArgb(253, 224, 71));
            var extraRect = new RectangleF(10, 72, Width - 130, 20);
            g.DrawString(extra, DetailFont, extraBrush, extraRect, sf);
        }

        // [Focus Window] Button
        using var btnBrush = new SolidBrush(Color.FromArgb(37, 99, 235));
        using var btnPen = new Pen(Color.FromArgb(96, 165, 250));
        g.FillRectangle(btnBrush, _focusBtnBounds);
        g.DrawRectangle(btnPen, _focusBtnBounds);

        using var btnTextBrush = new SolidBrush(Color.White);
        g.DrawString("Focus Window", ButtonFont, btnTextBrush, _focusBtnBounds, centerSf);
    }

    public static string FormatSourceLabel(AgentSnapshot agent)
    {
        if (string.Equals(agent.Source, "process", StringComparison.OrdinalIgnoreCase))
        {
            string provider = ResolveTerminalProvider(agent);
            string termTitle = provider == "Terminal Agent" ? "Terminal Agent" : $"{provider} Terminal";
            return agent.ProcessId.HasValue ? $"{termTitle}  |  PID: {agent.ProcessId.Value}" : termTitle;
        }

        return !string.IsNullOrEmpty(agent.Repo) ? $"Orca Bot  |  Repo: {agent.Repo}" : "Orca Bot";
    }

    public static string ResolveTerminalProvider(AgentSnapshot agent)
    {
        // Inspect Id and Name BEFORE SpriteKey so randomized visual sprites (e.g. "mixi", "neko-dev")
        // still accurately identify the underlying terminal CLI provider.
        string[] candidates =
        {
            agent.Id ?? string.Empty,
            agent.Name ?? string.Empty,
            agent.SpriteKey ?? string.Empty
        };

        for (int i = 0; i < candidates.Length; i++)
        {
            string key = candidates[i].ToLowerInvariant();
            if (key.Contains("omp")) return "OMP";
            if (key.Contains("agy") || key.Contains("antigravity")) return "AGY";
            if (key.Contains("claude")) return "Claude";
            if (key.Contains("codex")) return "Codex";
            if (key.Contains("opencode")) return "OpenCode";
            if (key.Contains("gemini")) return "Gemini";
        }

        return "Terminal Agent";
    }

    public static AgentDetailToast? ActiveToast
    {
        get
        {
            lock (_toastLock)
            {
                return _activeToast;
            }
        }
    }

    public static void ShowToast(AgentSnapshot agent)
    {
        lock (_toastLock)
        {
            AgentDetailToast? prev = _activeToast;
            _activeToast = null;
            if (prev != null && !prev.IsDisposed)
            {
                try
                {
                    prev.Close();
                }
                catch { }
            }

            AgentDetailToast? newToast = null;
            try
            {
                newToast = new AgentDetailToast(agent);
                _activeToast = newToast;
                newToast.Show();
                newToast.StartDismissTimer();
            }
            catch
            {
                if (ReferenceEquals(_activeToast, newToast))
                {
                    _activeToast = null;
                }

                if (newToast != null)
                {
                    try
                    {
                        newToast.Dispose();
                    }
                    catch { }
                }
            }
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        lock (_toastLock)
        {
            if (ReferenceEquals(_activeToast, this))
            {
                _activeToast = null;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_toastLock)
            {
                if (ReferenceEquals(_activeToast, this))
                {
                    _activeToast = null;
                }
            }
            _dismissTimer.Stop();
            _dismissTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
