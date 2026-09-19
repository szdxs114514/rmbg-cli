using System.Diagnostics;
using RmbgCli.Cli;

namespace RmbgCli.Core;

/// <summary>模型下载参数。</summary>
internal sealed record ModelDownloadRequest(
    ModelVariant Variant,
    string Directory,
    int Connections,
    bool PreferAria2,
    bool AllowInstallAria2,
    bool Overwrite);

/// <summary>实际使用的下载后端。</summary>
internal enum DownloadBackend
{
    Aria2,
    BuiltIn,
}

/// <summary>
/// RMBG-2.0 权重下载编排。
///
/// 后端选择顺序：
///   1. aria2c（本仓库 tools\aria2\aria2c.exe → PATH）—— 多连接 + 断点续传，速度最好；
///   2. 内置多连接下载器 —— 零外部依赖，保证任何机器都能下载；
/// 若允许安装且 aria2 缺失，会先提示再经用户确认后安装便携版。
/// </summary>
internal static class ModelDownloader
{
    /// <summary>下载并校验模型，返回最终文件路径。</summary>
    public static async Task<string> DownloadAsync(
        ModelDownloadRequest request,
        Logger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string directory = Path.GetFullPath(request.Directory);
        Directory.CreateDirectory(directory);

        string targetPath = Path.Combine(directory, request.Variant.FileName);
        string url = ModelCatalog.GetUrl(request.Variant);

        if (File.Exists(targetPath) && !request.Overwrite)
        {
            long size = new FileInfo(targetPath).Length;
            if (size > 1L * 1024 * 1024)
            {
                logger.Info($"模型已存在，跳过下载（加 --overwrite 可强制重新下载）：{targetPath}");
                return targetPath;
            }

            logger.Warn($"已存在的文件体积异常（{size} 字节），将重新下载：{targetPath}");
        }

        logger.Section("下载 RMBG-2.0 模型");
        logger.Info($"  变体  : {request.Variant.Key}（{request.Variant.Note}）");
        logger.Info($"  文件  : {request.Variant.FileName}");
        logger.Info($"  体积  : 约 {request.Variant.ApproximateMegabytes:F0} MB");
        logger.Info($"  来源  : {ModelCatalog.RepositoryUri}");
        logger.Info($"  目标  : {targetPath}");
        logger.Info(string.Empty);

        DownloadBackend backend = await ResolveBackendAsync(request, logger, cancellationToken).ConfigureAwait(false);
        Stopwatch stopwatch = Stopwatch.StartNew();

        if (backend == DownloadBackend.Aria2)
        {
            string executable = Aria2Backend.Locate()
                ?? throw new RmbgException(ExitCode.EnvironmentError, "aria2c 在下载前丢失，请重试。");

            logger.Info($"  后端  : aria2c（{Aria2Backend.TryGetVersion(executable) ?? "版本未知"}）");
            logger.Info(string.Empty);

            int exitCode = await Aria2Backend
                .DownloadAsync(executable, url, directory, request.Variant.FileName, request.Connections, logger, cancellationToken)
                .ConfigureAwait(false);

            stopwatch.Stop();

            // aria2 的退出码语义较细（1 未知错误、3 资源未找到、7 下载未完成等）；
            // 无论何种退出码，最终以文件本身是否完整为准。
            if (!File.Exists(targetPath))
            {
                throw new RmbgException(
                    ExitCode.IoError,
                    $"aria2 退出码 {exitCode}，且目标文件不存在：{targetPath}");
            }

            if (exitCode != 0)
            {
                logger.Warn($"  aria2 退出码为 {exitCode}，正在校验已取得的文件…");
            }
        }
        else
        {
            logger.Info("  后端  : 内置多连接下载器（aria2c 不可用）");
            logger.Info(string.Empty);

            try
            {
                DownloadOutcome outcome = await ParallelDownloader
                    .DownloadAsync(url, targetPath, request.Connections, logger, cancellationToken)
                    .ConfigureAwait(false);

                stopwatch.Stop();
                logger.Info($"  校验  : SHA256 {outcome.Sha256[..16]}…");
            }
            catch
            {
                stopwatch.Stop();
                throw;
            }
        }

        Validate(targetPath, request.Variant, logger, stopwatch.Elapsed);

        logger.Success($"下载完成：{targetPath}");
        return targetPath;
    }

    /// <summary>决定使用哪个后端；必要时（经用户许可）安装 aria2 便携版。</summary>
    private static async Task<DownloadBackend> ResolveBackendAsync(
        ModelDownloadRequest request,
        Logger logger,
        CancellationToken cancellationToken)
    {
        if (!request.PreferAria2)
        {
            logger.Detail("已按 --no-aria2 指定使用内置下载器。");
            return DownloadBackend.BuiltIn;
        }

        if (Aria2Backend.Locate() is not null)
        {
            return DownloadBackend.Aria2;
        }

        if (!request.AllowInstallAria2)
        {
            logger.Warn("未找到 aria2c，将使用内置下载器。加 --install-aria2 可自动安装便携版 aria2 以获得更好速度。");
            return DownloadBackend.BuiltIn;
        }

        logger.Info("未找到 aria2c，准备安装官方便携版（约 2.4 MB）。");

        try
        {
            await Aria2Backend.ProvisionAsync(logger, request.Connections, cancellationToken).ConfigureAwait(false);
            return DownloadBackend.Aria2;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RmbgException ex)
        {
            logger.Warn($"aria2 安装失败，改用内置下载器：{ex.Message.Split('\n')[0]}");
            return DownloadBackend.BuiltIn;
        }
    }

    /// <summary>下载后校验：体积、ONNX 头字节，并对体积偏差给出提示。</summary>
    private static void Validate(string path, ModelVariant variant, Logger logger, TimeSpan elapsed)
    {
        FileInfo info = new(path);
        double megabytes = info.Length / 1024d / 1024d;

        double speed = elapsed.TotalSeconds > 0.01 ? megabytes / elapsed.TotalSeconds : 0;

        if (info.Length < 1L * 1024 * 1024)
        {
            throw new RmbgException(
                ExitCode.IoError,
                $"下载得到的文件体积异常（{info.Length} 字节），可能拿到的是错误页面或 LFS 指针：{path}");
        }

        // ONNX 是 protobuf，首字节应为 0x08（ir_version 字段）。
        int firstByte;
        using (FileStream stream = File.OpenRead(path))
        {
            firstByte = stream.ReadByte();
        }

        if (firstByte != 0x08)
        {
            logger.Warn($"  文件首字节为 0x{firstByte:X2}，不是标准 ONNX 头，可能已损坏或被内容分发网络改写。");
        }

        double deviation = Math.Abs(info.Length - variant.ApproximateBytes) / (double)variant.ApproximateBytes;
        logger.Info($"  实际体积: {megabytes:F1} MB，耗时 {ParallelDownloader.FormatDuration(elapsed)}，平均 {speed:F1} MB/s");

        if (deviation > 0.02d)
        {
            logger.Warn(
                $"  体积与预期（{variant.ApproximateMegabytes:F0} MB）相差 {deviation:P1}。" +
                "若推理阶段报张量规格不符，请重新下载。");
        }
    }
}
