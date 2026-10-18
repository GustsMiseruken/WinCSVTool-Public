using WinCSVTool.Core;
using System.Text.RegularExpressions;

namespace WinCSVTool;

internal sealed class FindForm : Form
{
    private readonly MainForm _owner;
    private readonly DocumentPage _page;
    private readonly TextBox _search = new() { Width = 470, PlaceholderText = "検索する文字列または正規表現" };
    private readonly TextBox _replacement = new() { Width = 470, PlaceholderText = "置換後の文字列（空欄で削除）" };
    private readonly CheckBox _regex = new() { Text = "正規表現を使用（置換は $1 などを使用可）", AutoSize = true };
    private readonly ComboBox _column = new() { Width = 470, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _result = new() { AutoSize = true };
    private readonly Button _cancel = new() { Text = "中断", AutoSize = true, Enabled = false };
    private readonly FlowLayoutPanel _body;
    private long _next;
    private bool _running;
    private bool _cancelRequested;
    private bool _closeRequested;
    private enum WorkPhase { Idle, Searching, Previewing, Confirming, Applying, Reloading, Synchronizing }
    private WorkPhase _phase;

    public FindForm(MainForm owner, DocumentPage page)
    {
        _owner = owner; _page = page; _next = page.Offset;
        Text = "検索と置換 — " + page.Document.DisplayName; ClientSize = new Size(580, 500);
        StartPosition = FormStartPosition.CenterParent; Font = owner.Font; BackColor = Color.White; MaximizeBox = false; MinimizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog; AutoScaleMode = AutoScaleMode.Dpi;
        _column.Items.Add("すべての列"); foreach (var col in page.Document.Columns) _column.Items.Add(col.HeaderName); _column.SelectedIndex = 0;
        var buttons = new FlowLayoutPanel { Width = 490, Height = 42 };
        var find = new Button { Text = "次を検索", AutoSize = true, MinimumSize = new Size(110, 34) }; var replace = new Button { Text = "すべて置換…", AutoSize = true, MinimumSize = new Size(110, 34) };
        find.Click += async (_, _) => await FindAsync(); replace.Click += async (_, _) => await ReplaceAsync();
        buttons.Controls.AddRange([find, replace]);
        _body = Dialogs.Body(new Label { Text = "検索文字列", AutoSize = true }, _search, new Label { Text = "置換後の文字列", AutoSize = true }, _replacement,
            _regex, _column, buttons, _result, new Label { Text = "対象は現在のフィルター結果。検索は次の一致行へ進みます。", AutoSize = true });
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(18, 12, 18, 10), FlowDirection = FlowDirection.RightToLeft };
        _cancel.MinimumSize = new Size(110, 34);
        footer.Controls.Add(_cancel); _cancel.Click += (_, _) => RequestCancellation();
        Controls.Add(_body); Controls.Add(footer); footer.SendToBack();
        UiTheme.ThemeForm(this); UiTheme.StyleButton(find, primary: true); footer.BackColor = UiTheme.Header;
        _search.TextChanged += (_, _) => _next = 0;
        _column.SelectedIndexChanged += (_, _) => _next = 0;
        _regex.CheckedChanged += (_, _) => _next = 0;
        FormClosing += (_, e) =>
        {
            if (!_running) return;
            _closeRequested = true; RequestCancellation(); e.Cancel = true;
        };
    }

    private bool CanContinue => !_closeRequested && !_cancelRequested && !IsDisposed && !Disposing && !_owner.IsDisposed && !_page.IsDisposed;

    private void BeginOperation(WorkPhase phase)
    {
        _cancelRequested = false; _closeRequested = false; _running = true; SetPhase(phase);
    }

    private void SetPhase(WorkPhase phase)
    {
        _phase = phase;
        if (IsDisposed || Disposing) return;
        _body.Enabled = phase == WorkPhase.Idle;
        _cancel.Enabled = phase is not (WorkPhase.Idle or WorkPhase.Confirming or WorkPhase.Synchronizing) && !_cancelRequested;
    }

    private void RequestCancellation()
    {
        if (!_running || _phase is WorkPhase.Idle or WorkPhase.Synchronizing) return;
        _cancelRequested = true;
        if (!IsDisposed && !Disposing) _cancel.Enabled = false;
        _owner.CancelWork();
    }

