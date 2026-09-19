using Microsoft.ML.OnnxRuntime;
using Microsoft.Windows.AI.MachineLearning;
using RmbgCli.Cli;

namespace RmbgCli.Core;

/// <summary>
/// 一个候选执行提供程序（EP）。配置动作可能抛异常，由会话创建流程捕获后自动降级到下一个候选。
/// </summary>
/// <param name="Id">机器可读标识，用于日志与统计。</param>
/// <param name="DisplayName">面向用户的名称。</param>
/// <param name="Detail">补充说明（硬件类型、设备号、来源包等）。</param>
/// <param name="Configure">把该 EP 追加到会话选项上的动作。</param>
internal sealed record ExecutionProviderChoice(
    string Id,
    string DisplayName,
    string Detail,
    Action<SessionOptions> Configure);

/// <summary>
/// 执行提供程序的发现、安装引导与候选构建。
///
/// 完整流程遵循官方文档（install → register → select）：
///   1. 目录枚举 <c>FindAllProviders()</c>：**包含未安装的 EP**，状态见 ReadyState；
///   2. 已安装的 EP 通过 <c>RegisterCertifiedAsync()</c> / <c>TryRegister()</c> 注册到 ONNX Runtime；
///   3. 未安装的 EP（NotPresent）必须由用户显式同意后调用 <c>EnsureReadyAsync()</c> 下载安装，
///      本工具不会静默下载数百 MB 的驱动包；
///   4. 注册之后用 <c>OrtEnv.GetEpDevices()</c> 枚举真实可用的 EP 设备，
///      按 <c>EpName + HardwareDevice.Type</c> 精确选择，并用设备级重载追加。
///
/// 候选列表整体仍是"逐个尝试创建会话，第一个成功者生效"，
/// 因为设备列表可能在运行时动态变化（EP 自动更新或驱动更新）。
/// </summary>
internal static class ExecutionProviders
{
    /// <summary>按模式构造候选 EP 列表（按优先级排序）。</summary>
    public static async Task<IReadOnlyList<ExecutionProviderChoice>> BuildCandidatesAsync(
        EpMode mode,
        int deviceId,
        bool allowInstall,
        Logger logger,
        CancellationToken cancellationToken)
    {
        // 步骤 1：把本机「已安装」的 EP 加入依赖图并注册到 ORT（不触发下载）。
        if (mode != EpMode.Cpu)
        {
            IReadOnlyList<EpOutcome> outcomes = await EpProvisioner
                .RegisterInstalledAsync(logger, cancellationToken)
                .ConfigureAwait(false);

            foreach (EpOutcome outcome in outcomes)
            {
                logger.Detail($"  注册 {outcome.Name}: {(outcome.Success ? "成功" : "失败")} — {outcome.Detail}");
            }
        }

        // 步骤 2：按需安装（仅当用户显式允许）。
        if (mode == EpMode.Auto && allowInstall)
        {
            await InstallMissingAsync(logger, cancellationToken).ConfigureAwait(false);
        }

        // 步骤 3：基于 ORT 实际可用的 EP 设备构建候选。
        IReadOnlyList<OrtEpDevice> devices = EpProvisioner.GetDevices();
        List<ExecutionProviderChoice> windowsMl = BuildDeviceChoices(devices, logger);
        bool hasDmlDevice = devices.Any(d => d.EpName.Equals(KnownExecutionProviders.DmlProvider, StringComparison.OrdinalIgnoreCase));

        return mode switch
        {
            EpMode.Cpu => new[] { CpuChoice },

            EpMode.Dml => hasDmlDevice
                ? new[] { DmlChoice(deviceId) }
                : throw new RmbgException(
                    ExitCode.EnvironmentError,
                    "ONNX Runtime 中未发现 DirectML EP 设备。可执行 `--ep list` 查看详情，或改用 --ep cpu。"),

            EpMode.WinMl => windowsMl.Count > 0
                ? windowsMl
                : throw BuildNoWindowsMlProviderError(logger),

            _ => BuildAutoList(windowsMl, hasDmlDevice, deviceId),
        };
    }

