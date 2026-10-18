namespace WinCSVTool;

internal enum AppTheme { Light, Dark, System }
internal enum SqlPaneLayout { SeparateWindow, Right, Bottom, Tab }

internal sealed class WindowBounds
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public WindowBounds Clone() => (WindowBounds)MemberwiseClone();
}

/// <summary>Portable, user-editable preferences. CSV values themselves are never converted here.</summary>
internal sealed class AppSettings
{
    public string WorkFolder { get; set; } = "";
    public AppTheme Theme { get; set; } = AppTheme.Light;
    public int PageSize { get; set; } = 512;
    public string DefaultEncodingName { get; set; } = "auto";
    public bool DefaultHasHeader { get; set; } = true;
    public char DefaultDelimiter { get; set; } = ',';
    public bool ConfirmReadSettings { get; set; } = true;
    public string DefaultSaveEncodingName { get; set; } = "utf-8";
    public bool DefaultWriteBom { get; set; }
    public string GridFontFamily { get; set; } = "Yu Gothic UI";
    public float GridFontSize { get; set; } = 9.5f;
    public string EditorFontFamily { get; set; } = "Consolas";
    public float EditorFontSize { get; set; } = 11;
    public int ZoomPercent { get; set; } = 100;
    public bool CtrlMouseWheelZoom { get; set; } = true;
    public string CountryLanguage { get; set; } = "ja";
    public SqlPaneLayout SqlLayout { get; set; } = SqlPaneLayout.SeparateWindow;
    public float SqlPaneRatio { get; set; } = 0.42f;
    public bool RememberWindowBounds { get; set; } = true;
    public bool StartMaximized { get; set; }
    public WindowBounds? WindowBounds { get; set; }
    public bool WasMaximized { get; set; }
    public bool InspectorVisible { get; set; } = true;
    public Dictionary<string, string> Shortcuts { get; set; } = ShortcutCatalog.CreateDefaults();

    public AppSettings Clone()
    {
        var result = (AppSettings)MemberwiseClone();
        result.Shortcuts = Shortcuts is null ? ShortcutCatalog.CreateDefaults() : new(Shortcuts, StringComparer.Ordinal);
        result.WindowBounds = WindowBounds?.Clone();
        return result;
    }

    public Keys GetShortcut(string id) => Shortcuts.TryGetValue(id, out var text) && ShortcutCatalog.TryParse(text, out var keys) ? keys : Keys.None;

