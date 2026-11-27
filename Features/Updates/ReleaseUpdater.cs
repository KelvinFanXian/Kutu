using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Kutu.Features.Updates;

internal sealed record AvailableUpdate(Version Version, Uri DownloadUrl, long Size, string Sha256);

internal sealed class ReleaseUpdater(HttpClient client)
{
    private const string AssetName = "Kutu-win-x64.exe";
    private static readonly Uri LatestRelease = new("https://api.github.com/repos/KelvinFanXian/Kutu/releases/latest");
    private const long MaximumSize = 1024L * 1024 * 1024;

    internal async Task<AvailableUpdate?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
        request.Headers.UserAgent.ParseAdd("Kutu-Updater/0.1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseRelease(json.RootElement, currentVersion);
    }

    internal static AvailableUpdate? ParseRelease(JsonElement root, Version currentVersion)
    {
        if (!root.TryGetProperty("tag_name", out var tagNode)) return null;
        var tag = tagNode.GetString()?.TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var version) || version <= currentVersion) return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != AssetName) continue;
            var size = asset.GetProperty("size").GetInt64();
            var digest = asset.GetProperty("digest").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (size <= 0 || size > MaximumSize || digest is null ||
                !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
                digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var download) ||
                download.Scheme != Uri.UriSchemeHttps || download.Host != "github.com" ||
                !download.AbsolutePath.StartsWith("/KelvinFanXian/Kutu/releases/download/", StringComparison.Ordinal))
                throw new InvalidDataException("更新包信息缺少有效的地址或 SHA-256 校验值。");
            return new AvailableUpdate(version, download, size, digest[7..]);
        }
        return null;
    }

    internal async Task<string> DownloadAsync(AvailableUpdate update, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default, string? destinationDirectory = null)
    {
        var directory = destinationDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kutu", "Updates");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"Kutu-{update.Version}-{Guid.NewGuid():N}.exe");
        try
        {
            using var response = await client.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != update.Size)
                throw new InvalidDataException("更新包大小与发布信息不一致。");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, System.IO.FileShare.None, 1024 * 1024, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            long total = 0;
            try
            {
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    total += count;
                    if (total > update.Size) throw new InvalidDataException("更新包超出预期大小。");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    hash.AppendData(buffer, 0, count);
                    progress?.Report((int)(total * 100 / update.Size));
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            await output.FlushAsync(cancellationToken);
            if (total != update.Size ||
                !Convert.ToHexString(hash.GetHashAndReset()).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包完整性校验失败。");
            progress?.Report(100);
            return path;
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }
}
