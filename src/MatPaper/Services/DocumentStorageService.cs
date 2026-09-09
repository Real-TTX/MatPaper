using System.Text;
using MatPaper.Data;

namespace MatPaper.Services;

/// <summary>
/// Places document files on disk under a <see cref="StorageLocation"/> root using
/// human-readable paths derived from the location's path template. All returned
/// relative paths use forward slashes and are guaranteed not to escape the root.
/// </summary>
public sealed class DocumentStorageService
{
    // Characters that are unsafe in a path segment on any of our target
    // platforms (build/runtime is Linux, development is Windows). Path
    // separators are handled separately so template separators survive.
    private static readonly char[] InvalidSegmentChars =
    {
        '<', '>', ':', '"', '|', '?', '*'
    };

    /// <summary>
    /// Resolves the template tokens for the given metadata into a sanitized,
    /// forward-slash relative path (no leading slash, no traversal).
    /// </summary>
    public string BuildRelativePath(
        StorageLocation loc,
        string title,
        DateTime date,
        string? correspondentName,
        string? documentTypeName,
        string originalFileName)
    {
        ArgumentNullException.ThrowIfNull(loc);

        var extension = Path.GetExtension(originalFileName ?? string.Empty).ToLowerInvariant();

        var titleValue = string.IsNullOrWhiteSpace(title)
            ? Path.GetFileNameWithoutExtension(originalFileName ?? string.Empty)
            : title;

        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{Correspondent}"] = SanitizeTokenValue(string.IsNullOrWhiteSpace(correspondentName) ? "Unsorted" : correspondentName),
            ["{Year}"] = date.ToString("yyyy"),
            ["{Month}"] = date.ToString("MM"),
            ["{DocumentType}"] = SanitizeTokenValue(string.IsNullOrWhiteSpace(documentTypeName) ? "Unfiled" : documentTypeName),
            ["{Date}"] = date.ToString("yyyy-MM-dd"),
            ["{Title}"] = SanitizeTokenValue(string.IsNullOrWhiteSpace(titleValue) ? "Untitled" : titleValue),
            // Keep the leading dot; only strip separators/invalid chars.
            ["{Ext}"] = SanitizeTokenValue(extension)
        };

        var template = string.IsNullOrWhiteSpace(loc.PathTemplate)
            ? "{Title}{Ext}"
            : loc.PathTemplate;

        foreach (var token in tokens)
        {
            template = template.Replace(token.Key, token.Value, StringComparison.OrdinalIgnoreCase);
        }

        var rawSegments = template.Split('/', '\\');
        var segments = new List<string>(rawSegments.Length);
        foreach (var raw in rawSegments)
        {
            var segment = SanitizeSegment(raw);
            if (segment.Length > 0)
            {
                segments.Add(segment);
            }
        }

        if (segments.Count == 0)
        {
            segments.Add(SanitizeSegment($"Untitled{extension}"));
        }

