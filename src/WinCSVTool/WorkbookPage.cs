namespace WinCSVTool;

internal sealed class WorkbookPage : TabPage
{
    public string WorkbookId { get; }
    private readonly string _name;
    private readonly Dictionary<DocumentPage, EventHandler> _titleHandlers = [];
    internal ModernTabControl Sheets { get; } = new() { Dock = DockStyle.Fill, SheetStyle = true };
    internal DocumentPage? Current => Sheets.SelectedTab as DocumentPage;
    internal IEnumerable<DocumentPage> Pages => Sheets.TabPages.OfType<DocumentPage>();

    public WorkbookPage(MainForm owner, string id, string name, Func<DocumentPage, Task> close)
    {
        WorkbookId = id; _name = name; Text = name; BackColor = UiTheme.Surface; Padding = Padding.Empty;
        Controls.Add(Sheets);
        Sheets.SelectedIndexChanged += (_, _) => owner.UpdateInspector();
        Sheets.CloseRequested += async page => { if (page is DocumentPage document) await close(document); };
    }
    internal void Add(DocumentPage page)
    {
        Sheets.TabPages.Add(page); page.UpdateTitle();
        EventHandler handler = (_, _) => UpdateTitle(); _titleHandlers.Add(page, handler); page.TextChanged += handler; UpdateTitle();
    }
    internal void Detach(DocumentPage page)
    {
        if (_titleHandlers.Remove(page, out var handler)) page.TextChanged -= handler;
        Sheets.TabPages.Remove(page); UpdateTitle();
    }
    private void UpdateTitle() => Text = _name + (Pages.Any(p => p.Document.IsDirty) ? " *" : "");
    protected override void Dispose(bool disposing)
    {
        if (disposing) { foreach (var pair in _titleHandlers) pair.Key.TextChanged -= pair.Value; _titleHandlers.Clear(); }
        base.Dispose(disposing);
    }
}
