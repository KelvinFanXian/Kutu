using System.Diagnostics;
using System.IO;

namespace Kutu.Features.Updates;

internal static class UpdateInstaller
{
    internal static void Apply(string target, string processIdText)
    {
        if (!int.TryParse(processIdText, out var processId) || processId <= 0 ||
            !Path.IsPathFullyQualified(target) || !Path.GetFileName(target).Equals("Kutu.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("更新参数无效。");
        try
        {
            using var old = Process.GetProcessById(processId);
            if (!old.WaitForExit(30000)) throw new TimeoutException("旧版本仍在运行，请关闭后重试。");
        }
        catch (ArgumentException) { /* The old process has already exited. */ }
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("找不到更新程序。");
        ReplaceFile(source, target);
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target)! });
    }

    internal static void ReplaceFile(string source, string target)
    {
        var directory = Path.GetDirectoryName(target)!;
        var staged = Path.Combine(directory, $".Kutu-update-{Guid.NewGuid():N}.exe");
        var backup = Path.Combine(directory, "Kutu.previous.exe");
        try
        {
            File.Copy(source, staged);
            if (File.Exists(target)) File.Replace(staged, target, backup, true);
            else File.Move(staged, target);
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }
}
