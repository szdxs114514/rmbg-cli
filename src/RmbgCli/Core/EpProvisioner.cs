using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Windows.AI.MachineLearning;
using RmbgCli.Cli;

namespace RmbgCli.Core;

/// <summary>目录中的一个执行提供程序及其在本机的状态。</summary>
internal sealed record EpCatalogEntry(
    string Name,
    string Vendor,
    string Hardware,
    string Requirement,
    ExecutionProviderReadyState ReadyState,
    ExecutionProviderCertification Certification)
{
    /// <summary>是否已安装在本机（NotPresent 表示未安装）。</summary>
    public bool IsInstalled => ReadyState != ExecutionProviderReadyState.NotPresent;

    /// <summary>是否已就绪并可用于注册（Ready 状态）。</summary>
    public bool IsReady => ReadyState == ExecutionProviderReadyState.Ready;

    public string Label => KnownExecutionProviders.Describe(Name);
}

/// <summary>单个 EP 的就绪/安装结果。</summary>
internal sealed record EpOutcome(string Name, bool Success, string Detail);

/// <summary>
/// Windows ML 执行提供程序的安装、就绪与注册。
///
/// 状态机（来自官方文档）：
///   NotPresent  本机未安装        → EnsureReadyAsync() 下载并安装，并加入应用运行时依赖图
///   NotReady    已安装但未入依赖图 → EnsureReadyAsync() 加入依赖图
///   Ready       已安装且已入依赖图 → TryRegister() 注册到 ONNX Runtime
///
/// 注册完成后，用 <c>OrtEnv.Instance().GetEpDevices()</c> 才能看到对应的 EP 设备。
/// 注意：Device 列表可能在运行时动态变化（EP 自动更新或驱动更新），
/// 因此每次运行都应重新枚举，并对设备消失的情况留好回退路径。
/// </summary>
internal static class EpProvisioner
{
    /// <summary>EP 目录是否可用；部分精简环境可能没有该 WinRT 组件。</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                _ = ExecutionProviderCatalog.GetDefault();
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return false;
            }
        }
    }

    /// <summary>枚举目录中所有 EP（**包含未安装的**）。</summary>
    public static IReadOnlyList<EpCatalogEntry> Enumerate(Logger? logger = null)
    {
        try
        {
            ExecutionProviderCatalog catalog = ExecutionProviderCatalog.GetDefault();
            return catalog.FindAllProviders()
                .Select(ToEntry)
                .OrderByDescending(entry => KnownExecutionProviders.PriorityOf(entry.Name))
                .ThenBy(entry => entry.Name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger?.Detail($"无法访问 Windows ML 执行提供程序目录：{ex.GetType().Name}: {ex.Message}");
            return Array.Empty<EpCatalogEntry>();
        }
    }

    /// <summary>ONNX Runtime 当前实际可用的 EP 设备（注册完成后才会出现 Windows ML EP）。</summary>
    public static IReadOnlyList<OrtEpDevice> GetDevices()
    {
        try
        {
            return OrtEnv.Instance().GetEpDevices();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Array.Empty<OrtEpDevice>();
        }
    }

    /// <summary>ONNX Runtime 中已注册的 EP 提供程序名。</summary>
    public static IReadOnlyList<string> GetAvailableProviderNames()
    {
        try
        {
            return OrtEnv.Instance().GetAvailableProviders();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>本机硬件设备（CPU / GPU / NPU）。</summary>
    public static IReadOnlyList<OrtHardwareDevice> GetHardwareDevices()
    {
        try
        {
            return OrtEnv.Instance().GetHardwareDevices();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Array.Empty<OrtHardwareDevice>();
        }
    }

    /// <summary>
    /// 把本机「已安装」的 EP 加入应用依赖图并注册到 ONNX Runtime。
    /// 不触发任何下载。
    /// </summary>
    public static async Task<IReadOnlyList<EpOutcome>> RegisterInstalledAsync(Logger logger, CancellationToken cancellationToken)
    {
        List<EpOutcome> outcomes = new();

        ExecutionProviderCatalog catalog;
        try
        {
            catalog = ExecutionProviderCatalog.GetDefault();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.Detail($"Windows ML EP 目录不可用：{ex.GetType().Name}: {ex.Message}");
            return outcomes;
        }

        ExecutionProvider[] providers = catalog.FindAllProviders();
        ExecutionProvider[] installed = providers
            .Where(p => p.ReadyState != ExecutionProviderReadyState.NotPresent)
            .ToArray();

        if (installed.Length == 0)
        {
            return outcomes;
        }

        // NotReady 的 EP 需要先通过 EnsureReadyAsync 加入应用依赖图（本地操作，不会下载）。
        foreach (ExecutionProvider provider in installed.Where(p => p.ReadyState == ExecutionProviderReadyState.NotReady))
        {
            cancellationToken.ThrowIfCancellationRequested();

            EpOutcome outcome = await EnsureReadyAsync(provider, logger, cancellationToken).ConfigureAwait(false);
            outcomes.Add(outcome);
        }

        // 一次性把本机所有已安装且已认证的 EP 注册到 ONNX Runtime。
        try
        {
            // 注意：IAsyncOperationWithProgress 是可 await 的 WinRT 异步类型，
            // 但它没有 ConfigureAwait()，只能直接 await。
            await catalog.RegisterCertifiedAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Detail($"RegisterCertifiedAsync 失败：{ex.GetType().Name}: {ex.Message}");
        }

        // 用 ORT 侧的真实结果补充/修正状态。
        IReadOnlyList<string> registered = GetAvailableProviderNames();
        foreach (EpCatalogEntry entry in Enumerate(logger).Where(e => e.IsInstalled))
        {
            bool ortVisible = registered.Any(name =>
                name.Contains(entry.Name.Replace("ExecutionProvider", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase)
                || entry.Name.Contains(name.Replace("ExecutionProvider", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase));

            if (!outcomes.Any(o => o.Name == entry.Name))
            {
                outcomes.Add(new EpOutcome(
                    entry.Name,
                    ortVisible,
                    ortVisible ? "已注册到 ONNX Runtime" : "已安装；ONNX Runtime 未报告该 EP（可能缺少对应驱动）"));
            }
        }

        return outcomes;
    }

    /// <summary>
    /// 安装/就绪指定的 EP，然后注册到 ONNX Runtime。
    /// </summary>
    public static async Task<EpOutcome> EnsureAsync(string epName, Logger logger, CancellationToken cancellationToken)
    {
        ExecutionProvider? provider;
        try
        {
            provider = ExecutionProviderCatalog.GetDefault()
                .FindAllProviders()
                .FirstOrDefault(p => p.Name.Equals(epName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new EpOutcome(epName, false, $"无法访问 EP 目录：{ex.GetType().Name}: {ex.Message}");
        }

        if (provider is null)
        {
            return new EpOutcome(epName, false, "该 EP 未出现在本机目录中（硬件或系统版本不满足要求）");
        }

        return await EnsureReadyAsync(provider, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>安装指定的全部 EP（对应 <c>--install-ep all</c>）。</summary>
    public static async Task<IReadOnlyList<EpOutcome>> InstallAllAsync(Logger logger, CancellationToken cancellationToken)
    {
        List<EpOutcome> outcomes = new();

        ExecutionProviderCatalog catalog;
        try
        {
            catalog = ExecutionProviderCatalog.GetDefault();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new[] { new EpOutcome("(catalog)", false, $"无法访问 EP 目录：{ex.GetType().Name}: {ex.Message}") };
        }

        ExecutionProvider[] providers = catalog.FindAllProviders();
        ExecutionProvider[] pending = providers
            .Where(p => p.ReadyState == ExecutionProviderReadyState.NotPresent)
            .ToArray();

        if (pending.Length == 0)
        {
            logger.Info("本机所有兼容的 EP 均已安装，无需下载。");
        }
        else
        {
            logger.Info($"检测到 {pending.Length} 个可安装的 EP，开始下载安装（首次可能需要数分钟）：");
            foreach (ExecutionProvider provider in pending)
            {
                logger.Info($"  · {KnownExecutionProviders.Describe(provider.Name)}");
            }

            foreach (ExecutionProvider provider in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                outcomes.Add(await EnsureReadyAsync(provider, logger, cancellationToken).ConfigureAwait(false));
            }
        }

        // 一次性注册本机所有已安装且已认证的 EP。
        try
        {
            await catalog.EnsureAndRegisterCertifiedAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Detail($"EnsureAndRegisterCertifiedAsync 失败：{ex.GetType().Name}: {ex.Message}");
        }

        foreach (EpCatalogEntry entry in Enumerate(logger))
        {
            if (entry.IsInstalled && !outcomes.Any(o => o.Name == entry.Name))
            {
                outcomes.Add(new EpOutcome(entry.Name, true, $"已安装，状态：{entry.ReadyState}"));
            }
        }

        return outcomes;
    }

    /// <summary>
    /// 调用 <c>EnsureReadyAsync()</c> 让 EP 就绪（必要时下载安装），随后注册到 ONNX Runtime。
    /// </summary>
    private static async Task<EpOutcome> EnsureReadyAsync(
        ExecutionProvider provider,
        Logger logger,
        CancellationToken cancellationToken)
    {
        string name = provider.Name;

        if (provider.ReadyState == ExecutionProviderReadyState.Ready)
        {
            bool ok = TryRegisterSafe(provider, logger);
            return new EpOutcome(name, ok, ok ? "已就绪并注册到 ONNX Runtime" : "已就绪，但注册失败");
        }

        bool downloading = provider.ReadyState == ExecutionProviderReadyState.NotPresent;
        logger.Info(downloading
            ? $"  正在下载并安装 {KnownExecutionProviders.Describe(name)} …"
            : $"  正在将 {KnownExecutionProviders.Describe(name)} 加入依赖图 …");

        Stopwatch stopwatch = Stopwatch.StartNew();
        ProgressThrottle throttle = new(logger);
        ExecutionProviderReadyResult result;

        try
        {
            var operation = provider.EnsureReadyAsync();

            // 进度回调的 progressInfo 取值区间为 0-100。
            operation.Progress = (_, progress) =>
            {
                throttle.Report(downloading
                    ? $"  {name} 安装中 {progress,5:F1}%  ({stopwatch.Elapsed.TotalSeconds:F0}s)"
                    : $"  {name} 就绪中 {progress,5:F1}%");
            };

            // WinRT 异步类型可 await，但不支持 ConfigureAwait()。
            result = await operation;
        }
        catch (OperationCanceledException)
        {
            throttle.Complete();
            throw;
        }
        catch (Exception ex)
        {
            throttle.Complete();
            return new EpOutcome(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }

        throttle.Complete();

        if (result.Status != ExecutionProviderReadyResultState.Success)
        {
            string detail = result.Status == ExecutionProviderReadyResultState.InProgress
                ? "操作仍在进行中（未完成）"
                : "安装失败";

            if (result.ExtendedError is { } error)
            {
                detail += $"：{Normalize(error.Message)}";
            }

            if (!string.IsNullOrWhiteSpace(result.DiagnosticText))
            {
                detail += $" | 诊断：{Normalize(result.DiagnosticText)}";
            }

            // Windows 返回的原文（如"产品不适用或找不到"）对用户没有指导意义，
            // 补上该 EP 的硬件/驱动要求，便于自行核对。此处刻意不匹配本地化文案。
            KnownExecutionProvider? known = KnownExecutionProviders.Find(name);
            if (known is not null)
            {
                detail += Environment.NewLine +
                          $"     该 EP 的硬件要求：{known.Requirement}" +
                          Environment.NewLine +
                          "     安装失败通常意味着本机硬件或驱动不满足上述要求，" +
                          "可改用 --ep dml 或 --ep cpu。";
            }

            return new EpOutcome(name, false, detail);
        }

        bool registered = TryRegisterSafe(provider, logger);
        string summary = downloading
            ? $"安装完成（{stopwatch.Elapsed.TotalSeconds:F0}s）"
            : "已就绪";

        return new EpOutcome(
            name,
            registered,
            registered ? summary + "，并已注册到 ONNX Runtime" : summary + "，但注册失败");
    }

    private static bool TryRegisterSafe(ExecutionProvider provider, Logger logger)
    {
        try
        {
            bool ok = provider.TryRegister();
            if (!ok)
            {
                logger.Detail($"    {provider.Name}: TryRegister() 返回 false");
            }

            return ok;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.Detail($"    {provider.Name}: TryRegister() 抛出 {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static EpCatalogEntry ToEntry(ExecutionProvider provider)
    {
        KnownExecutionProvider? known = KnownExecutionProviders.Find(provider.Name);

        return new EpCatalogEntry(
            provider.Name,
            known?.Vendor ?? "（未知供应商）",
            known?.Hardware ?? "—",
            known?.Requirement ?? "—",
            provider.ReadyState,
            provider.Certification);
    }

    /// <summary>
    /// 把 WinRT 异常信息压成单行。ExtendedError.Message 常把同一句 HRESULT 描述拼接两次
    /// （外层描述 + 内层描述），这里顺带做一次去重。
    /// </summary>
    private static string Normalize(string text)
    {
        string flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

        // 仅处理"两句完全相同"这一种确定形态，不做模糊匹配。
        string[] sentences = flat.Split('。', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (sentences.Length == 2 && sentences[0] == sentences[1])
        {
            return sentences[0] + "。";
        }

        return flat;
    }

    /// <summary>进度回调节流：避免高频刷新拖慢安装。</summary>
    private sealed class ProgressThrottle
    {
        private readonly Logger _logger;
        private long _lastTicks;

        public ProgressThrottle(Logger logger)
        {
            _logger = logger;
            _lastTicks = Stopwatch.GetTimestamp();
        }

        public void Report(string text)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - Interlocked.Read(ref _lastTicks) < Stopwatch.Frequency / 5)
            {
                return;
            }

            Interlocked.Exchange(ref _lastTicks, now);
            _logger.Progress(text);
        }

        public void Complete()
        {
            _lastTicks = long.MaxValue;
            _logger.ProgressComplete();
        }
    }
}
