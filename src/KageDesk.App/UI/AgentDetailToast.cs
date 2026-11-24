using System.Drawing;
using KageDesk.App.Native;
using KageDesk.Core.Models;

namespace KageDesk.App.UI;

public class AgentDetailToast : Form
{
    private readonly System.Windows.Forms.Timer _dismissTimer;
    private readonly AgentSnapshot _agent;

    public AgentDetailToast(AgentSnapshot agent)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.FromArgb(18, 20, 28);
        Size = new Size(280, 110);

        _dismissTimer = new System.Windows.Forms.Timer
        {
            Interval = 4000
        };
        _dismissTimer.Tick += (_, _) => Close();
        _dismissTimer.Start();

        // Position above taskbar near center-right
        Rectangle workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
        Location = new Point(
            Math.Max(workArea.Left + 20, workArea.Right - Width - 40),
            Math.Max(workArea.Top + 20, workArea.Bottom - Height - 140));

        Click += (_, _) => Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;

        // Outer border
        using var pen = new Pen(Color.FromArgb(99, 102, 241), 1.5f);
        g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

        // Header bar
        using var headerBrush = new SolidBrush(Color.FromArgb(30, 32, 48));
        g.FillRectangle(headerBrush, 1, 1, Width - 2, 24);

        // State indicator dot
        Color dotColor = _agent.State switch
        {
            AgentState.Working => Color.FromArgb(34, 197, 94),
            AgentState.NeedsApproval => Color.FromArgb(245, 158, 11),
            AgentState.Failed => Color.FromArgb(239, 68, 68),
            _ => Color.FromArgb(148, 163, 184)
        };
        using var dotBrush = new SolidBrush(dotColor);
        g.FillEllipse(dotBrush, 8, 8, 8, 8);

        // Agent title
        using var titleFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        using var textBrush = new SolidBrush(Color.White);
        g.DrawString(_agent.Name, titleFont, textBrush, 22, 4);

        // Close mark [x]
        using var closeFont = new Font("Segoe UI", 8f, FontStyle.Bold);
        using var closeBrush = new SolidBrush(Color.FromArgb(156, 163, 175));
        g.DrawString("×", closeFont, closeBrush, Width - 16, 4);

        // Body info
        using var bodyFont = new Font("Segoe UI", 8.5f, FontStyle.Regular);
        using var dimBrush = new SolidBrush(Color.FromArgb(209, 213, 219));

        string stateText = $"State: {_agent.State}  |  Role: {_agent.Role}";
        g.DrawString(stateText, bodyFont, dimBrush, 10, 32);

        string repoText = !string.IsNullOrEmpty(_agent.Repo) ? $"Repo: {_agent.Repo}" : "";
        string toolText = !string.IsNullOrEmpty(_agent.ToolName) ? $"Tool: {_agent.ToolName}" : "";
        string details = string.Join("  ", new[] { repoText, toolText }.Where(s => !string.IsNullOrEmpty(s)));
        if (!string.IsNullOrEmpty(details))
        {
            g.DrawString(details, bodyFont, dimBrush, 10, 52);
        }

        string extra = !string.IsNullOrEmpty(_agent.QuestionPrompt)
            ? _agent.QuestionPrompt
            : (!string.IsNullOrEmpty(_agent.TaskTitle) ? _agent.TaskTitle : "");

        if (!string.IsNullOrEmpty(extra))
        {
            using var extraBrush = new SolidBrush(Color.FromArgb(253, 224, 71));
            string trimmed = extra.Length > 40 ? extra[..37] + "..." : extra;
            g.DrawString(trimmed, bodyFont, extraBrush, 10, 72);
        }
    }

    public static void ShowToast(AgentSnapshot agent)
    {
        try
        {
            var toast = new AgentDetailToast(agent);
            toast.Show();
        }
        catch
        {
            // Ignore UI errors
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dismissTimer.Stop();
            _dismissTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
