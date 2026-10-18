using WinCSVTool.Core;

namespace WinCSVTool;

internal static class Program
{
    internal static string DisplayVersion => typeof(Program).Assembly.GetName().Version!.ToString(3);

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var store = new SettingsStore();
        AppSettings settings;
        try { settings = store.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Retain malformed preferences before a later save replaces them.
            string backup = "";
            try
            {
                if (File.Exists(store.Path))
                {
                    backup = store.Path + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8] + ".bak";
                    File.Copy(store.Path, backup, overwrite: false);
                }
            }
            catch (Exception copyError) when (copyError is IOException or UnauthorizedAccessException) { backup = ""; }
            MessageBox.Show("環境設定を読み込めないため、初期設定で起動します。\n" + ex.Message + (backup.Length > 0 ? "\n元の設定: " + backup : ""), "WinCSVTool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            settings = new AppSettings();
        }
        UiTheme.SetTheme(settings.Theme);
        Workspace? recovered = null;
        try
        {
            var folders = new[] { string.IsNullOrWhiteSpace(settings.WorkFolder) ? null : settings.WorkFolder, null }.Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in folders)
            {
                if (recovered is not null) break;
                foreach (var candidate in Workspace.FindRecoveryCandidates(folder))
                {
                    var text = "前回終了しなかった作業データが残っています。\n" + candidate.Path + "\n\n" +
                        (candidate.HasCheckpoint ? "はい: 作業を復旧する\nいいえ: この作業データを破棄する\nキャンセル: そのまま残す" : "この作業は復旧できません。\n" + candidate.Problem + "\n\nはい: フォルダーを開く\nいいえ: この作業データを破棄する\nキャンセル: そのまま残す");
                    var choice = MessageBox.Show(text, "前回の作業データ", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);
                    if (choice == DialogResult.Yes && candidate.HasCheckpoint && recovered is null)
                    {
                        try { recovered = Workspace.Recover(candidate); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
                        { MessageBox.Show("作業を復旧できませんでした。データは削除していません。\n" + ex.Message, "作業の復旧", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                        if (recovered is not null) break;
                    }
                    else if (choice == DialogResult.Yes)
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(candidate.Path) { UseShellExecute = true });
                    else if (choice == DialogResult.No)
                    {
                        if (MessageBox.Show("この作業データを完全に削除しますか？\n元のCSV/Excelファイルは変更しません。\n" + candidate.Path, "作業データの破棄", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) continue;
                        try { Workspace.DiscardRecovery(candidate); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                        { MessageBox.Show("作業データを削除できませんでした。場所を確認して手動で整理してください。\n" + candidate.Path + "\n" + ex.Message, "作業データの削除", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { MessageBox.Show("前回の作業データを確認できませんでした。\n" + ex.Message, "作業復旧", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        Workspace workspace;
        try { workspace = recovered ?? new Workspace(string.IsNullOrWhiteSpace(settings.WorkFolder) ? null : settings.WorkFolder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        {
            var residual = ex.Data["WinCSVTool.ResidualWorkFolder"] as string;
            MessageBox.Show("指定した作業データの保存先を利用できないため、Windowsの一時フォルダーを使用します。\n" + ex.Message +
                (residual is null ? "" : "\n削除できなかった作業フォルダー: " + residual + "\nアプリの終了後にエクスプローラーで確認してください。"), "WinCSVTool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            workspace = new Workspace();
        }
        using (workspace) Application.Run(new MainForm(workspace, args.Where(File.Exists).ToArray(), settings, store));
        if (workspace.CleanupFailures.Count > 0)
            MessageBox.Show("作業データの削除に失敗しました。未保存の内容が残っている可能性があります。\nアプリを終了した後、次の場所をエクスプローラーで確認して手動で整理してください。\n\n" + string.Join("\n\n", workspace.CleanupFailures), "作業データの削除に失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return 0;
    }
}
