using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ThumbnailExplorer;

public sealed class FileEntry(string fullPath, bool isDirectory, DateTime modified = default, long size = 0) : INotifyPropertyChanged
{
    public string FullPath { get; } = fullPath;
    public bool IsDirectory { get; } = isDirectory;
    public string Name { get; } = Path.GetFileName(fullPath);
    public DateTime Modified { get; } = modified;
    public long Size { get; } = size;
    public string DisplayName { get; private set; } = Path.GetFileName(fullPath);
    internal void SetExtensionVisibility(bool show)
    {
        string stem = Path.GetFileNameWithoutExtension(Name);
        string next = show || IsDirectory || stem.Length == 0 ? Name : stem;
        if (next == DisplayName)
            return;
        DisplayName = next;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
    }

    public string Glyph => IsDirectory ? "\uE8D5" : "\uE8A5";
    public string Kind => IsDirectory ? L.Get("Folder")
        : Path.GetExtension(Name).TrimStart('.') is { Length: > 0 } ext ? ext.ToUpperInvariant() : L.Get("File");
    public BitmapSource? Thumbnail { get; private set; }
    public bool IsCustom { get; private set; }
    public bool HasThumbnail => Thumbnail is not null;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void SetThumbnail(BitmapSource? bitmap, bool isCustom)
    {
        Thumbnail = bitmap;
        IsCustom = isCustom;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasThumbnail)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCustom)));
    }
}

internal sealed record TextStyle(string Text, string Font, double Size, string Color);
internal sealed record CanvasLayer(string Kind, double X, double Y, double Width, double Height, string Color,
    string Text = "", string Font = "Yu Gothic UI", double FontSize = 54)
{
    internal string Label => Kind switch
    {
        "text" => string.IsNullOrWhiteSpace(Text) ? L.Get("Text") : L.Format("TextLayer", Text.Split('\n')[0]),
        "ellipse" => L.Get("Ellipse"),
        "triangle" => L.Get("Triangle"),
        _ => L.Get("Rectangle")
    };
}
internal sealed record ThumbnailDraft(IReadOnlyList<CanvasLayer> Layers, BitmapSource? Background, StrokeCollection Strokes);

internal sealed class ThumbnailStore(string root)
{
    internal const int CanvasSize = 512;

