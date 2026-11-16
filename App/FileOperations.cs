using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.VisualBasic.FileIO;

namespace ThumbnailExplorer;

internal static class FileOperations
{
    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = 0,
        IgnoreInaccessible = false
    };

    internal static string ValidateName(string name, string directory)
    {
        name = name.Trim();
        string stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        if (name.Length == 0 || name.EndsWith('.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) &&
             "123456789¹²³".Contains(stem[3])))
            throw new ArgumentException(L.Get("InvalidName"));

        return Path.Combine(directory, name);
    }

    internal static string AvailableName(string path, bool isDirectory)
    {
        string parent = Path.GetDirectoryName(path)!;
        string extension = isDirectory ? "" : Path.GetExtension(path);
        string name = Path.GetFileName(path);
        string stem = name[..(name.Length - extension.Length)];
        string candidate = path;
        for (int number = 2; Path.Exists(candidate); number++)
            candidate = Path.Combine(parent, $"{stem} ({number}){extension}");
        return candidate;
    }

    internal static void Transfer(string source, string destination, bool move, ThumbnailStore store)
    {
        var attributes = File.GetAttributes(source);
        bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
        if (string.Equals(source, destination, StringComparison.Ordinal))
            return;

        bool caseOnly = move && string.Equals(source, destination, StringComparison.OrdinalIgnoreCase);
        if (Path.Exists(destination) && !caseOnly)
            throw new IOException(L.Format("NameConflict", Path.GetFileName(destination)));
        if (isDirectory && IsWithin(destination, source))
            throw new IOException(L.Get("FolderIntoItself"));

        // Reject junctions and symbolic links inside copied trees before copying any data.
        if ((isDirectory || !move) && attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException(L.Get("LinkTransferError"));

        var assignments = new List<(string Source, string Destination)>();
        if (store.Exists(source))
            assignments.Add((source, destination));
        if (isDirectory)
        {
            foreach (string child in Directory.EnumerateFileSystemEntries(source, "*", WalkOptions))
            {
                if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException(L.Format("FolderContainsLink", child));
                if (store.Exists(child))
                    assignments.Add((child, Path.Combine(destination, Path.GetRelativePath(source, child))));
            }
        }

        if (isDirectory)
        {
            if (caseOnly)
                Directory.Move(source, destination);
            else if (move)
                FileSystem.MoveDirectory(source, destination, overwrite: false);
            else
                FileSystem.CopyDirectory(source, destination, overwrite: false);
        }
        else if (move)
            File.Move(source, destination);
        else
            File.Copy(source, destination);

        foreach (var assignment in assignments)
            store.Transfer(assignment.Source, assignment.Destination, move);
    }

    internal static void Compress(IEnumerable<string> sources, string destination)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                foreach (string source in sources)
                {
                    var attributes = File.GetAttributes(source);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                        throw new IOException(L.Format("CompressLinkError", source));
                    string name = Path.GetFileName(source);
                    if (!attributes.HasFlag(FileAttributes.Directory))
                    {
                        archive.CreateEntryFromFile(source, name);
                        continue;
                    }

                    archive.CreateEntry(name + "/");
                    // Stop at links before the traversal can enter their targets.
                    foreach (string child in Directory.EnumerateFileSystemEntries(source, "*", WalkOptions))
                    {
                        if (child.Equals(temporary, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                            throw new IOException(L.Format("CompressFolderLinkError", child));
                        string entry = name + "/" + Path.GetRelativePath(source, child).Replace('\\', '/');
                        if (Directory.Exists(child))
                            archive.CreateEntry(entry + "/");
                        else
                            archive.CreateEntryFromFile(child, entry);
                    }
                }
            }
            File.Move(temporary, destination);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static bool IsWithin(string candidate, string directory) =>
        candidate.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
