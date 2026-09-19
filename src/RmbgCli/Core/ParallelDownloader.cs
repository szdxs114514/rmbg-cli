using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using RmbgCli.Cli;

namespace RmbgCli.Core;

/// <summary>下载结果。</summary>
internal sealed record DownloadOutcome(string FilePath, long Bytes, TimeSpan Elapsed, string Sha256);

/// <summary>
/// 内置多连接 HTTP 下载器。
///
/// 存在的意义：aria2c 未安装时仍要能加速下载（尤其是 500 MB ~ 1 GB 的模型权重），
/// 因此不依赖任何外部可执行文件。
///
/// 实现要点：
///   - 先用 <c>Range: bytes=0-0</c> 探测总长度与是否支持分片（比 HEAD 更可靠，CDN 常对 HEAD 返回 200）；
///   - 支持分片时把文件切成 N 段并行下载，用 <see cref="RandomAccess"/> 按偏移写入，
///     避免多线程共享流指针的竞态；
///   - 单段失败按指数退避重试，并从该段已写入的位置续传；
///   - 不支持分片时退化为单流下载，且支持基于已有 .partial 文件续传。
/// </summary>
internal static class ParallelDownloader
{
    private const int BufferSize = 1 << 17;          // 128 KB
    private const int MaxSegmentAttempts = 4;
    private const long MinSizeForSegmentation = 4L * 1024 * 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private static readonly HttpClient Client = CreateClient();

