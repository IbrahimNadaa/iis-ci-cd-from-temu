using System.IO.Compression;

namespace IisDeployApi;

internal static class SafeFiles
{
    public static string LocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' ||
            path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ApiException(400, "Use an absolute local Windows path, such as C:\\Sites\\MyApp.");

        string fullPath;
        try { fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ApiException(400, "Invalid filesystem path."); }

        foreach (var segment in fullPath[3..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            ValidateSegment(segment);
        return fullPath;
    }

    public static bool IsWithin(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public static bool Overlaps(string first, string second) => IsWithin(first, second) || IsWithin(second, first);

    public static void RejectReparseAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ApiException(400, "Symbolic links and junctions are not supported.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void ValidateSegment(string segment)
    {
        var stem = segment.Split('.')[0].ToUpperInvariant();
        var reserved = stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) &&
                (stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3'));
        if (segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || reserved)
            throw new ApiException(400, "A path contains an unsupported Windows filename.");
    }

    public static void ExtractZip(string zipPath, string destination, DeploymentOptions options, bool requireFiles)
    {
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > options.MaxArchiveEntries)
            throw new ApiException(413, "The ZIP contains too many entries.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long extractedBytes = 0;
        var fileCount = 0;
        var buffer = new byte[81920];
        foreach (var entry in archive.Entries)
        {
            // Unix symlinks and Windows reparse points must never become deployment files.
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new ApiException(400, "The ZIP contains a symbolic link or junction.");
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Length == 0)
                throw new ApiException(400, "The ZIP contains an invalid entry path.");
            var isDirectory = name.EndsWith('/');
            var segments = name.TrimEnd('/').Split('/');
            foreach (var segment in segments)
            {
                if (segment.Length == 0)
                    throw new ApiException(400, "The ZIP contains an empty path segment.");
                ValidateSegment(segment);
            }
            var target = Path.GetFullPath(Path.Combine(destination, Path.Combine(segments)));
            if (!IsWithin(target, destination) || !seen.Add(target))
                throw new ApiException(400, "The ZIP contains duplicate or escaping paths.");
            if (isDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            if (entry.Length > options.MaxExtractedBytes - extractedBytes)
                throw new ApiException(413, "The ZIP exceeds the extracted size limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            long entryBytes = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (read > options.MaxExtractedBytes - extractedBytes)
                    throw new ApiException(413, "The ZIP exceeds the extracted size limit.");
                output.Write(buffer, 0, read);
                extractedBytes += read;
                entryBytes += read;
            }
            if (entryBytes != entry.Length)
                throw new ApiException(400, "A ZIP entry has an invalid uncompressed length.");
            fileCount++;
        }
        if (requireFiles && fileCount == 0)
            throw new ApiException(400, "The deployment ZIP must contain at least one file.");
    }

    // Iterative traversal checks each item before descending; it never follows reparse points.
    private static IEnumerable<(string Path, bool IsDirectory)> Walk(string root)
    {
        RejectReparseAncestors(root);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var item in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new ApiException(400, "Application folders must not contain symbolic links or junctions.");
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                yield return (item, isDirectory);
                if (isDirectory) pending.Push(item);
            }
        }
    }

    public static void CopyTree(string source, string destination, DeploymentOptions options)
    {
        RejectReparseAncestors(destination);
        Directory.CreateDirectory(destination);
        long bytes = 0;
        var entries = 0;
        foreach (var item in Walk(source))
        {
            if (++entries > options.MaxArchiveEntries)
                throw new ApiException(413, "The application exceeds the backup entry limit.");
            var target = Path.Combine(destination, Path.GetRelativePath(source, item.Path));
            RejectReparseAncestors(target);
            if (item.IsDirectory)
                Directory.CreateDirectory(target);
            else
            {
                var length = new FileInfo(item.Path).Length;
                if (length > options.MaxExtractedBytes - bytes)
                    throw new ApiException(413, "The application exceeds the backup size limit.");
                bytes += length;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(item.Path, target, overwrite: true);
            }
        }
    }

    public static void CreateZip(string source, string zipPath)
    {
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var item in Walk(source))
        {
            var name = Path.GetRelativePath(source, item.Path).Replace('\\', '/');
            if (item.IsDirectory) archive.CreateEntry(name + '/');
            else archive.CreateEntryFromFile(item.Path, name, CompressionLevel.Fastest);
        }
    }

    public static void ClearDirectory(string root)
    {
        // Validate the complete tree before deleting anything, including hidden files.
        var items = Walk(root).ToList();
        foreach (var item in items.Where(item => !item.IsDirectory))
        {
            File.SetAttributes(item.Path, FileAttributes.Normal);
            File.Delete(item.Path);
        }
        foreach (var item in items.Where(item => item.IsDirectory).OrderByDescending(item => item.Path.Length))
            Directory.Delete(item.Path);
    }
}
