using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Kutu.Features.FileShare;

var scratch = Path.Combine(Path.GetTempPath(), "KutuTests-" + Guid.NewGuid().ToString("N"));
var shared = Path.Combine(scratch, "shared");
Directory.CreateDirectory(shared);
Directory.CreateDirectory(Path.Combine(shared, "子目录"));
Directory.CreateDirectory(Path.Combine(shared, ".git"));
File.WriteAllText(Path.Combine(shared, "a & b.txt"), "hello", Encoding.UTF8);
File.WriteAllText(Path.Combine(shared, "子目录", "中文.txt"), "nested", Encoding.UTF8);
File.WriteAllText(Path.Combine(scratch, "secret.txt"), "secret", Encoding.UTF8);

var reservation = new TcpListener(IPAddress.Loopback, 0);
reservation.Start();
var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
reservation.Stop();
var server = new ShareServer(shared, port, loopbackOnly: true);
using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
try
{
    Check(ShareServer.DefaultPort == 1234, "正式共享端口固定为 1234");
    await server.StartAsync();

    var root = await client.GetStringAsync("/");
    Check(root.Contains("a &amp; b.txt") && root.Contains("子目录") && !root.Contains(".git"),
        "目录列表、HTML 转义和仓库元数据隐藏");
    Check(root.Contains("选择文件上传") && root.Contains("拖到这里"), "网页上传入口");
    var tokenMatch = Regex.Match(root, "const uploadToken=\"([0-9A-F]+)\";");
    Check(tokenMatch.Success, "网页上传令牌");
    var token = tokenMatch.Groups[1].Value;

    var file = await client.GetAsync("/a%20%26%20b.txt");
    Check(file.StatusCode == HttpStatusCode.OK && await file.Content.ReadAsStringAsync() == "hello",
        "特殊文件名下载");
    Check(file.Content.Headers.ContentDisposition?.DispositionType == "attachment", "附件下载响应头");

    var nested = await client.GetAsync("/子目录/中文.txt");
    Check(nested.StatusCode == HttpStatusCode.OK && await nested.Content.ReadAsStringAsync() == "nested",
        "中文路径下载");

    using var head = new HttpRequestMessage(HttpMethod.Head, "/a%20%26%20b.txt");
    var headResponse = await client.SendAsync(head);
    Check(headResponse.StatusCode == HttpStatusCode.OK, "HEAD 请求");

    var traversal = await client.GetAsync("/%2e%2e/secret.txt");
    Check(traversal.StatusCode != HttpStatusCode.OK, "路径越界拒绝");

    var missing = await client.GetAsync("/missing.txt");
    Check(missing.StatusCode == HttpStatusCode.NotFound, "不存在文件返回 404");
    var metadata = await client.GetAsync("/.git/");
    Check(metadata.StatusCode == HttpStatusCode.NotFound, "仓库元数据不可访问");

    var noToken = await Upload("", "new.txt", "uploaded", null);
    Check(noToken.StatusCode == HttpStatusCode.Forbidden, "无令牌上传被拒绝");
    var uploaded = await Upload("", "new.txt", "uploaded", token);
    Check(uploaded.StatusCode == HttpStatusCode.Created && File.ReadAllText(Path.Combine(shared, "new.txt")) == "uploaded",
        "上传到根目录");
    var duplicate = await Upload("", "new.txt", "replacement", token);
    Check(duplicate.StatusCode == HttpStatusCode.Conflict && File.ReadAllText(Path.Combine(shared, "new.txt")) == "uploaded",
        "同名文件不覆盖");
    var childUpload = await Upload("子目录", "friend.txt", "child", token);
    Check(childUpload.StatusCode == HttpStatusCode.Created && File.ReadAllText(Path.Combine(shared, "子目录", "friend.txt")) == "child",
        "上传到当前子目录");
    var badName = await Upload("", "..\\secret.txt", "bad", token);
    Check(badName.StatusCode == HttpStatusCode.BadRequest && File.ReadAllText(Path.Combine(scratch, "secret.txt")) == "secret",
        "上传文件名越界被拒绝");
    var badPath = await Upload("..", "bad.txt", "bad", token);
    Check(badPath.StatusCode == HttpStatusCode.BadRequest, "上传目录越界被拒绝");
    Check(!Directory.EnumerateFiles(shared, ".kutu-upload-*.tmp").Any(), "上传临时文件已清理");

    await server.StopAsync();
    Console.WriteLine("PASS: all integration checks");
}
finally
{
    server.Dispose();
    Directory.Delete(scratch, recursive: true);
}

async Task<HttpResponseMessage> Upload(string path, string name, string body, string? token)
{
    var url = "/_kutu/upload?path=" + Uri.EscapeDataString(path) + "&name=" + Uri.EscapeDataString(name);
    using var request = new HttpRequestMessage(HttpMethod.Post, url)
    {
        Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body))
    };
    if (token is not null) request.Headers.Add("X-Kutu-Upload-Token", token);
    return await client.SendAsync(request);
}

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception($"FAIL: {name}");
    Console.WriteLine($"PASS: {name}");
}
