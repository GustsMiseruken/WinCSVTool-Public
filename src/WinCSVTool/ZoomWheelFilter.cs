namespace WinCSVTool;

/// <summary>One app-wide zoom preference, including native editors which otherwise zoom independently.</summary>
internal sealed class ZoomWheelFilter(MainForm owner) : IMessageFilter
{
    public bool PreFilterMessage(ref Message message)
    {
        if (message.Msg != 0x020A || owner.IsDisposed || ((message.WParam.ToInt64() & 8) == 0 && (Control.ModifierKeys & Keys.Control) == 0)) return false;
        var target = Control.FromHandle(message.HWnd) ?? Control.FromChildHandle(message.HWnd);
        if (target is null) return false;
        var form = target.FindForm();
        if (form != owner && !owner.OwnsSqlForm(form)) return false;
        // Block RichEdit's independent Ctrl+wheel behavior even when app zoom is disabled.
        if (!owner.Settings.CtrlMouseWheelZoom) return true;
        var delta = unchecked((short)((message.WParam.ToInt64() >> 16) & 0xffff));
        if (delta != 0) owner.SetZoomPercent(owner.Settings.ZoomPercent + Math.Sign(delta) * 10);
        return true;
    }
}