    /// <summary>Repair preferences written by older versions or edited outside the app.</summary>
    public void Normalize()
    {
        WorkFolder = WorkFolder?.Trim() ?? "";
        if (!Enum.IsDefined(Theme)) Theme = AppTheme.Light;
        if (!Enum.IsDefined(SqlLayout)) SqlLayout = SqlPaneLayout.SeparateWindow;
        PageSize = Math.Clamp(PageSize, 100, 10_000);
        ZoomPercent = Math.Clamp(ZoomPercent, 50, 200);
        CountryLanguage = string.Equals(CountryLanguage, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ja";
        SqlPaneRatio = float.IsFinite(SqlPaneRatio) ? Math.Clamp(SqlPaneRatio, 0.2f, 0.8f) : 0.42f;
        GridFontSize = float.IsFinite(GridFontSize) ? Math.Clamp(GridFontSize, 6, 32) : 9.5f;
        EditorFontSize = float.IsFinite(EditorFontSize) ? Math.Clamp(EditorFontSize, 6, 32) : 11;
        GridFontFamily = string.IsNullOrWhiteSpace(GridFontFamily) ? "Yu Gothic UI" : GridFontFamily.Trim();
        EditorFontFamily = string.IsNullOrWhiteSpace(EditorFontFamily) ? "Consolas" : EditorFontFamily.Trim();
        DefaultEncodingName = CanonicalEncoding(DefaultEncodingName, allowAuto: true) ?? "auto";
        DefaultSaveEncodingName = CanonicalEncoding(DefaultSaveEncodingName, allowAuto: false) ?? "utf-8";
        if (DefaultDelimiter is not (',' or '\t' or ';')) DefaultDelimiter = ',';
        if (WindowBounds is { } bounds && (bounds.Width < 640 || bounds.Height < 400 || bounds.Width > 32_768 || bounds.Height > 32_768)) WindowBounds = null;
        var source = Shortcuts;
        Shortcuts = ShortcutCatalog.CreateDefaults();
        if (source is not null)
        {
            foreach (var definition in ShortcutCatalog.Definitions)
            {
                if (!source.TryGetValue(definition.Id, out var text)) continue;
                if (ShortcutCatalog.TryParse(text, out var keys) && ShortcutCatalog.IsAssignable(keys)) Shortcuts[definition.Id] = ShortcutCatalog.Format(keys);
            }
        }
        // Conflicting hand-edited assignments should never steal another command's shortcut.
        var seen = new HashSet<Keys>();
        if (ShortcutCatalog.Definitions.Select(x => GetShortcut(x.Id)).Where(x => x != Keys.None).Any(x => !seen.Add(x)))
            Shortcuts = ShortcutCatalog.CreateDefaults();
    }

    public void Validate()
    {
        if (!Enum.IsDefined(Theme) || !Enum.IsDefined(SqlLayout)) throw new ArgumentException("テーマまたはSQLパネルの配置が不正です。");
        if (PageSize is < 100 or > 10_000) throw new ArgumentException("1ページの最大表示行数は100～10,000行で指定してください。");
        if (ZoomPercent is < 50 or > 200) throw new ArgumentException("表示倍率は50～200%で指定してください。");
        if (CountryLanguage is not ("ja" or "en")) throw new ArgumentException("国・地域名の表示言語は日本語または英語を選択してください。");
        if (!float.IsFinite(SqlPaneRatio) || SqlPaneRatio is < 0.2f or > 0.8f) throw new ArgumentException("SQLパネルの表示比率は20～80%で指定してください。");
        if (!float.IsFinite(GridFontSize) || GridFontSize is < 6 or > 32 || !float.IsFinite(EditorFontSize) || EditorFontSize is < 6 or > 32)
            throw new ArgumentException("フォントサイズは6～32ポイントで指定してください。");
        if (string.IsNullOrWhiteSpace(GridFontFamily) || string.IsNullOrWhiteSpace(EditorFontFamily)) throw new ArgumentException("フォント名を指定してください。");
        if (CanonicalEncoding(DefaultEncodingName, true) is null || CanonicalEncoding(DefaultSaveEncodingName, false) is null)
            throw new ArgumentException("対応する文字コードを選択してください。");
        if (DefaultDelimiter is not (',' or '\t' or ';')) throw new ArgumentException("区切り文字はカンマ・タブ・セミコロンから選択してください。");
        if (!string.IsNullOrWhiteSpace(WorkFolder))
        {
            try
            {
                if (!System.IO.Path.IsPathFullyQualified(WorkFolder)) throw new ArgumentException("作業データの保存先には絶対パスを指定してください。");
                _ = System.IO.Path.GetFullPath(WorkFolder);
            }
            catch (Exception ex) when (ex is NotSupportedException or System.IO.PathTooLongException)
            { throw new ArgumentException("作業データの保存先のパスが不正です。", ex); }
        }
        if (Shortcuts is null) throw new ArgumentException("ショートカット設定が不正です。");
        var used = new Dictionary<Keys, string>();
        foreach (var definition in ShortcutCatalog.Definitions)
        {
            if (!Shortcuts.TryGetValue(definition.Id, out var text) || !ShortcutCatalog.TryParse(text, out var keys) || !ShortcutCatalog.IsAssignable(keys))
                throw new ArgumentException($"「{definition.Label}」のショートカットが不正です。CtrlまたはAltと組み合わせるか、ファンクションキーを指定してください。");
            if (keys == Keys.None) continue;
            if (used.TryGetValue(keys, out var other)) throw new ArgumentException($"「{definition.Label}」と「{other}」に同じショートカットが設定されています。");
            used.Add(keys, definition.Label);
        }
    }

    private static string? CanonicalEncoding(string? value, bool allowAuto) => value?.Trim().ToLowerInvariant() switch
    {
        "auto" when allowAuto => "auto",
        "utf8" or "utf-8" => "utf-8",
        "shift_jis" or "shift-jis" or "sjis" or "cp932" => "shift_jis",
        "euc-jp" => "euc-jp",
        "utf16" or "utf-16" or "utf-16le" => "utf-16",
        "utf-16be" => "utf-16BE",
        _ => null
    };
}

internal sealed record ShortcutDefinition(string Id, string Label, Keys DefaultKeys);

internal static class ShortcutCatalog
{
    public static IReadOnlyList<ShortcutDefinition> Definitions { get; } = new ShortcutDefinition[]
    {
        new("open", "ファイルを開く", Keys.Control | Keys.O),
        new("new", "新しいCSV", Keys.Control | Keys.N),
        new("save", "保存", Keys.Control | Keys.S),
        new("undo", "直前の変更を取り消す", Keys.Control | Keys.Z),
        new("find", "検索と置換", Keys.Control | Keys.F),
        new("closeTab", "タブを閉じる", Keys.Control | Keys.W),
        new("sql", "SQL分析", Keys.Control | Keys.Q),
        new("settings", "環境設定", Keys.Control | Keys.Oemcomma),
        new("properties", "ファイルの情報", Keys.Alt | Keys.Enter),
        new("refresh", "表示を更新", Keys.F5),
        new("zoomIn", "表示を拡大", Keys.Control | Keys.Oemplus),
        new("zoomOut", "表示を縮小", Keys.Control | Keys.OemMinus),
        new("zoomReset", "表示倍率を100%に戻す", Keys.Control | Keys.D0),
        new("sqlRun", "SQLを実行", Keys.Control | Keys.Enter),
        new("decode", "デコード", Keys.None),
        new("sortFilter", "ソートとフィルター", Keys.None),
        new("stats", "統計", Keys.None),
        new("wrap", "セル内で折り返す", Keys.None),
        new("appendRow", "行を末尾に追加", Keys.None),
        new("deleteRows", "選択行を削除", Keys.None),
        new("addColumn", "列を追加", Keys.None),
        new("renameColumn", "列名を変更", Keys.None),
        new("deleteColumn", "列を削除", Keys.None),
        new("clearView", "ソートとフィルターを解除", Keys.None),
        new("exit", "アプリケーションを終了", Keys.None)
    };

