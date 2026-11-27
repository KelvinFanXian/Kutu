using System.IO;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace Kutu.Features.FileShare;

internal sealed class ShareServer : IDisposable
{
    public const int DefaultPort = 1234;
    public const long MaxUploadBytes = 10L * 1024 * 1024 * 1024;
    private readonly string _root;
    private readonly string _uploadToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly bool _loopbackOnly;
    private WebApplication? _app;

    public int Port { get; }

    public ShareServer(string root, int port = DefaultPort, bool loopbackOnly = false)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Port = port;
        _loopbackOnly = loopbackOnly;
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.WebHost.ConfigureKestrel(options =>
        {
            if (_loopbackOnly) options.ListenLocalhost(Port);
            else options.ListenAnyIP(Port);
            options.Limits.MaxRequestBodySize = MaxUploadBytes;
        });
        var app = builder.Build();
        app.MapMethods("/", ["GET", "HEAD"], HandleRequestAsync);
        app.MapMethods("/{**relativePath}", ["GET", "HEAD"], HandleRequestAsync);
        app.MapPost("/_kutu/upload", HandleUploadAsync);
        try
        {
            await app.StartAsync();
            _app = app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        var app = _app;
        _app = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await app.StopAsync(timeout.Token); }
        finally { await app.DisposeAsync(); }
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();

    private async Task HandleRequestAsync(HttpContext context)
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'";

        var relative = context.Request.RouteValues["relativePath"]?.ToString() ?? "";
        try
        {
            var (fullPath, segments, error) = ResolvePath(relative);
            if (error != 0) { context.Response.StatusCode = error; return; }

            if (Directory.Exists(fullPath!))
            {
                await SendDirectoryAsync(context, fullPath!, segments);
                return;
            }
            if (File.Exists(fullPath!))
            {
                await Results.File(fullPath!, "application/octet-stream", Path.GetFileName(fullPath),
                    enableRangeProcessing: true).ExecuteAsync(context);
                return;
            }
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
        catch (FileNotFoundException) { context.Response.StatusCode = StatusCodes.Status404NotFound; }
        catch (DirectoryNotFoundException) { context.Response.StatusCode = StatusCodes.Status404NotFound; }
        catch (UnauthorizedAccessException) { context.Response.StatusCode = StatusCodes.Status403Forbidden; }
        catch (IOException) { context.Response.StatusCode = StatusCodes.Status500InternalServerError; }
        catch (ArgumentException) { context.Response.StatusCode = StatusCodes.Status400BadRequest; }
    }

    private async Task HandleUploadAsync(HttpContext context)
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Cache-Control"] = "no-store";
        if (context.Request.Headers["X-Kutu-Upload-Token"] != _uploadToken)
        {
            await ErrorAsync(context, StatusCodes.Status403Forbidden, "请从 Kutu 分享页面选择文件上传。");
            return;
        }

        var name = context.Request.Query["name"].ToString();
        if (!ValidFileName(name))
        {
            await ErrorAsync(context, StatusCodes.Status400BadRequest, "文件名无效。");
            return;
        }
        if (context.Request.ContentLength is > MaxUploadBytes)
        {
            await ErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "文件超过 10 GB 上限。");
            return;
        }

        string? temporary = null;
        try
        {
            var (directory, _, error) = ResolvePath(context.Request.Query["path"].ToString());
            if (error != 0) { await ErrorAsync(context, error, "目标文件夹不可访问。"); return; }
            if (!Directory.Exists(directory!))
            {
                await ErrorAsync(context, StatusCodes.Status404NotFound, "目标文件夹不存在。");
                return;
            }
            var destination = Path.Combine(directory!, name);
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                await ErrorAsync(context, StatusCodes.Status409Conflict, "同名文件已存在，请先在本机处理或更改文件名。");
                return;
            }

            temporary = Path.Combine(directory!, $".kutu-upload-{Guid.NewGuid():N}.tmp");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                System.IO.FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                int count;
                while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
                {
                    total += count;
                    if (total > MaxUploadBytes)
                    {
                        await ErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "文件超过 10 GB 上限。");
                        return;
                    }
                    await output.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted);
                }
            }
            File.Move(temporary, destination, overwrite: false);
            temporary = null;
            context.Response.StatusCode = StatusCodes.Status201Created;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("上传完成。");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (FileNotFoundException) { await ErrorAsync(context, StatusCodes.Status404NotFound, "目标文件夹不存在。"); }
        catch (DirectoryNotFoundException) { await ErrorAsync(context, StatusCodes.Status404NotFound, "目标文件夹不存在。"); }
        catch (UnauthorizedAccessException) { await ErrorAsync(context, StatusCodes.Status403Forbidden, "没有写入权限。"); }
        catch (IOException)
        {
            await ErrorAsync(context, StatusCodes.Status409Conflict, "上传失败：文件已存在或磁盘不可写。");
        }
        catch (ArgumentException) { await ErrorAsync(context, StatusCodes.Status400BadRequest, "目标路径无效。"); }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch (IOException) { }
            }
        }
    }

    private static async Task ErrorAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(message);
    }

    private static bool ValidFileName(string name) =>
        name.Length is > 0 and <= 240 &&
        name is not "." and not ".." &&
        name == Path.GetFileName(name) &&
        !name.EndsWith(' ') && !name.EndsWith('.') &&
        !name.Any(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c)) &&
        !HiddenName(name);

    private static bool HiddenName(string name) =>
        name.StartsWith(".kutu-upload-", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".hg", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".svn", StringComparison.OrdinalIgnoreCase);

    private (string? Path, string[] Segments, int Error) ResolvePath(string relative)
    {
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s is "." or ".." || s.Contains('\\') || s.Contains(':') ||
                              s.Any(char.IsControl)))
            return (null, segments, StatusCodes.Status400BadRequest);
        if (segments.Any(HiddenName))
            return (null, segments, StatusCodes.Status404NotFound);

        var path = _root;
        foreach (var segment in segments)
        {
            path = Path.GetFullPath(Path.Combine(path, segment));
            if (!IsWithinRoot(path)) return (null, segments, StatusCodes.Status403Forbidden);
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                return (null, segments, StatusCodes.Status403Forbidden);
        }
        return (path, segments, 0);
    }

    private bool IsWithinRoot(string path)
    {
        var prefix = Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar;
        return path.Equals(_root, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private async Task SendDirectoryAsync(HttpContext context, string directory, string[] segments)
    {
        var items = Directory.EnumerateFileSystemEntries(directory)
            .Where(path => !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) &&
                           !HiddenName(Path.GetFileName(path)))
            .OrderBy(path => !Directory.Exists(path))
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .ThenBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        context.Response.Headers["Content-Security-Policy"] = $"default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-{nonce}'; connect-src 'self'";
        var html = DirectoryPageRenderer.Render(segments, items, _uploadToken, nonce);
        context.Response.ContentType = "text/html; charset=utf-8";
        if (!HttpMethods.IsHead(context.Request.Method))
            await context.Response.WriteAsync(html);
    }
}
