using System;
using System.IO;
using System.Text.Json;

namespace ThumbnailExplorer;

internal sealed class ExplorerSettings
{
    internal static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThumbnailExplorer", "settings.json");

    internal string FilePath { get; set; } = DefaultPath;

    public bool ShowHidden { get; set; }
    public bool ShowExtensions { get; set; } = true;
    public string Theme { get; set; } = "Light";
    public int Sort { get; set; }
    public bool SortDescending { get; set; }
    public double Zoom { get; set; } = 1;
    public string? Folder { get; set; }

    internal static ExplorerSettings Load()
    {
        try
        {
            return File.Exists(DefaultPath)
                ? JsonSerializer.Deserialize<ExplorerSettings>(File.ReadAllText(DefaultPath)) ?? new()
                : new();
        }
        catch (Exception error) when (ThumbnailStore.IsReadFailure(error))
        {
            return new();
        }
    }

    internal void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