    /// <summary>
    /// 安装本机可下载的 EP（供 <c>--install-ep</c> 使用）。
    /// </summary>
    public static async Task<IReadOnlyList<EpOutcome>> InstallAsync(
        IReadOnlyList<KnownExecutionProvider> targets,
        Logger logger,
        CancellationToken cancellationToken)
    {
        bool all = targets.Count == KnownExecutionProviders.Downloadable.Count;

        IReadOnlyList<EpOutcome> outcomes = all
            ? await EpProvisioner.InstallAllAsync(logger, cancellationToken).ConfigureAwait(false)
            : await InstallEachAsync(targets, logger, cancellationToken).ConfigureAwait(false);

        ReportInstallOutcomes(outcomes, logger);

        // 安装后立即显示 ORT 侧结果，便于确认是否真的可用。
        IReadOnlyList<string> providers = EpProvisioner.GetAvailableProviderNames();
        if (providers.Count > 0)
        {
            logger.Info(string.Empty);
            logger.Info("ONNX Runtime 当前可用的执行提供程序：");
            foreach (string name in providers)
            {
                logger.Info($"  · {name}");
            }
        }

        return outcomes;
    }

    /// <summary>打印当前机器可用的执行提供程序清单（--ep list）。</summary>
    public static void PrintAvailability(Logger logger)
    {
        logger.Info($"  运行时: Microsoft.Windows.AI.MachineLearning " +
                    $"{typeof(ExecutionProviderCatalog).Assembly.GetName().Version?.ToString(3)}");

        if (!EpProvisioner.IsAvailable)
        {
            logger.Warn("  无法访问 Windows ML 执行提供程序目录；本机可能不支持动态 EP。");
        }

        // ---- Windows ML 目录 ----
        logger.Section("Windows ML 执行提供程序目录（含未安装项）");

        IReadOnlyList<EpCatalogEntry> entries = EpProvisioner.Enumerate(logger);

        if (entries.Count == 0)
        {
            logger.Warn("  目录为空：本机没有可通过 Windows ML 动态获取的 EP。");
        }
        else
        {
            foreach (EpCatalogEntry entry in entries)
            {
                string state = entry.ReadyState switch
                {
                    ExecutionProviderReadyState.Ready => "已就绪",
                    ExecutionProviderReadyState.NotReady => "已安装（待注册）",
                    _ => "未安装",
                };

                logger.Info($"  · {entry.Label}");
                logger.Info($"      状态    : {state}（{entry.ReadyState}）");
                logger.Info($"      认证    : {entry.Certification}");

                if (!entry.IsInstalled)
                {
                    logger.Info($"      要求    : {entry.Requirement}");
                }
            }

            int missing = entries.Count(e => !e.IsInstalled);
            if (missing > 0)
            {
                logger.Info(string.Empty);
                logger.Warn($"  有 {missing} 个 EP 尚未安装。安装命令：");
                logger.Warn("    rmbg --install-ep all            # 安装全部兼容的 EP");
                logger.Warn("    rmbg --install-ep qnn            # 只安装指定 EP（按简写）");
                logger.Warn($"    可选：{KnownExecutionProviders.DescribeKeys()}");
            }
        }

        // ---- ONNX Runtime 侧 ----
        logger.Info(string.Empty);
        logger.Section("ONNX Runtime 已注册的执行提供程序");

        IReadOnlyList<string> available = EpProvisioner.GetAvailableProviderNames();
        if (available.Count == 0)
        {
            logger.Warn("  （未报告任何 EP，可能未初始化 ONNX Runtime 环境）");
        }
        else
        {
            foreach (string name in available)
            {
                logger.Info($"  · {KnownExecutionProviders.Describe(name)}");
            }
        }

        // ---- EP 设备 ----
        logger.Info(string.Empty);
        logger.Section("ONNX Runtime EP 设备");

        IReadOnlyList<OrtEpDevice> devices = EpProvisioner.GetDevices();
        if (devices.Count == 0)
        {
            logger.Warn("  （没有可用的 EP 设备）");
        }
        else
        {
            foreach (OrtEpDevice device in devices)
            {
                string hardware = device.HardwareDevice?.Type.ToString() ?? "Unknown";
                logger.Info($"  · {device.EpName}  硬件={hardware}  供应商={device.EpVendor}");
            }
        }

        // ---- 硬件 ----
        logger.Info(string.Empty);
        logger.Section("本机硬件设备");

        IReadOnlyList<OrtHardwareDevice> hardwareDevices = EpProvisioner.GetHardwareDevices();
        if (hardwareDevices.Count == 0)
        {
            logger.Warn("  （未能枚举硬件设备）");
        }
        else
        {
            foreach (IGrouping<string, OrtHardwareDevice> group in hardwareDevices.GroupBy(d => d.Type.ToString()))
            {
                logger.Info($"  · {group.Key} x{group.Count()}");
                foreach (OrtHardwareDevice device in group)
                {
                    logger.Info($"      {device.Vendor} (vendorId=0x{device.VendorId:X4}, deviceId=0x{device.DeviceId:X4})");
                }
            }
        }

        logger.Info(string.Empty);
        logger.Info("说明：CPU 与 DirectML 随 Windows ML 运行时内置；其余 EP 需经 --install-ep 下载安装后注册使用。");
        logger.Info("      `auto` 模式的候选顺序为：Windows ML 已注册 EP（NPU 优先）→ DirectML → CPU。");
    }

