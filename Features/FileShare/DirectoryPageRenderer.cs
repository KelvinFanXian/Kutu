using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;

namespace Kutu.Features.FileShare;

internal static class DirectoryPageRenderer
{
    private static readonly string Css = ReadResource("Kutu.FileShare.DirectoryCss");
    private static readonly string Script = ReadResource("Kutu.FileShare.DirectoryJs");

    public static string Render(string[] segments, string[] paths, string uploadToken, string nonce)
    {
        var title = segments.Length == 0 ? "共享文件夹" : segments[^1];
        var html = new StringBuilder(4096 + paths.Length * 320);
        html.Append("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>");
        html.Append(WebUtility.HtmlEncode(title));
        html.Append(" · Kutu</title><link rel=\"icon\" type=\"image/x-icon\" href=\"/favicon.ico\"><style>").Append(Css).Append("</style></head><body><main>");

        html.Append("<h1 class=\"srOnly\">").Append(WebUtility.HtmlEncode(title));
        html.Append("</h1><div class=\"heading\"><p class=\"intro\">");
        if (segments.Length > 0) html.Append("当前：").Append(WebUtility.HtmlEncode(title)).Append(" · ");
        html.Append("在当前文件夹中查找、浏览和下载文件。</p><details class=\"upload\" id=\"uploadPanel\"><summary>上传文件</summary><div class=\"uploadContent\"><p>上传到当前文件夹。支持多个文件，同名不覆盖；单个文件最大 10 GB。</p><div class=\"drop\" id=\"drop\"><input id=\"pick\" type=\"file\" multiple hidden><button id=\"choose\" type=\"button\">选择文件</button><span>或拖放到这里</span></div><ul id=\"uploadStatus\" aria-live=\"polite\"></ul></div></details></div>");

        html.Append("""
            <section class="filters" aria-label="文件筛选">
              <label class="searchField"><span>名称</span><input id="search" type="search" placeholder="搜索文件或文件夹" autocomplete="off"></label>
              <label><span>类型</span><select id="kind"><option value="all">全部类型</option><option value="folder">文件夹</option><option value="image">图片</option><option value="document">文档</option><option value="video">视频</option><option value="audio">音频</option><option value="archive">压缩包</option><option value="other">其他文件</option></select></label>
              <label><span>修改时间</span><select id="timeFilter"><option value="all">全部时间</option><option value="today" selected>今天</option><option value="7d">最近 7 天</option><option value="30d">最近 30 天</option><option value="custom">自定义范围</option></select></label>
              <label><span>排序</span><select id="sort"><option value="recent">最近修改</option><option value="name">名称</option><option value="oldest">最早修改</option><option value="largest">文件最大</option><option value="smallest">文件最小</option></select></label>
              <div class="dateRange" id="dateRange" hidden><label><span>开始日期</span><input id="dateFrom" type="date"></label><label><span>结束日期</span><input id="dateTo" type="date"></label></div>
              <button class="reset" id="reset" type="button">清除筛选</button>
            </section>
            <p class="filterHint">时间筛选针对当前文件夹中的文件；文件夹会保留，方便继续浏览。</p>
            <p class="dateError" id="dateError" hidden>结束日期不能早于开始日期。</p>
            """);

        if (segments.Length > 0)
        {
            html.Append("<a class=\"back\" href=\"");
            html.Append(WebUtility.HtmlEncode(Link(segments.SkipLast(1))));
            html.Append("\">← 返回上一级</a>");
        }

        html.Append("<div class=\"listHeading\"><strong id=\"resultCount\">共 ");
        html.Append(paths.Length.ToString(CultureInfo.InvariantCulture));
        html.Append(" 项</strong><span>修改时间 · 大小</span></div><div class=\"list\" id=\"entries\">");
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            var folder = Directory.Exists(path);
            var kind = folder ? "folder" : Kind(name);
            var modifiedUtc = File.GetLastWriteTimeUtc(path);
            var modified = new DateTimeOffset(modifiedUtc).ToUnixTimeMilliseconds();
            var size = folder ? 0 : new FileInfo(path).Length;
            var fileUrl = Link(segments.Append(name));
            var previewable = !folder && size <= PreviewImages.MaxBytes && PreviewImages.ContentType(name) is not null;
            html.Append("<div class=\"item\" data-name=\"").Append(WebUtility.HtmlEncode(name));
            html.Append("\" data-kind=\"").Append(kind);
            html.Append("\" data-time=\"").Append(modified.ToString(CultureInfo.InvariantCulture));
            html.Append("\" data-size=\"").Append(size.ToString(CultureInfo.InvariantCulture));
            html.Append("\">");
            if (previewable)
            {
                var previewUrl = "/_kutu/preview?path=" + Uri.EscapeDataString(string.Join('/', segments.Append(name)));
                html.Append("<button class=\"thumbButton\" type=\"button\" aria-label=\"预览 ")
                    .Append(WebUtility.HtmlEncode(name)).Append("\" data-preview=\"")
                    .Append(WebUtility.HtmlEncode(previewUrl)).Append("\"><img class=\"thumb\" src=\"")
                    .Append(WebUtility.HtmlEncode(previewUrl)).Append("\" alt=\"\" loading=\"lazy\" decoding=\"async\"></button>");
            }
            else html.Append("<span class=\"itemIcon\" aria-hidden=\"true\">").Append(Icon(kind)).Append("</span>");
            html.Append("<a class=\"itemText\" href=\"").Append(WebUtility.HtmlEncode(fileUrl));
            html.Append("\"><span class=\"itemName\">").Append(WebUtility.HtmlEncode(name));
            html.Append("</span><span class=\"itemMeta\"><time datetime=\"");
            html.Append(modifiedUtc.ToString("O", CultureInfo.InvariantCulture));
            html.Append("\">").Append(modifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture));
            html.Append("</time><span>").Append(folder ? "文件夹" : FormatSize(size));
            html.Append("</span></span></a>");
            if (!folder)
            {
                html.Append("<span class=\"itemActions\">");
                if (previewable)
                    html.Append("<button class=\"actionButton previewAction\" type=\"button\" aria-label=\"预览 ")
                        .Append(WebUtility.HtmlEncode(name)).Append("\" title=\"预览\"><svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M2 12s3.6-6 10-6 10 6 10 6-3.6 6-10 6S2 12 2 12Z\"/><circle cx=\"12\" cy=\"12\" r=\"2.5\"/></svg></button>");
                html.Append("<a class=\"actionButton\" href=\"").Append(WebUtility.HtmlEncode(fileUrl))
                    .Append("\" aria-label=\"下载 ").Append(WebUtility.HtmlEncode(name))
                    .Append("\" title=\"下载\"><svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M12 3v12m-4-4 4 4 4-4M4 17v3h16v-3\"/></svg></a></span>");
            }
            html.Append("</div>");
        }
        html.Append("</div><div class=\"empty\" id=\"empty\" hidden></div>");
        html.Append("<nav class=\"pager\" aria-label=\"文件分页\"><label>每页 <select id=\"pageSize\"><option value=\"10\" selected>10 项</option><option value=\"25\">25 项</option><option value=\"50\">50 项</option><option value=\"100\">100 项</option></select></label><div><button id=\"prevPage\" type=\"button\">上一页</button><span id=\"pageInfo\">第 1 / 1 页</span><button id=\"nextPage\" type=\"button\">下一页</button></div></nav>");
        html.Append("<dialog class=\"previewDialog\" id=\"previewDialog\"><div class=\"previewHeader\"><strong id=\"previewTitle\"></strong><button id=\"closePreview\" type=\"button\">关闭</button></div><img id=\"previewImage\" alt=\"\"><a id=\"downloadPreview\">下载原文件</a></dialog>");
        html.Append("<p class=\"footer\">由 Kutu 在局域网内提供文件分享。</p><script nonce=\"");
        html.Append(nonce).Append("\">const uploadToken=");
        html.Append(System.Text.Json.JsonSerializer.Serialize(uploadToken));
        html.Append(";const uploadPath=");
        html.Append(System.Text.Json.JsonSerializer.Serialize(string.Join('/', segments)));
        html.Append(';').Append(Script).Append("</script></main></body></html>");
        return html.ToString();
    }

    private static string Link(IEnumerable<string> parts) => "/" + string.Join("/", parts.Select(Uri.EscapeDataString));

    private static string Kind(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic", ".svg", ".tif", ".tiff" }.Contains(extension)) return "image";
        if (new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".csv", ".rtf", ".json", ".xml" }.Contains(extension)) return "document";
        if (new[] { ".mp4", ".mov", ".avi", ".mkv", ".webm", ".wmv" }.Contains(extension)) return "video";
        if (new[] { ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg" }.Contains(extension)) return "audio";
        if (new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2" }.Contains(extension)) return "archive";
        return "other";
    }

    private static string Icon(string kind) => kind switch
    {
        "folder" => "📁",
        "image" => "🖼️",
        "video" => "🎬",
        "audio" => "🎵",
        "archive" => "🗜️",
        _ => "📄"
    };

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var units = new[] { "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unit = -1;
        do { size /= 1024; unit++; } while (size >= 1024 && unit < units.Length - 1);
        return $"{size:0.#} {units[unit]}";
    }

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource: {name}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