    public static Dictionary<string, string> CreateDefaults() => Definitions.ToDictionary(x => x.Id, x => Format(x.DefaultKeys), StringComparer.Ordinal);

    public static bool TryParse(string? text, out Keys keys)
    {
        keys = Keys.None;
        if (string.IsNullOrWhiteSpace(text)) return true;
        try
        {
            if (new KeysConverter().ConvertFromInvariantString(text) is not Keys parsed) return false;
            keys = parsed;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or FormatException) { return false; }
    }

    public static string Format(Keys keys) => keys == Keys.None ? "" : new KeysConverter().ConvertToInvariantString(keys) ?? "";

    public static bool IsAssignable(Keys keys)
    {
        if (keys == Keys.None) return true;
        var key = keys & Keys.KeyCode;
        var modifiers = keys & Keys.Modifiers;
        if ((keys & ~(Keys.KeyCode | Keys.Control | Keys.Alt | Keys.Shift)) != 0) return false;
        if (key is Keys.None or Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin or Keys.ProcessKey or Keys.Packet) return false;
        if ((int)key < 1 || (int)key > 254) return false;
        if ((modifiers & (Keys.Control | Keys.Alt)) == 0 && key is not (>= Keys.F1 and <= Keys.F24)) return false;
        if (keys is (Keys.Alt | Keys.F4) or (Keys.Alt | Keys.Tab) or (Keys.Alt | Keys.Escape) or (Keys.Control | Keys.Tab) or (Keys.Control | Keys.Shift | Keys.Tab) or (Keys.Control | Keys.Alt | Keys.Delete)) return false;
        if (modifiers == Keys.Control && key is Keys.A or Keys.C or Keys.V or Keys.X or Keys.Y) return false;
        return true;
    }
}
