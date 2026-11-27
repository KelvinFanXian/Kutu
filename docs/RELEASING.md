# 发布 Kutu

当前版本号在 `Kutu.csproj` 的 `<Version>` 中。更新功能读取 GitHub 仓库的最新正式 Release，寻找名为 `Kutu-win-x64.exe` 的资源。仓库中的 `.github/workflows/release.yml` 在推送 `v*` 标签后运行测试、发布 Windows x64 单文件程序并创建 Release。

## 发布步骤

1. 完成测试和文档，修改 `Kutu.csproj` 版本号，例如 `0.2.0`，提交并推送到 `main`。
2. 在需要公开发布该版本时，创建并推送与版本一致的标签：

   ```powershell
   git tag v0.2.0
   git push origin v0.2.0
   ```

3. 检查 GitHub Actions 和 Release 页面，确认资源名、大小与 SHA-256 摘要均已生成。用上一版本 Kutu 检查更新并完成一次真实更新。

推送标签会触发公开发布；只在准备好给用户下载时执行。当前还没有正式 Release，更新检查会正常显示没有新版本。