    /// <summary>下载 <paramref name="url"/> 到 <paramref name="destinationPath"/>。</summary>
    public static async Task<DownloadOutcome> DownloadAsync(
        string url,
        string destinationPath,
        int connections,
        Logger logger,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(connections, 1);

        string fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        string partialPath = fullPath + ".partial";
        Stopwatch stopwatch = Stopwatch.StartNew();

        (long? totalLength, bool supportsRanges) = await ProbeAsync(url, cancellationToken).ConfigureAwait(false);

        long bytes;
        if (supportsRanges && totalLength is > MinSizeForSegmentation && connections > 1)
        {
            logger.Info($"  模式: 分片下载（{connections} 连接）");
            bytes = await DownloadSegmentedAsync(url, partialPath, totalLength.Value, connections, logger, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            logger.Info(supportsRanges
                ? "  模式: 单流下载（文件较小或连接数设为 1）"
                : "  模式: 单流下载（服务器不支持分片）");

            bytes = await DownloadSingleStreamAsync(url, partialPath, totalLength, logger, cancellationToken)
                .ConfigureAwait(false);
        }

        stopwatch.Stop();

        if (totalLength is > 0 && bytes != totalLength.Value)
        {
            throw new RmbgException(
                ExitCode.IoError,
                $"下载不完整：期望 {totalLength.Value} 字节，实际 {bytes} 字节。可重新执行以续传。");
        }

        logger.ProgressComplete();
        File.Move(partialPath, fullPath, overwrite: true);

        string sha256 = await ComputeSha256Async(fullPath, cancellationToken).ConfigureAwait(false);

        return new DownloadOutcome(fullPath, bytes, stopwatch.Elapsed, sha256);
    }

    /// <summary>探测总长度与分片支持情况。</summary>
    private static async Task<(long? TotalLength, bool SupportsRanges)> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        using CancellationTokenSource probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probe.CancelAfter(ProbeTimeout);

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

        try
        {
            using HttpResponseMessage response = await Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, probe.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.Length is { } length)
            {
                return (length, true);
            }

            if (response.IsSuccessStatusCode)
            {
                // 服务器忽略了 Range，返回整份内容 —— 不支持分片。
                return (response.Content.Headers.ContentLength, false);
            }

            throw new RmbgException(
                ExitCode.IoError,
                $"下载探测失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}（{url}）。" +
                (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                    ? Environment.NewLine + "服务端拒绝了请求。请检查：① 是否处于需要认证的代理环境；" +
                      "② 是否被内容分发网络的访问控制拦截。可改用 aria2 后端（去掉 --no-aria2）重试。"
                    : string.Empty));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RmbgException(ExitCode.IoError, $"下载探测超时（{url}）。请检查网络或代理设置。");
        }
        catch (HttpRequestException ex)
        {
            throw new RmbgException(ExitCode.IoError, $"无法连接下载地址（{url}）：{ex.Message}", ex);
        }
    }

    /// <summary>多段并行下载。</summary>
    private static async Task<long> DownloadSegmentedAsync(
        string url,
        string partialPath,
        long totalLength,
        int connections,
        Logger logger,
        CancellationToken cancellationToken)
    {
        // 分片模式不支持跨进程续传（段边界状态未持久化），统一从头开始，避免拼出损坏文件。
        if (File.Exists(partialPath))
        {
            File.Delete(partialPath);
            logger.Detail("  已丢弃旧的 .partial 文件（分片模式不支持续传）");
        }

        await using (FileStream preallocate = new(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096))
        {
            preallocate.SetLength(totalLength);
        }

        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(
            partialPath, FileMode.Open, FileAccess.Write, FileShare.Read);

        long downloaded = 0;
        ProgressReporter reporter = new(logger, totalLength, () => Interlocked.Read(ref downloaded));

        long segmentSize = totalLength / connections;
        Task[] tasks = new Task[connections];

        for (int index = 0; index < connections; index++)
        {
            long start = index * segmentSize;
            long end = index == connections - 1 ? totalLength - 1 : (start + segmentSize) - 1;

            tasks[index] = DownloadSegmentAsync(
                url,
                handle,
                start,
                end,
                bytesWritten => Interlocked.Add(ref downloaded, bytesWritten),
                reporter,
                cancellationToken);
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            reporter.Stop();
        }

        return totalLength;
    }

    /// <summary>下载单个分段，失败按退避重试并续传。</summary>
    private static async Task DownloadSegmentAsync(
        string url,
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        long start,
        long end,
        Action<long> onProgress,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        long position = start;

        for (int attempt = 1; attempt <= MaxSegmentAttempts; attempt++)
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(position, end);
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

                using HttpResponseMessage response = await Client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (response.StatusCode != HttpStatusCode.PartialContent)
                {
                    throw new RmbgException(
                        ExitCode.IoError,
                        $"服务器未按分片响应（HTTP {(int)response.StatusCode}），请改用 --connections 1 重试。");
                }

                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

                byte[] buffer = new byte[BufferSize];
                int read;

                while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), position, cancellationToken)
                        .ConfigureAwait(false);

                    position += read;
                    onProgress(read);
                    reporter.ReportThrottled();
                }

                if (position > end)
                {
                    return;
                }

                throw new IOException($"分段 {start}-{end} 提前结束（已到 {position}）。");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxSegmentAttempts)
            {
                reporter.SetStatus($"分段 {start / 1024 / 1024} MB 处重试 {attempt}/{MaxSegmentAttempts - 1}（{Trim(ex.Message)}）");
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new RmbgException(
                    ExitCode.IoError,
                    $"分段 {start}-{end} 下载失败（已重试 {MaxSegmentAttempts} 次）：{ex.Message}",
                    ex);
            }
        }
    }

    /// <summary>单流下载（支持基于 .partial 的续传）。</summary>
    private static async Task<long> DownloadSingleStreamAsync(
        string url,
        string partialPath,
        long? totalLength,
        Logger logger,
        CancellationToken cancellationToken)
    {
        long existing = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;

        if (existing > 0 && totalLength is > 0 && existing >= totalLength.Value)
        {
            // 上次已下完但未改名，直接复用。
            logger.Detail("  检测到完整的 .partial 文件，跳过下载");
            return existing;
        }

        if (existing > 0)
        {
            logger.Info($"  续传: 从 {existing / 1024d / 1024d:F1} MB 继续");
        }

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (existing > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        }

        using HttpResponseMessage response = await Client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // 续传请求若被降级为 200，说明服务器忽略了 Range，必须从头写，否则会拼出脏数据。
        bool append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append)
        {
            existing = 0;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new RmbgException(
                ExitCode.IoError,
                $"下载失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}（{url}）");
        }

        long total = totalLength ?? response.Content.Headers.ContentLength ?? 0;
        long received = existing;
        ProgressReporter reporter = new(logger, total, () => received);

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream destination = new(
            partialPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            BufferSize,
            useAsync: true);

        byte[] buffer = new byte[BufferSize];
        int read;

        try
        {
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                reporter.ReportThrottled();
            }
        }
        finally
        {
            reporter.Stop();
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        return destination.Length;
    }

    /// <summary>计算文件的 SHA256（大写十六进制）。</summary>
    internal static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static HttpClient CreateClient()
    {
        SocketsHttpHandler handler = new()
        {
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            MaxConnectionsPerServer = 64,
        };

        HttpClient client = new(handler)
        {
            // 大文件下载时长不可预估，超时交由 CancellationToken 控制。
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // 必须显式设置 User-Agent：.NET 的 HttpClient 默认不发送该头部，而 ModelScope 的
        // CDN（cdn-lfs-*.modelscope.cn）会对缺少 User-Agent 的请求直接返回 403 Forbidden，
        // 且该 403 发生在跟随 302 之后的 CDN 侧，表现为"地址能访问但下载器报 403"。
        // curl / aria2 / Python 之所以正常，是因为它们都会自动附带 User-Agent。
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");

        return client;
    }

    /// <summary>用于所有下载请求的 User-Agent（同时便于服务端侧识别来源）。</summary>
    private static string UserAgent
    {
        get
        {
            string version = typeof(ParallelDownloader).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
            return $"rmbg/{version} (Windows; ONNX Runtime background remover)";
        }
    }

    private static string Trim(string message)
        => message.Length <= 60 ? message : message[..60] + "…";

    /// <summary>节流的进度渲染器：多线程汇总已下载字节，单行原地刷新。</summary>
    private sealed class ProgressReporter
    {
        private readonly Logger _logger;
        private readonly long _total;
        private readonly Func<long> _downloaded;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly object _gate = new();

        private long _lastRenderTicks;
        private string? _status;

        public ProgressReporter(Logger logger, long total, Func<long> downloaded)
        {
            _logger = logger;
            _total = total;
            _downloaded = downloaded;
            _lastRenderTicks = Stopwatch.GetTimestamp();
        }

        public void SetStatus(string status)
        {
            lock (_gate)
            {
                _status = status;
            }
        }

        /// <summary>多线程调用；内部按时间节流，只有真正渲染时才加锁。</summary>
        public void ReportThrottled()
        {
            long now = Stopwatch.GetTimestamp();
            long last = Interlocked.Read(ref _lastRenderTicks);

            // 约 150 ms 刷新一次，避免高频 Write 拖慢下载。
            if (now - last < Stopwatch.Frequency / 7)
            {
                return;
            }

            Interlocked.Exchange(ref _lastRenderTicks, now);
            Render();
        }

        public void Stop() => _lastRenderTicks = long.MaxValue;

        private void Render()
        {
            if (!_logger.CanRenderProgress)
            {
                return;
            }

            long done = _downloaded();
            double elapsed = Math.Max(_stopwatch.Elapsed.TotalSeconds, 0.001);
            double megabytesPerSecond = done / 1024d / 1024d / elapsed;

            string line;
            lock (_gate)
            {
                if (_status is not null)
                {
                    line = $"  {_status}";
                    _status = null;
                }
                else if (_total > 0)
                {
                    double percent = Math.Min(1d, (double)done / _total);
                    double remainingMb = (_total - done) / 1024d / 1024d;
                    string eta = megabytesPerSecond > 0.01
                        ? FormatDuration(TimeSpan.FromSeconds(remainingMb / megabytesPerSecond))
                        : "--:--";

                    line = $"  下载中 {percent,6:P1}  {done / 1048576d,7:F1}/{_total / 1048576d:F1} MB  " +
                           $"{megabytesPerSecond,6:F1} MB/s  剩余 {eta}";
                }
                else
                {
                    line = $"  下载中 {done / 1048576d:F1} MB  {megabytesPerSecond:F1} MB/s";
                }
            }

            _logger.Progress(line);
        }
    }

    internal static string FormatDuration(TimeSpan value)
        => value.TotalHours >= 1
            ? $"{(int)value.TotalHours}h{value.Minutes:00}m"
            : $"{(int)value.TotalMinutes:00}:{value.Seconds:00}";
}
