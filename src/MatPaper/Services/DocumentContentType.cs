namespace MatPaper.Services;

/// <summary>Maps a document file name to the MIME type used when serving it.</summary>
public static class DocumentContentType
{
    public static string FromFileName(string? fileName) => Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".tif" or ".tiff" => "image/tiff",
        _ => "application/octet-stream",
    };
}
