using System.Drawing;
using KageDesk.App.Native;

namespace KageDesk.App.UI;

public class UiDispatcherForm : Form
{
    private static readonly uint WmTaskbarCreated = Win32Interop.RegisterWindowMessage("TaskbarCreated");

    public event Action? TaskbarCreated;

    protected override bool ShowWithoutActivation => true;

    public UiDispatcherForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Opacity = 0;
        Size = Size.Empty;
        Location = new Point(-2000, -2000);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Win32Interop.WS_EX_TOOLWINDOW | Win32Interop.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)WmTaskbarCreated && WmTaskbarCreated != 0)
        {
            TaskbarCreated?.Invoke();
        }
        base.WndProc(ref m);
    }
}
