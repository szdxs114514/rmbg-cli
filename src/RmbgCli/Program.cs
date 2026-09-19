using System.Diagnostics;
using System.Text;
using RmbgCli.Cli;
using RmbgCli.Core;
using RmbgCli.Imaging;

namespace RmbgCli;

/// <summary>程序入口。</summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        TryEnableUtf8Output();

        // 完全不带参数且处于交互式终端时，直接进入引导式操作 ——
        // 避免用户面对一屏参数说明却不知道该从哪开始。
        if (args.Length == 0 && IsInteractiveTerminal())
        {
            args = new[] { "--guide" };
        }

        if (!CommandLineParser.TryParse(args, out AppOptions options, out string? parseError))
        {
            Console.Error.WriteLine($"参数错误：{parseError}");
            return (int)ExitCode.UsageError;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(HelpText.Full);
            return (int)ExitCode.Success;
        }

        if (options.ShowVersion)
        {
            Console.WriteLine(HelpText.VersionLine);
            return (int)ExitCode.Success;
        }

        Logger logger = new(options.Quiet, options.Verbose);

        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
        {
            // 首次 Ctrl+C 请求优雅退出，避免留下写了一半的输出文件。
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += onCancel;

        try
        {
            if (options.Ep == EpMode.List)
            {
                ExecutionProviders.PrintAvailability(logger);
                return (int)ExitCode.Success;
            }

            if (options.Guide)
            {
                InteractiveWizard wizard = new(logger, cancellation.Token);
                return await wizard.RunAsync().ConfigureAwait(false);
            }

            if (options.DownloadModel)
            {
                return await DownloadModelAsync(options, logger, cancellation.Token).ConfigureAwait(false);
            }

            if (options.InstallEp)
            {
                return await InstallExecutionProvidersAsync(options, logger, cancellation.Token).ConfigureAwait(false);
            }

            if (options.InstallAria2 && options.Inputs.Count == 0 && string.IsNullOrWhiteSpace(options.Output))
            {
                string installed = await Aria2Backend
                    .ProvisionAsync(logger, options.Connections, cancellation.Token)
                    .ConfigureAwait(false);

                logger.Success($"aria2 已就绪：{installed}");
                return (int)ExitCode.Success;
            }

            return await ExecuteAsync(options, logger, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.Warn("操作已被取消。");
            return (int)ExitCode.Cancelled;
        }
        catch (RmbgException ex)
        {
            logger.Error(ex.Message);
            return (int)ex.ExitCode;
        }
        catch (Exception ex)
        {
            logger.Error("发生未预期的错误：" + ex);
            return (int)ExitCode.InferenceError;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>下载模型（非交互路径，供 <c>--download-model</c> 使用）。</summary>
    private static async Task<int> DownloadModelAsync(AppOptions options, Logger logger, CancellationToken cancellationToken)
    {
        ModelVariant variant = ModelCatalog.Find(options.ModelVariant) ?? ModelCatalog.Default;

        ModelDownloadRequest request = new(
            Variant: variant,
            Directory: options.DownloadDirectory ?? ModelCatalog.DefaultDirectory(),
            Connections: options.Connections,
            PreferAria2: options.PreferAria2,
            AllowInstallAria2: options.InstallAria2,
            Overwrite: options.Overwrite);

        await ModelDownloader.DownloadAsync(request, logger, cancellationToken).ConfigureAwait(false);
        return (int)ExitCode.Success;
    }

    /// <summary>
    /// 安装本机可下载的 Windows ML 执行提供程序（<c>--install-ep</c>）。
    /// </summary>
    private static async Task<int> InstallExecutionProvidersAsync(
        AppOptions options,
        Logger logger,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<KnownExecutionProvider> targets =
            KnownExecutionProviders.Resolve(options.EpSpec) ?? KnownExecutionProviders.Downloadable;

        logger.Section("安装 Windows ML 执行提供程序");

        if (targets.Count == KnownExecutionProviders.Downloadable.Count)
        {
            logger.Info("  目标  : 本机所有兼容的 EP");
        }
        else
        {
            logger.Info($"  目标  : {string.Join("、", targets.Select(t => t.Label))}");
        }

        logger.Info($"  平台  : Windows 11 24H2（build 26100）及以上才支持动态下载 EP");
        logger.Info("  说明  : 首次安装可能需要数分钟，安装包由 Windows 从 Windows Update 分发");
        logger.Info(string.Empty);

        IReadOnlyList<EpOutcome> outcomes = await ExecutionProviders
            .InstallAsync(targets, logger, cancellationToken)
            .ConfigureAwait(false);

        int succeeded = outcomes.Count(o => o.Success);
        int failed = outcomes.Count - succeeded;

        logger.Info(string.Empty);
        logger.Info($"  安装完成：成功 {succeeded}，失败 {failed}");

        if (failed > 0)
        {
            logger.Info("  失败项的常见原因：硬件不满足 EP 要求、驱动版本过低、或系统低于 24H2。");
            logger.Info("  可执行 `rmbg --ep list` 核对本机硬件与 EP 要求。");
        }

        // 安装完成后打印一次最终环境，便于确认是否真的可用。
        logger.Info(string.Empty);
        ExecutionProviders.PrintAvailability(logger);

        return failed == 0 ? (int)ExitCode.Success : (int)ExitCode.EnvironmentError;
    }

    /// <summary>输出是否为可交互终端（用于决定是否自动进入引导式操作）。</summary>
    private static bool IsInteractiveTerminal()
    {
        try
        {
            return !Console.IsInputRedirected && !Console.IsOutputRedirected;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// 执行抠图流程。同时被命令行路径与引导式向导复用。
    /// </summary>
    internal static async Task<int> ExecuteAsync(AppOptions options, Logger logger, CancellationToken cancellationToken)
    {
        // ---- 1. 先展开任务：输入/输出路径的校验要在加载模型之前完成，
        //         否则一个路径拼错的调用也要白等一次模型加载与 EP 初始化。 ----
        BatchPlan plan = BatchPlanner.Build(options, logger);

        foreach (string problem in plan.Problems)
        {
            logger.Warn(problem);
        }

        foreach (SkippedItem skipped in plan.Skipped)
        {
            logger.Info($"跳过 {Path.GetFileName(skipped.InputPath)}：{skipped.Reason}");
        }

        if (plan.Jobs.Count == 0)
        {
            if (plan.Problems.Count > 0)
            {
                logger.Error("没有可处理的图片。");
                return (int)ExitCode.IoError;
            }

            if (plan.Skipped.Count > 0)
            {
                // 全部因"输出已存在"被跳过属于正常结果（重复运行是幂等的），不应报错。
                logger.Info($"所有 {plan.Skipped.Count} 个任务均已跳过（输出文件已存在）。如需覆盖请加 --overwrite。");
                return (int)ExitCode.Success;
            }

            logger.Error("没有可处理的图片。");
            return (int)ExitCode.Success;
        }

        // ---- 2. 定位模型 ----
        string modelPath = ModelLocator.Resolve(options.ModelPath, logger);
        FileInfo modelInfo = new(modelPath);

        // ---- 3. 选择执行提供程序并创建会话 ----
        IReadOnlyList<ExecutionProviderChoice> candidates = await ExecutionProviders
            .BuildCandidatesAsync(options.Ep, options.DeviceId, options.AllowEpInstall, logger, cancellationToken)
            .ConfigureAwait(false);

        using RmbgSession session = RmbgSession.Create(modelPath, candidates, options.Threads, logger, cancellationToken);

        int inputSize = ResolveInputSize(session, options, logger);

        logger.Section("RMBG-2.0 背景移除");
        logger.Info($"  模型     : {modelInfo.Name}（{modelInfo.Length / 1024d / 1024d:F1} MB）");
        logger.Info($"  提供程序 : {session.Provider.DisplayName}（{session.Provider.Detail}）");
        logger.Detail($"  输入张量 : {session.Model.Input.Name} {session.Model.Input.ShapeText} {session.Model.Input.ElementTypeText}");
        logger.Detail($"  输出张量 : {session.Model.Output.Name} {session.Model.Output.ShapeText} {session.Model.Output.ElementTypeText}");
        logger.Info($"  推理尺寸 : {inputSize}x{inputSize}");
        logger.Info($"  待处理   : {plan.Jobs.Count} 张");
        logger.Info(string.Empty);

        // ---- 4. 逐张处理 ----
        BackgroundRemover remover = new(session, options, logger, inputSize);
        List<(string Input, string Reason)> failures = new();
        List<double> inferenceMilliseconds = new();
        int succeeded = 0;

        Stopwatch totalStopwatch = Stopwatch.StartNew();

        for (int index = 0; index < plan.Jobs.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ProcessingJob job = plan.Jobs[index];
            logger.Info($"[{index + 1}/{plan.Jobs.Count}] {Path.GetFileName(job.InputPath)}");

            try
            {
                ProcessingResult result = await remover.ProcessAsync(job, cancellationToken).ConfigureAwait(false);

                succeeded++;
                inferenceMilliseconds.Add(result.InferenceTime.TotalMilliseconds);

                logger.Success(
                    $"    完成 → {result.OutputPath}" +
                    $"（{result.Width}x{result.Height}，" +
                    $"预处理 {result.PreprocessTime.TotalMilliseconds:F0}ms / " +
                    $"推理 {result.InferenceTime.TotalMilliseconds:F0}ms / " +
                    $"后处理 {result.PostprocessTime.TotalMilliseconds:F0}ms）");

                logger.Detail($"    蒙版统计: {result.Statistics}");

                if (result.MaskPath is not null)
                {
                    logger.Info($"    蒙版 → {result.MaskPath}");
                }

                WarnIfSuspicious(result, logger);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (RmbgException ex)
            {
                failures.Add((job.InputPath, ex.Message));
                logger.Error($"    失败：{ex.Message}");
            }
            catch (Exception ex)
            {
                failures.Add((job.InputPath, $"{ex.GetType().Name}: {ex.Message}"));
                logger.Error($"    失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        totalStopwatch.Stop();

        // ---- 5. 汇总 ----
        logger.Info(string.Empty);
        logger.Section("处理汇总");

        string averageInference = inferenceMilliseconds.Count > 0
            ? $"{(inferenceMilliseconds.Sum() / inferenceMilliseconds.Count) / 1000d:F2}s"
            : "n/a";

        logger.Info(
            $"  成功 {succeeded} 张，失败 {failures.Count} 张，跳过 {plan.Skipped.Count} 张；" +
            $"总耗时 {totalStopwatch.Elapsed.TotalSeconds:F1}s（平均单张推理 {averageInference}）");

        foreach ((string input, string reason) in failures)
        {
            logger.Error($"  ✗ {input}");
            logger.Error($"      {reason.Replace(Environment.NewLine, Environment.NewLine + "      ", StringComparison.Ordinal)}");
        }

        if (failures.Count == 0)
        {
            return (int)ExitCode.Success;
        }

        return (int)ExitCode.PartialFailure;
    }

    /// <summary>模型输入尺寸固定时，以模型为准并提示用户。</summary>
    private static int ResolveInputSize(RmbgSession session, AppOptions options, Logger logger)
    {
        int? fixedSize = session.Model.FixedInputSize;

        if (fixedSize is > 0)
        {
            if (fixedSize.Value != options.Size)
            {
                logger.Warn($"该模型的输入尺寸固定为 {fixedSize.Value}，已忽略 --size {options.Size}。");
            }

            return fixedSize.Value;
        }

        logger.Detail("模型输入尺寸为动态维度，使用 --size 指定值。");
        return options.Size;
    }

    /// <summary>对明显异常的蒙版给出提示，避免用户拿到"全黑/全白"结果却不知所以。</summary>
    private static void WarnIfSuspicious(ProcessingResult result, Logger logger)
    {
        MaskStatistics statistics = result.Statistics;

        if (statistics.Max < 0.5f)
        {
            logger.Warn("    蒙版置信度极低（最大前景概率 < 0.5），结果可能不可用：请确认图片内容与模型预期匹配。");
        }
        else if (statistics.CoverageRatio > 0.995d)
        {
            logger.Warn("    蒙版几乎全为前景（覆盖率 > 99.5%），可能未检测到主体；可尝试 --invert 或调整输入。");
        }
        else if (statistics.CoverageRatio < 0.0005d)
        {
            logger.Warn("    蒙版几乎全为背景（覆盖率 < 0.05%），可能未检测到主体；可尝试 --invert。");
        }
    }

    private static void TryEnableUtf8Output()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // 输出被重定向时可能失败，不影响主流程。
        }
        catch (PlatformNotSupportedException)
        {
            // 同上。
        }
    }
}