    /// <summary>报告安装结果。</summary>
    public static void ReportInstallOutcomes(IReadOnlyList<EpOutcome> outcomes, Logger logger)
    {
        if (outcomes.Count == 0)
        {
            return;
        }

        logger.Info(string.Empty);
        logger.Section("安装结果");

        foreach (EpOutcome outcome in outcomes)
        {
            if (outcome.Success)
            {
                logger.Success($"  ✔ {KnownExecutionProviders.Describe(outcome.Name)} — {outcome.Detail}");
            }
            else
            {
                logger.Warn($"  ✘ {KnownExecutionProviders.Describe(outcome.Name)} — {outcome.Detail}");
            }
        }
    }

    private static async Task<IReadOnlyList<EpOutcome>> InstallEachAsync(
        IReadOnlyList<KnownExecutionProvider> targets,
        Logger logger,
        CancellationToken cancellationToken)
    {
        List<EpOutcome> outcomes = new();
        IReadOnlyList<EpCatalogEntry> present = EpProvisioner.Enumerate(logger);

        foreach (KnownExecutionProvider target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            EpCatalogEntry? entry = present.FirstOrDefault(e =>
                e.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                outcomes.Add(new EpOutcome(
                    target.Name,
                    false,
                    "该 EP 未出现在本机目录中（硬件或系统版本不满足要求：" + target.Requirement + "）"));
                continue;
            }

            if (entry.IsReady)
            {
                outcomes.Add(new EpOutcome(target.Name, true, "本机已安装且已就绪，无需重新下载"));
                continue;
            }

            outcomes.Add(await EpProvisioner.EnsureAsync(target.Name, logger, cancellationToken).ConfigureAwait(false));
        }

        return outcomes;
    }

    private static async Task InstallMissingAsync(Logger logger, CancellationToken cancellationToken)
    {
        IReadOnlyList<EpCatalogEntry> missing = EpProvisioner.Enumerate(logger)
            .Where(e => !e.IsInstalled)
            .ToArray();

        if (missing.Count == 0)
        {
            return;
        }

        logger.Info($"已获授权安装 {missing.Count} 个缺失的执行提供程序…");
        foreach (EpCatalogEntry entry in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EpOutcome outcome = await EpProvisioner.EnsureAsync(entry.Name, logger, cancellationToken).ConfigureAwait(false);
            logger.Detail($"  {entry.Name}: {(outcome.Success ? "成功" : "失败")} — {outcome.Detail}");
        }
    }

