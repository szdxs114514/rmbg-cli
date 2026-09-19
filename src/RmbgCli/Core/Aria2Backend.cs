using System.Diagnostics;
using System.IO.Compression;
using RmbgCli.Cli;

namespace RmbgCli.Core;

/// <summary>
/// aria2 便携版后端。
///
/// 设计取舍：
///   - 优先使用本仓库 tools\aria2\aria2c.exe（便携、可控、可固定校验值）；
///   - 其次查找 PATH 上的 aria2c（例如 winget / scoop 安装的）；
///   - 都没有时可从官方 GitHub Release 自动安装便携版，**必须经用户确认**，
///     并校验下载包的 SHA256，避免镜像损坏或被篡改。
/// </summary>
internal static class Aria2Backend
{
    /// <summary>固定使用的 aria2 版本。</summary>
    public const string Version = "1.37.0";

    private const string AssetName = "aria2-1.37.0-win-64bit-build1";

    /// <summary>官方 GitHub Release 直链。</summary>
    public const string DownloadUrl =
        "https://github.com/aria2/aria2/releases/download/release-1.37.0/aria2-1.37.0-win-64bit-build1.zip";

    /// <summary>官方包的 SHA256（于 2026-09 实测，用于校验镜像是否原样转发）。</summary>
    public const string ExpectedSha256 =
        "67D015301EEF0B612191212D564C5BB0A14B5B9C4796B76454276A4D28D9B288";

    public const long ExpectedPackageBytes = 2_475_379;

    private const string ExecutableName = "aria2c.exe";

    /// <summary>便携安装目录：<仓库根>\tools\aria2。</summary>
    public static string PortableDirectory()
        => Path.Combine(RepositoryRoot(), "tools", "aria2");

    /// <summary>
    /// 定位可用的 aria2c.exe。
    /// </summary>
    /// <returns>找到则返回绝对路径，否则返回 null。</returns>
    public static string? Locate()
    {
        string portable = Path.Combine(PortableDirectory(), ExecutableName);
        if (File.Exists(portable))
        {
            return portable;
        }

        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        foreach (string directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(directory, ExecutableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // PATH 中存在非法路径片段，忽略。
            }
        }

        return null;
    }

