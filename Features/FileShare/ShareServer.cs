using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
            .ThenBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        static string Link(IEnumerable<string> parts) => "/" + string.Join("/", parts.Select(Uri.EscapeDataString));
        var title = segments.Length == 0 ? "共享文件夹" : segments[^1];
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        context.Response.Headers["Content-Security-Policy"] = $"default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-{nonce}'; connect-src 'self'";
        var html = new StringBuilder("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>");
        html.Append(WebUtility.HtmlEncode(title));
        html.Append(" · Kutu</title><style>body{font-family:system-ui,'Microsoft YaHei',sans-serif;background:#f5f7fb;color:#17294d;max-width:780px;margin:36px auto;padding:0 18px}h1{font-size:26px}h2{font-size:18px;margin:0 0 8px}p{color:#64748b}.card{background:white;border-radius:12px;padding:22px;margin:20px 0}.drop{border:2px dashed #b9c9e5;border-radius:10px;padding:24px;text-align:center;background:#f8fbff}.drop.over{border-color:#2463eb;background:#eff5ff}button{border:0;border-radius:8px;padding:11px 18px;background:#2463eb;color:white;font:inherit;cursor:pointer}.small{font-size:13px}.list{background:white;border-radius:12px;overflow:hidden}.item{display:block;padding:16px 20px;border-bottom:1px solid #edf1f7;color:#2357b8;text-decoration:none;overflow-wrap:anywhere}.item:hover{background:#eff5ff}.hint{font-size:13px;margin-top:22px}#uploadStatus{padding-left:20px;color:#475569}</style></head><body><h1>");
        html.Append(WebUtility.HtmlEncode(title));
        html.Append("</h1><p>可以上传到当前文件夹，也可以浏览和下载已有文件。</p><section class=\"card\"><h2>上传文件到这里</h2><p class=\"small\">支持一次选择多个文件；同名文件不会覆盖。单个文件最大 10 GB。</p><div class=\"drop\" id=\"drop\"><input id=\"pick\" type=\"file\" multiple hidden><button id=\"choose\" type=\"button\">选择文件上传</button><p class=\"small\">也可以把文件拖到这里</p></div><ul id=\"uploadStatus\" aria-live=\"polite\"></ul></section><div class=\"list\">");
        if (segments.Length > 0)
        {
            html.Append("<a class=\"item\" href=\"");
            html.Append(WebUtility.HtmlEncode(Link(segments.SkipLast(1))));
            html.Append("\">↩ 返回上一级</a>");
        }
        foreach (var path in items)
        {
            var name = Path.GetFileName(path);
            var isDirectory = Directory.Exists(path);
            html.Append("<a class=\"item\" href=\"");
            html.Append(WebUtility.HtmlEncode(Link(segments.Append(name))));
            html.Append("\">");
            html.Append(isDirectory ? "📁 " : "📄 ");
            html.Append(WebUtility.HtmlEncode(name));
            html.Append("</a>");
        }
        if (items.Length == 0) html.Append("<div class=\"item\" style=\"color:#64748b\">这个文件夹是空的</div>");
        html.Append("</div><p class=\"hint\">由 Kutu 在局域网内提供文件分享。上传的文件会保存到当前文件夹。</p><script nonce=\"");
        html.Append(nonce);
        html.Append("\">const uploadToken=");
        html.Append(JsonSerializer.Serialize(_uploadToken));
        html.Append(";const uploadPath=");
        html.Append(JsonSerializer.Serialize(string.Join('/', segments)));
        html.Append(";");
        html.Append("""
            const pick=document.getElementById('pick'),drop=document.getElementById('drop'),status=document.getElementById('uploadStatus');
            document.getElementById('choose').onclick=()=>pick.click();
            pick.onchange=()=>uploadFiles(pick.files);
            drop.ondragover=e=>{e.preventDefault();drop.classList.add('over')};
            drop.ondragleave=()=>drop.classList.remove('over');
            drop.ondrop=e=>{e.preventDefault();drop.classList.remove('over');uploadFiles(e.dataTransfer.files)};
            async function uploadFiles(files){
              if(!files||!files.length)return;
              let completed=0;
              for(const file of files){
                const row=document.createElement('li');row.textContent=file.name+'：等待上传';status.append(row);
                const ok=await new Promise(resolve=>{
                  const xhr=new XMLHttpRequest();
                  const query=new URLSearchParams({path:uploadPath,name:file.name});
                  xhr.open('POST','/_kutu/upload?'+query);
                  xhr.setRequestHeader('X-Kutu-Upload-Token',uploadToken);
                  xhr.upload.onprogress=e=>{if(e.lengthComputable)row.textContent=file.name+'：'+Math.round(e.loaded/e.total*100)+'%'};
                  xhr.onload=()=>{row.textContent=file.name+'：'+(xhr.status===201?'上传完成':xhr.responseText||'上传失败');resolve(xhr.status===201)};
                  xhr.onerror=()=>{row.textContent=file.name+'：网络中断';resolve(false)};
                  xhr.send(file);
                });
                if(ok)completed++;
              }
              if(completed===files.length)location.reload();
            }
            </script></body></html>
            """);
        context.Response.ContentType = "text/html; charset=utf-8";
        if (!HttpMethods.IsHead(context.Request.Method))
            await context.Response.WriteAsync(html.ToString());
    }
}