    /// <summary>
    /// 从 ORT 的 EP 设备列表构造候选，按硬件类型（NPU &gt; GPU &gt; CPU）与供应商优先级排序。
    /// </summary>
    private static List<ExecutionProviderChoice> BuildDeviceChoices(
        IReadOnlyList<OrtEpDevice> devices,
        Logger logger)
    {
        List<ExecutionProviderChoice> choices = new();
        OrtEnv env = OrtEnv.Instance();

        // 内置 EP 用专门的重载追加，这里只处理通过目录注册进来的 Windows ML EP。
        IEnumerable<OrtEpDevice> windowsMlDevices = devices.Where(d =>
            !d.EpName.Equals(KnownExecutionProviders.CpuProvider, StringComparison.OrdinalIgnoreCase)
            && !d.EpName.Equals(KnownExecutionProviders.DmlProvider, StringComparison.OrdinalIgnoreCase)
            && d.HardwareDevice is not null);

        foreach (OrtEpDevice device in windowsMlDevices
                     .OrderByDescending(d => HardwareRank(d.HardwareDevice!.Type))
                     .ThenByDescending(d => KnownExecutionProviders.PriorityOf(d.EpName)))
        {
            OrtEpDevice captured = device;
            string hardware = captured.HardwareDevice!.Type.ToString();

            choices.Add(new ExecutionProviderChoice(
                $"winml:{captured.EpName}:{hardware}",
                $"Windows ML / {captured.EpName}",
                $"硬件 {hardware}，供应商 {captured.EpVendor}",
                options => options.AppendExecutionProvider(
                    env,
                    new[] { captured },
                    new Dictionary<string, string>())));

            logger.Detail($"发现 EP 设备：{captured.EpName} / {hardware}");
        }

        return choices;
    }

    private static IReadOnlyList<ExecutionProviderChoice> BuildAutoList(
        List<ExecutionProviderChoice> windowsMl,
        bool hasDmlDevice,
        int deviceId)
    {
        List<ExecutionProviderChoice> candidates = new(windowsMl);

        if (hasDmlDevice)
        {
            candidates.Add(DmlChoice(deviceId));
        }

        candidates.Add(CpuChoice);
        return candidates;
    }

    private static ExecutionProviderChoice DmlChoice(int deviceId) => new(
        "dml",
        "DirectML (GPU)",
        $"device {deviceId}",
        options => options.AppendExecutionProvider_DML(deviceId));

    private static ExecutionProviderChoice CpuChoice => new(
        "cpu",
        "CPU (MLAS)",
        "内置",
        options => options.AppendExecutionProvider_CPU(1));

    private static RmbgException BuildNoWindowsMlProviderError(Logger logger)
    {
        IReadOnlyList<EpCatalogEntry> entries = EpProvisioner.Enumerate(logger);
        IReadOnlyList<EpCatalogEntry> missing = entries.Where(e => !e.IsInstalled).ToArray();

        string body = missing.Count > 0
            ? "以下 EP 可在本机安装，但尚未安装：" + Environment.NewLine +
              "  - " + string.Join(
                  Environment.NewLine + "  - ",
                  missing.Select(e => $"{KnownExecutionProviders.Describe(e.Name)}（要求：{e.Requirement}）")) +
              Environment.NewLine + Environment.NewLine +
              "安装命令：" + Environment.NewLine +
              "  rmbg --install-ep all" + Environment.NewLine +
              "  rmbg --install-ep " + (KnownExecutionProviders.Find(missing[0].Name)?.Aliases.FirstOrDefault() ?? missing[0].Name)
            : "本机目录中没有可用的 Windows ML 执行提供程序（硬件或系统版本不满足要求）。";

        return new RmbgException(
            ExitCode.EnvironmentError,
            "Windows ML 中没有已注册的执行提供程序。" + Environment.NewLine + Environment.NewLine + body +
            Environment.NewLine + Environment.NewLine +
            "也可改用 --ep dml（DirectML，兼容任意支持 D3D12 的显卡）或 --ep cpu。" +
            Environment.NewLine + "执行 `rmbg --ep list` 可查看完整环境自检结果。");
    }

    private static int HardwareRank(OrtHardwareDeviceType type) => type switch
    {
        OrtHardwareDeviceType.NPU => 30,
        OrtHardwareDeviceType.GPU => 20,
        OrtHardwareDeviceType.CPU => 10,
        _ => 0,
    };
}