    /// <summary>读取 aria2c 版本号（失败返回 null）。</summary>
    public static string? TryGetVersion(string executable)
    {
        try
        {
            using Process process = Start(executable, new[] { "--version" }, redirect: true);
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            string? firstLine = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            return firstLine;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// 下载并安装 aria2 便携版到 <see cref="PortableDirectory"/>。
    /// </summary>
    /// <param name="logger">日志。</param>
    /// <param name="connections">用于下载安装包本身的并发连接数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>安装后的 aria2c.exe 绝对路径。</returns>
    public static async Task<string> ProvisionAsync(Logger logger, int connections, CancellationToken cancellationToken)
    {
        string targetDirectory = PortableDirectory();
        string cacheDirectory = Path.Combine(targetDirectory, ".cache");
        Directory.CreateDirectory(cacheDirectory);

        string zipPath = Path.Combine(cacheDirectory, $"{AssetName}.zip");

        logger.Section("安装 aria2 便携版");
        logger.Info($"  来源  : github.com/aria2/aria2 (release-{Version})");
        logger.Info($"  目标  : {targetDirectory}");
        logger.Info($"  校验  : SHA256 {ExpectedSha256[..16]}…");

        DownloadOutcome outcome = await ParallelDownloader
            .DownloadAsync(DownloadUrl, zipPath, Math.Clamp(connections, 1, 16), logger, cancellationToken)
            .ConfigureAwait(false);

        if (!string.Equals(outcome.Sha256, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(zipPath);
            throw new RmbgException(
                ExitCode.EnvironmentError,
                $"""
                 aria2 安装包校验失败，已中止安装。

                 期望 SHA256: {ExpectedSha256}
                 实际 SHA256: {outcome.Sha256}

                 说明：官方包在 1.37.0 版本下应为 {ExpectedPackageBytes} 字节。
                 若你所在网络使用了镜像/代理（例如 ghproxy），请确认其原样转发了文件内容。
                 也可手动下载后把 aria2c.exe 放到：{targetDirectory}\{ExecutableName}
                 """);
        }

        logger.Success("  安装包校验通过");

        // 解压到临时目录后再搬运，避免半途失败留下残缺的安装目录。
        string stagingDirectory = Path.Combine(cacheDirectory, "extract");
        if (Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }

        ZipFile.ExtractToDirectory(zipPath, stagingDirectory, overwriteFiles: true);

        string? extracted = Directory
            .EnumerateFiles(stagingDirectory, ExecutableName, SearchOption.AllDirectories)
            .FirstOrDefault();

        if (extracted is null)
        {
            throw new RmbgException(
                ExitCode.EnvironmentError,
                $"安装包中未找到 {ExecutableName}，压缩包结构可能已变化：{zipPath}");
        }

        Directory.CreateDirectory(targetDirectory);
        string finalPath = Path.Combine(targetDirectory, ExecutableName);
        File.Copy(extracted, finalPath, overwrite: true);

        // 保留许可证，便于合规追溯。
        string? license = Directory
            .EnumerateFiles(stagingDirectory, "COPYING", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (license is not null)
        {
            File.Copy(license, Path.Combine(targetDirectory, "COPYING"), overwrite: true);
        }

        Directory.Delete(stagingDirectory, recursive: true);
        TryDelete(zipPath);

        string? version = TryGetVersion(finalPath);
        if (version is null)
        {
            throw new RmbgException(
                ExitCode.EnvironmentError,
                $"aria2c.exe 已就位但无法执行，可能被杀毒软件拦截：{finalPath}");
        }

        logger.Success($"  已安装: {finalPath}");
        logger.Info($"  版本  : {version}");
        return finalPath;
    }

    /// <summary>
    /// 用 aria2 下载单个文件。
    /// </summary>
    /// <returns>aria2 的退出码（0 表示成功）。</returns>
    public static async Task<int> DownloadAsync(
        string executable,
        string url,
        string directory,
        string fileName,
        int connections,
        Logger logger,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);

        // 交互式终端下不重定向 stdout，让 aria2 渲染它自己的多行进度条（体验优于我们转写）。
        bool passthrough = logger.CanRenderProgress;

        using Process process = Start(executable, BuildArguments(url, directory, fileName, connections), redirect: !passthrough);

        if (!passthrough)
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    logger.Detail("  aria2: " + e.Data);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    logger.Detail("  aria2: " + e.Data);
                }
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        if (!passthrough)
        {
            // 确保异步读取完成，避免退出后仍有未消费的输出。
            process.WaitForExit();
        }

        return process.ExitCode;
    }

    private static IReadOnlyList<string> BuildArguments(string url, string directory, string fileName, int connections)
    {
        int splits = Math.Clamp(connections, 1, 32);

        return new[]
        {
            $"-x{splits}",                      // 每个服务器的最大连接数
            $"-s{splits}",                      // 单个文件的分片数
            "-k1M",                             // 分片粒度 1 MiB
            "--continue=true",                  // 断点续传
            "--max-tries=5",
            "--retry-wait=3",
            "--timeout=30",
            "--connect-timeout=20",
            "--file-allocation=none",           // 避免在慢盘上长时间预分配
            "--allow-overwrite=true",
            "--auto-file-renaming=false",
            "--console-log-level=warn",
            "--summary-interval=5",
            "-d", directory,
            "-o", fileName,
            url,
        };
    }

    private static Process Start(string executable, IReadOnlyList<string> arguments, bool redirect)
    {
        ProcessStartInfo info = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = redirect,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
            WorkingDirectory = Environment.CurrentDirectory,
        };

        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return Process.Start(info)
               ?? throw new RmbgException(ExitCode.EnvironmentError, $"无法启动进程：{executable}");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已退出。
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 权限不足，忽略。
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响主流程。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }

    /// <summary>从程序目录向上回溯找仓库根（含 src 或 scripts 的目录）。</summary>
    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int depth = 0; depth < 9 && directory is not null; depth++)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src"))
                || Directory.Exists(Path.Combine(directory.FullName, "scripts")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }
}
