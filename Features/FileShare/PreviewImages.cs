using System.IO;

namespace Kutu.Features.FileShare;

internal static class PreviewImages
{
    public const long MaxBytes = 20L * 1024 * 1024;

    public static string? ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => null
    };
}