    internal static readonly TextStyle DefaultText = new("", "Yu Gothic UI", 54, "#FF242342");
    internal static readonly string DefaultRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThumbnailExplorer", "Thumbnails");

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".wdp", ".jxr"
    };

    private string EntryPath(string filePath)
    {
        string normalized = Path.GetFullPath(filePath);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(root, hash + ".zip");
    }

    internal (BitmapSource? Bitmap, bool IsCustom) Preview(FileEntry file)
    {
        string archivePath = EntryPath(file.FullPath);
        try
        {
            if (File.Exists(archivePath))
            {
                using var archive = OpenArchive(archivePath);
                using var preview = RequiredEntry(archive, "preview.png").Open();
                return (Decode(preview, 144), true);
            }
        }
        catch (Exception error) when (IsReadFailure(error))
        {
            // A damaged thumbnail should not prevent the folder from opening.
        }

        try
        {
            return !file.IsDirectory && ImageExtensions.Contains(Path.GetExtension(file.Name))
                ? (LoadImage(file.FullPath, 144), false)
                : (null, false);
        }
        catch (Exception error) when (IsReadFailure(error))
        {
            return (null, false);
        }
    }

    internal ThumbnailDraft? Read(string filePath)
    {
        string archivePath = EntryPath(filePath);
        if (!File.Exists(archivePath))
            return null;

        using var archive = OpenArchive(archivePath);
        List<CanvasLayer> layers = [];
        if (archive.GetEntry("layers.json") is { } scene)
        {
            using var metadata = scene.Open();
            layers = JsonSerializer.Deserialize<List<CanvasLayer>>(metadata) ?? [];
        }
        else if (archive.GetEntry("text.json") is { } original)
        {
            using var metadata = original.Open();
            if (JsonSerializer.Deserialize<TextStyle>(metadata) is { Text.Length: > 0 } old)
                layers.Add(new CanvasLayer("text", 21, 210, 470, 0, old.Color, old.Text, old.Font, old.Size));
        }
        if (layers.Count > 100 || layers.Exists(layer =>
            layer is null || layer.Kind is not ("text" or "rectangle" or "ellipse" or "triangle") ||
            !double.IsFinite(layer.X) || !double.IsFinite(layer.Y) ||
            !double.IsFinite(layer.Width) || layer.Width is < 8 or > CanvasSize ||
            !double.IsFinite(layer.Height) || layer.Height is < 0 or > CanvasSize ||
            !double.IsFinite(layer.FontSize) || layer.FontSize is < 8 or > 160 ||
            layer.Text is null || layer.Text.Length > 500 || string.IsNullOrWhiteSpace(layer.Font) ||
            layer.Color is null || !layer.Color.StartsWith('#')))
            throw new InvalidDataException(L.Get("InvalidLayerData"));

        BitmapSource? image = null;
        if (archive.GetEntry("image.png") is { } imageEntry)
        {
            using var imageStream = imageEntry.Open();
            image = Decode(imageStream);
        }

        // Ink's ISF reader needs a seekable stream.
        using var ink = RequiredEntry(archive, "strokes.isf").Open();
        using var seekableInk = new MemoryStream();
        ink.CopyTo(seekableInk);
        seekableInk.Position = 0;
        return new ThumbnailDraft(layers, image, new StrokeCollection(seekableInk));
    }

    internal void Save(string filePath, ThumbnailDraft draft, BitmapSource preview)
    {
        Directory.CreateDirectory(root);
        string destination = EntryPath(filePath);
        string temporaryPath = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using (var metadata = archive.CreateEntry("layers.json").Open())
                    JsonSerializer.Serialize(metadata, draft.Layers);
                using (var inkEntry = archive.CreateEntry("strokes.isf").Open())
                using (var ink = new MemoryStream())
                {
                    draft.Strokes.Save(ink);
                    ink.Position = 0;
                    ink.CopyTo(inkEntry);
                }

                if (draft.Background is { } image)
                    WritePng(archive, "image.png", image);
                WritePng(archive, "preview.png", preview);
            }
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    internal bool Exists(string filePath) => File.Exists(EntryPath(filePath));

    internal void Transfer(string source, string destination, bool move)
    {
        string oldPath = EntryPath(source);
        string newPath = EntryPath(destination);
        if (oldPath == newPath || !File.Exists(oldPath))
            return;
        if (move)
            File.Move(oldPath, newPath, overwrite: true);
        else
            File.Copy(oldPath, newPath, overwrite: true);
    }

    internal void Remove(string filePath) => File.Delete(EntryPath(filePath));

    internal static BitmapSource? LoadDefaultBackground(FileEntry file) =>
        !file.IsDirectory && ImageExtensions.Contains(Path.GetExtension(file.Name)) ? LoadImage(file.FullPath, 1024) : null;

    internal static BitmapSource LoadImage(string filePath, int longestEdge)
    {
        using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Decode(file, longestEdge);
    }

    private static BitmapSource Decode(Stream source, int longestEdge = 0)
    {
        using var buffer = source.CanSeek ? null : new MemoryStream();
        if (buffer is not null)
        {
            source.CopyTo(buffer);
            buffer.Position = 0;
        }

        Stream stream = buffer ?? source;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        if (longestEdge > 0)
        {
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (frame.PixelWidth >= frame.PixelHeight && frame.PixelWidth > longestEdge)
                bitmap.DecodePixelWidth = longestEdge;
            else if (frame.PixelHeight > longestEdge)
                bitmap.DecodePixelHeight = longestEdge;
            stream.Position = 0;
        }
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static ZipArchive OpenArchive(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try { return new ZipArchive(file, ZipArchiveMode.Read); }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static ZipArchiveEntry RequiredEntry(ZipArchive archive, string name) =>
        archive.GetEntry(name) ?? throw new InvalidDataException(L.Get("MissingThumbnailData"));

    private static void WritePng(ZipArchive archive, string name, BitmapSource bitmap)
    {
        using var entry = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        buffer.Position = 0;
        buffer.CopyTo(entry);
    }

    internal static bool IsReadFailure(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or FormatException
            or InvalidOperationException or ArgumentException or JsonException
            or System.Runtime.InteropServices.COMException;
}
