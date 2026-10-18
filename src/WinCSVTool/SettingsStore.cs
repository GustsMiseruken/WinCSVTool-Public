using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinCSVTool;

/// <summary>Settings live beside the portable executable, outside version control.</summary>
internal sealed class SettingsStore
{
    private readonly object _sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Path { get; }

    public SettingsStore(string? path = null) => Path = System.IO.Path.GetFullPath(path ?? System.IO.Path.Combine(AppContext.BaseDirectory, ".local", "settings.json"));

    public AppSettings Load()
    {
        lock (_sync) return LoadCore();
    }

    private AppSettings LoadCore()
    {
        if (!File.Exists(Path)) return new AppSettings();
        try
        {
            using var input = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length > 1024 * 1024) throw new InvalidDataException("環境設定ファイルが大きすぎます（上限1MB）。");
            var settings = JsonSerializer.Deserialize<AppSettings>(input, JsonOptions) ?? throw new InvalidDataException("環境設定ファイルの内容が空です。");
            settings.Normalize();
            settings.Validate();
            return settings;
        }
        catch (JsonException ex) { throw new InvalidDataException("環境設定ファイルを読み込めませんでした。JSON形式を確認してください。", ex); }
    }

    public void Save(AppSettings settings)
    {
        lock (_sync) SaveCore(settings);
    }

    private void SaveCore(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, settings, JsonOptions);
                output.Flush(flushToDisk: true);
            }
            if (File.Exists(Path)) File.Replace(temporary, Path, destinationBackupFileName: null);
            else File.Move(temporary, Path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