        return string.Join('/', segments);
    }

    /// <summary>
    /// Ensures the target directory exists, writes the content (unique-suffixing
    /// the file name on collision) and returns the actual forward-slash relative path.
    /// </summary>
    public async Task<string> SaveNewAsync(StorageLocation loc, string desiredRelativePath, Stream content)
    {
        ArgumentNullException.ThrowIfNull(loc);
        ArgumentNullException.ThrowIfNull(content);

        var root = GetRootFullPath(loc);
        var targetAbsolute = ResolveWithinRoot(root, desiredRelativePath);

        var directory = Path.GetDirectoryName(targetAbsolute)!;
        Directory.CreateDirectory(directory);

        var uniqueAbsolute = MakeUnique(targetAbsolute);

        await using (var file = new FileStream(uniqueAbsolute, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await content.CopyToAsync(file).ConfigureAwait(false);
        }

        return ToRelative(root, uniqueAbsolute);
    }

    /// <summary>
    /// Moves a file to a new relative path within the same location root,
    /// unique-suffixing on collision and cleaning up emptied source directories.
    /// </summary>
    public Task<string> MoveAsync(StorageLocation loc, string currentRelativePath, string desiredRelativePath)
    {
        ArgumentNullException.ThrowIfNull(loc);

        if (string.Equals(currentRelativePath, desiredRelativePath, StringComparison.Ordinal))
        {
            return Task.FromResult(currentRelativePath);
        }

        var root = GetRootFullPath(loc);
        var sourceAbsolute = ResolveWithinRoot(root, currentRelativePath);
        var targetAbsolute = ResolveWithinRoot(root, desiredRelativePath);

        if (string.Equals(sourceAbsolute, targetAbsolute, StringComparison.Ordinal))
        {
            return Task.FromResult(ToRelative(root, sourceAbsolute));
        }

        var directory = Path.GetDirectoryName(targetAbsolute)!;
        Directory.CreateDirectory(directory);

        var uniqueAbsolute = MakeUnique(targetAbsolute);
        File.Move(sourceAbsolute, uniqueAbsolute);

        CleanupEmptyDirectories(root, Path.GetDirectoryName(sourceAbsolute));

        return Task.FromResult(ToRelative(root, uniqueAbsolute));
    }

    /// <summary>
    /// Relocates a file between two locations. Within a single location this
    /// delegates to <see cref="MoveAsync"/>; across locations it copies then
    /// deletes the source and cleans up emptied source directories.
    /// </summary>
    public async Task<string> RelocateAsync(
        StorageLocation fromLoc,
        string fromRelativePath,
        StorageLocation toLoc,
        string desiredRelativePath)
    {
        ArgumentNullException.ThrowIfNull(fromLoc);
        ArgumentNullException.ThrowIfNull(toLoc);

        if (fromLoc.Id == toLoc.Id)
        {
            return await MoveAsync(fromLoc, fromRelativePath, desiredRelativePath).ConfigureAwait(false);
        }

        var fromRoot = GetRootFullPath(fromLoc);
        var toRoot = GetRootFullPath(toLoc);

        var sourceAbsolute = ResolveWithinRoot(fromRoot, fromRelativePath);
        var targetAbsolute = ResolveWithinRoot(toRoot, desiredRelativePath);

        var directory = Path.GetDirectoryName(targetAbsolute)!;
        Directory.CreateDirectory(directory);

        var uniqueAbsolute = MakeUnique(targetAbsolute);

        await using (var source = new FileStream(sourceAbsolute, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var destination = new FileStream(uniqueAbsolute, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await source.CopyToAsync(destination).ConfigureAwait(false);
        }

        File.Delete(sourceAbsolute);
        CleanupEmptyDirectories(fromRoot, Path.GetDirectoryName(sourceAbsolute));

        return ToRelative(toRoot, uniqueAbsolute);
    }

    /// <summary>
    /// Safely combines the location root with a relative path, rejecting any
    /// path that would escape the root.
    /// </summary>
    public string GetAbsolutePath(StorageLocation loc, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return ResolveWithinRoot(GetRootFullPath(loc), relativePath);
    }

    private static string GetRootFullPath(StorageLocation loc)
    {
        if (string.IsNullOrWhiteSpace(loc.RootPath))
        {
            throw new InvalidOperationException($"Storage location '{loc.Name}' has no root path.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(loc.RootPath));
    }

    private static string ResolveWithinRoot(string rootFullPath, string relativePath)
    {
        var normalized = (relativePath ?? string.Empty)
            .Replace('\\', '/')
            .TrimStart('/');

        var combined = Path.GetFullPath(Path.Combine(rootFullPath, normalized));

        var rootWithSeparator = rootFullPath + Path.DirectorySeparatorChar;
        if (!combined.Equals(rootFullPath, StringComparison.Ordinal) &&
            !combined.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Resolved path escapes the storage root.");
        }

        return combined;
    }

    private static string ToRelative(string rootFullPath, string absolutePath)
    {
        var relative = Path.GetRelativePath(rootFullPath, absolutePath);
        return relative.Replace('\\', '/');
    }

    private static string MakeUnique(string absolutePath)
    {
        if (!File.Exists(absolutePath))
        {
            return absolutePath;
        }

        var directory = Path.GetDirectoryName(absolutePath)!;
        var name = Path.GetFileNameWithoutExtension(absolutePath);
        var extension = Path.GetExtension(absolutePath);

        for (var counter = 2; ; counter++)
        {
            var candidate = Path.Combine(directory, $"{name} ({counter}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static void CleanupEmptyDirectories(string rootFullPath, string? startDirectory)
    {
        var current = startDirectory;

        while (!string.IsNullOrEmpty(current) &&
               !current.Equals(rootFullPath, StringComparison.Ordinal) &&
               current.StartsWith(rootFullPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            try
            {
                if (Directory.Exists(current) &&
                    !Directory.EnumerateFileSystemEntries(current).Any())
                {
                    Directory.Delete(current);
                }
                else
                {
                    break;
                }
            }
            catch (IOException)
            {
                break;
            }
            catch (UnauthorizedAccessException)
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static string SanitizeTokenValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch == '/' || ch == '\\')
            {
                builder.Append(' ');
                continue;
            }

            if (char.IsControl(ch) || Array.IndexOf(InvalidSegmentChars, ch) >= 0)
            {
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static string SanitizeSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(segment.Length);
        var previousWasWhitespace = false;

        foreach (var ch in segment)
        {
            if (ch == '/' || ch == '\\' || char.IsControl(ch) || Array.IndexOf(InvalidSegmentChars, ch) >= 0)
            {
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (previousWasWhitespace)
                {
                    continue;
                }

                builder.Append(' ');
                previousWasWhitespace = true;
                continue;
            }

            builder.Append(ch);
            previousWasWhitespace = false;
        }

        // Trim leading/trailing dots and spaces; guard against reserved names.
        var result = builder.ToString().Trim().Trim('.').Trim();
        return result;
    }
}