    private void EndOperation()
    {
        _running = false; SetPhase(WorkPhase.Idle);
        if (_closeRequested && !IsDisposed && !Disposing) Close();
    }

    private async Task FindAsync()
    {
        if (_running || _search.Text.Length == 0) return;
        var search = _search.Text; var regex = _regex.Checked; int? col = _column.SelectedIndex <= 0 ? null : _column.SelectedIndex - 1;
        var start = _next;
        FindMatch? match = null; BeginOperation(WorkPhase.Searching);
        try
        {
            if (!await _owner.RunWork("検索", token =>
            {
                match = _owner.Workspace.Find(_page.Document, _page.View, col, search, regex, start, token);
                if (match is null && start > 0) match = _owner.Workspace.Find(_page.Document, _page.View, col, search, regex, 0, token);
            }) || !CanContinue) return;
            if (match is null) { _result.Text = "一致する値がありません。"; return; }
            SetPhase(WorkPhase.Reloading);
            if (!await _page.ReloadAsync(match.Offset / _page.PageSize * _page.PageSize) || !CanContinue) return;
            var row = match.Offset - _page.Offset;
            if (row < 0 || row >= _page.Grid.RowCount || match.Column < 0 || match.Column >= _page.Grid.ColumnCount) return;
            _page.Grid.ClearSelection(); _page.Grid.CurrentCell = _page.Grid[match.Column, (int)(match.Offset - _page.Offset)];
            _page.Grid.CurrentCell.Selected = true; _owner.UpdateInspector();
            _next = match.Offset + 1;
            _result.Text = $"行 {match.Offset + 1:N0} / 列 {match.Column + 1} に一致";
        }
        finally { EndOperation(); }
    }

    private async Task ReplaceAsync()
    {
        if (_running || _search.Text.Length == 0) return;
        var search = _search.Text; var replacement = _replacement.Text; var regex = _regex.Checked; int? col = _column.SelectedIndex <= 0 ? null : _column.SelectedIndex - 1;
        long count = 0; BeginOperation(WorkPhase.Previewing);
        RowScope? scope = null;
        try
        {
            BulkChangePreview? preview = null;
            if (!await _owner.RunWork("置換の変更予定を確認", token =>
            {
                token.ThrowIfCancellationRequested();
                // Reject an invalid pattern before copying a potentially multi-million-row scope.
                if (regex) _ = new Regex(search, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                scope = _owner.Workspace.CaptureScope(_page.Document, _page.View, token);
                preview = _owner.Workspace.PreviewReplace(_page.Document, scope, col, search, replacement, regex, token);
            }) || !CanContinue) return;
            SetPhase(WorkPhase.Confirming);
            if (!_owner.ConfirmBulkChange("すべて置換", preview!, col is null ? "すべての列" : _page.Document.Columns[col.Value].HeaderName) || !CanContinue) return;
            SetPhase(WorkPhase.Applying);
            var committed = false;
            _page.InvalidateDisplayedRows();
            try
            {
                await _owner.RunWork("置換", token =>
                {
                    using (var undo = _owner.Workspace.BeginUndo(_page.Document, "すべて置換"))
                    {
                        count = _owner.Workspace.Replace(_page.Document, scope!, col, search, replacement, regex, token);
                        undo.Complete();
                    }
                    committed = true;
                });
            }
            finally
            {
                // Completion of the DB edit and completion of its display refresh are separate.
                // Even a late cancel/close or failed Undo finalization must refresh the parent page.
                _next = 0;
                if (!_owner.IsDisposed && !_owner.Disposing && !_page.IsDisposed && !_page.Disposing)
                {
                    SetPhase(WorkPhase.Synchronizing);
                    _page.UpdateTitle();
                    var refreshed = await _page.ReloadAsync(canCancel: false);
                    if (!IsDisposed && !Disposing)
                        _result.Text = committed
                            ? refreshed ? $"{count:N0}セルを置換しました。" : $"{count:N0}セルの置換は完了しました。表示の更新が必要です。"
                            : "置換処理を終了しました。" + (refreshed ? "" : "表示の更新が必要です。");
                }
            }
        }
        finally
        {
            try { scope?.Dispose(); }
            finally { EndOperation(); }
        }
    }
}
