using RmbgCli.Core;
using RmbgCli.Imaging;
using SixLabors.ImageSharp.PixelFormats;

// Windows ML 包内也有一个 ModelCatalog 类型，与本地模型清单同名。
// 这里不整体引入其命名空间，只对需要的枚举做别名，避免二义性引用。
using ExecutionProviderReadyState = Microsoft.Windows.AI.MachineLearning.ExecutionProviderReadyState;

namespace RmbgCli.Cli;

/// <summary>
/// 引导式操作模式（<c>--guide</c>，或直接运行且未提供任何参数时自动进入）。
///
/// 目标是让不熟悉命令行参数的用户也能跑通完整流程：
/// 环境自检 → 缺模型就引导下载 → 逐项确认抠图参数 → 执行 → 回到主菜单。
/// </summary>
internal sealed class InteractiveWizard
{
    private readonly Logger _logger;
    private readonly CancellationToken _cancellationToken;

    public InteractiveWizard(Logger logger, CancellationToken cancellationToken)
    {
        _logger = logger;
        _cancellationToken = cancellationToken;
    }

    /// <summary>运行引导式会话。</summary>
    public async Task<int> RunAsync()
    {
        PrintBanner();
        PrintEnvironmentSummary();

        // 首次进入时若连模型都没有，直接把默认选项落在"下载模型"上。
        bool hasModel = TryFindModel(out string? modelPath);
        int defaultMenuIndex = hasModel ? 0 : 1;

        while (true)
        {
            _cancellationToken.ThrowIfCancellationRequested();

            int choice = ConsolePrompt.ChooseIndexed(
                "主菜单",
                new (string, string?)[]
                {
                    ("开始抠图", "引导式设置输入、输出与后处理参数"),
                    ("下载 / 更换模型", "aria2 多连接下载，或内置多线程下载"),
                    ("安装 / 管理执行提供程序", "启用 NPU / GPU 加速；查看哪些 EP 可安装"),
                    ("查看环境与执行提供程序", "模型、aria2、EP 与硬件状态自检"),
                    ("查看完整命令行用法", null),
                    ("退出", null),
                },
                defaultMenuIndex);

            if (choice < 0 || choice == 5)
            {
                _logger.Info(string.Empty);
                _logger.Info("已退出。");
                return (int)ExitCode.Success;
            }

            switch (choice)
            {
                case 0:
                    await RunCutoutFlowAsync(modelPath).ConfigureAwait(false);
                    break;

                case 1:
                    await RunModelDownloadFlowAsync().ConfigureAwait(false);
                    // 下载完成后刷新模型路径，避免后续抠图仍报"模型缺失"。
                    TryFindModel(out modelPath);
                    defaultMenuIndex = 0;
                    break;

                case 2:
                    await RunEpManagementFlowAsync().ConfigureAwait(false);
                    break;

                case 3:
                    PrintEnvironmentDetails();
                    break;

                case 4:
                    Console.WriteLine(HelpText.Full);
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- 抠图流程

    private async Task RunCutoutFlowAsync(string? modelPath)
    {
        if (modelPath is null)
        {
            _logger.Warn("尚不可用：未找到 ONNX 模型。请先在主菜单选择「下载 / 更换模型」。");
            return;
        }

        AppOptions options = new();

        // ---- 输入 ----
        Console.WriteLine();
        ConsolePromptWriteTitle("第 1 步 / 共 6 步：选择输入");

        string[]? inputs = ConsolePrompt.AskPaths(
            "请输入图片、目录或通配符（如 *.png）",
            PathKind.ExistingFileOrDirectory);

        if (inputs is null)
        {
            return;
        }

        options.Inputs.AddRange(inputs);

        // ---- 输出 ----
        Console.WriteLine();
        ConsolePromptWriteTitle("第 2 步 / 共 6 步：选择输出");

        string suggestedOutput = SuggestOutput(inputs);

        string? output = ConsolePrompt.AskSinglePath("请输入输出文件（单张）或输出目录（多张）", PathKind.AnyPath, suggestedOutput);

        if (output is null)
        {
            return;
        }

        options.Output = output;

        // 输入中只要存在非普通文件项（目录 / 通配符），就问一次是否递归。
        bool maybeDirectory = Array.Exists(inputs, static p => !File.Exists(p));
        if (maybeDirectory && ConsolePrompt.Confirm("是否递归搜索子目录？", false))
        {
            options.Recursive = true;
        }

        if (ConsolePrompt.Confirm("输出文件已存在时是否覆盖？", false))
        {
            options.Overwrite = true;
        }

        // ---- 格式与背景 ----
        Console.WriteLine();
        ConsolePromptWriteTitle("第 3 步 / 共 6 步：输出格式");

        int formatIndex = ConsolePrompt.ChooseIndexed(
            "输出格式",
            new (string, string?)[]
            {
                ("PNG（无损，支持透明，推荐）", null),
                ("WebP（体积更小，支持透明）", null),
            });

        options.Format = formatIndex == 1 ? OutputFormat.Webp : OutputFormat.Png;

        Rgba32? background = ConsolePrompt.AskBackground(out bool backgroundCancelled);
        if (backgroundCancelled)
        {
            return;
        }

        options.Background = background;

        // ---- 蒙版 ----
        Console.WriteLine();
        ConsolePromptWriteTitle("第 4 步 / 共 6 步：蒙版导出");

        int maskIndex = ConsolePrompt.ChooseIndexed(
            "是否额外导出 8bit 灰度蒙版",
            new (string, string?)[]
            {
                ("不导出（推荐）", null),
                ("同时导出抠图结果与蒙版", "便于在 Photoshop 等工具里二次修边"),
                ("只导出蒙版", null),
            });

        options.MaskOutput = maskIndex switch
        {
            1 => MaskOutputMode.Both,
            2 => MaskOutputMode.Mask,
            _ => MaskOutputMode.None,
        };

        // ---- 边缘处理 ----
        Console.WriteLine();
        ConsolePromptWriteTitle("第 5 步 / 共 6 步：边缘与阈值");

        options.Feather = ConsolePrompt.AskFloat("边缘羽化半径（像素，0 表示关闭）", 0f, 0f, 64f);
        options.Threshold = ConsolePrompt.AskFloat("二值化阈值（0 表示保留软蒙版，推荐）", 0f, 0f, 1f);

        if (ConsolePrompt.Confirm("是否反转蒙版（主体与背景判断相反时使用）？", false))
        {
            options.Invert = true;
        }

        // ---- 推理后端 ----
        Console.WriteLine();
        ConsolePromptWriteTitle("第 6 步 / 共 6 步：推理后端");

        // 有可安装的 EP 时先给出提示与安装机会，避免用户拿到"只有 CPU/DirectML"的结果却不明白原因。
        await OfferMissingExecutionProvidersAsync().ConfigureAwait(false);

        int epIndex = ConsolePrompt.ChooseIndexed(
            "执行提供程序",
            new (string, string?)[]
            {
                ("自动选择（Windows ML NPU → DirectML GPU → CPU）", "推荐"),
                ("DirectML（GPU）", "兼容任意支持 D3D12 的显卡"),
                ("CPU", "最兼容，速度最慢"),
            });

        options.Ep = epIndex switch
        {
            1 => EpMode.Dml,
            2 => EpMode.Cpu,
            _ => EpMode.Auto,
        };

        // ---- 概要确认 ----
        PrintCutoutSummary(options, modelPath);

        if (!ConsolePrompt.Confirm("确认开始处理？", true))
        {
            _logger.Info("已取消本次操作。");
            return;
        }

        Console.WriteLine();
        int exitCode = await Program.ExecuteAsync(options, _logger, _cancellationToken).ConfigureAwait(false);

        if (exitCode == (int)ExitCode.Success)
        {
            _logger.Success("本批处理全部成功。");
        }

        Console.WriteLine();
        ConsolePrompt.Pause("按回车回到主菜单…");
    }

    // -------------------------------------------------- 执行提供程序安装流程

    /// <summary>EP 管理菜单：查看状态、安装指定的或全部可安装的 EP。</summary>
    private async Task RunEpManagementFlowAsync()
    {
        Console.WriteLine();
        ConsolePromptWriteTitle("安装 / 管理执行提供程序");

        _logger.Info("  CPU 与 DirectML 随 Windows ML 运行时内置，无需安装。");
        _logger.Info("  其余 EP 需按需下载安装（要求 Windows 11 24H2 / build 26100 及以上），");
        _logger.Info("  安装后会自动注册到 ONNX Runtime，之后 auto 模式即可选用。");
        Console.WriteLine();

        IReadOnlyList<EpCatalogEntry> entries = EpProvisioner.Enumerate(_logger);

        if (entries.Count == 0)
        {
            _logger.Warn("  本机目录中没有可通过 Windows ML 动态获取的 EP。");
            _logger.Info("  这通常意味着硬件不满足要求（需要对应厂商的 NPU 或较新 GPU）。");
            _logger.Info($"  如果确认硬件满足，可执行 `rmbg --ep list` 核对已注册的 EP 设备与硬件清单。");
            Console.WriteLine();
            return;
        }

        // 展示当前状态
        _logger.Section("本机 EP 状态");
        foreach (EpCatalogEntry entry in entries)
        {
            string state = entry.ReadyState switch
            {
                ExecutionProviderReadyState.Ready => "已就绪",
                ExecutionProviderReadyState.NotReady => "已安装（待注册）",
                _ => "未安装",
            };

            if (entry.IsInstalled)
            {
                _logger.Success($"  ✔ {entry.Label} — {state}");
            }
            else
            {
                _logger.Info($"  · {entry.Label} — {state}");
                _logger.Info($"      要求：{entry.Requirement}");
            }
        }

        IReadOnlyList<EpCatalogEntry> missing = entries.Where(e => !e.IsInstalled).ToArray();
        Console.WriteLine();

        if (missing.Count == 0)
        {
            _logger.Success("  所有兼容的 EP 均已安装。");
            Console.WriteLine();
            return;
        }

        // 安装目标选择
        List<(string, string?)> menu = new()
        {
            ($"安装全部 {missing.Count} 个未安装的 EP", "逐个下载，可能需要数分钟"),
        };

        foreach (EpCatalogEntry entry in missing)
        {
            menu.Add(($"只安装 {KnownExecutionProviders.Describe(entry.Name)}", entry.Requirement));
        }

        menu.Add(("返回（不安装）", null));

        int choice = ConsolePrompt.ChooseIndexed("请选择要安装的目标", menu, 0);

        if (choice < 0 || choice == menu.Count - 1)
        {
            return;
        }

        IReadOnlyList<KnownExecutionProvider> targets = choice switch
        {
            0 => KnownExecutionProviders.Downloadable
                .Where(k => missing.Any(m => m.Name.Equals(k.Name, StringComparison.OrdinalIgnoreCase)))
                .ToArray(),
            _ => KnownExecutionProviders.Find(missing[choice - 1].Name) is { } single
                ? new[] { single }
                : Array.Empty<KnownExecutionProvider>(),
        };

        if (targets.Count == 0)
        {
            _logger.Warn("  无法解析所选 EP（可能未收录在已知清单中），请改用 `rmbg --install-ep`。");
            return;
        }

        _logger.Info(string.Empty);
        _logger.Info("  即将安装：");
        foreach (KnownExecutionProvider target in targets)
        {
            _logger.Info($"    · {target.Label} — {target.Requirement}");
        }

        _logger.Info(string.Empty);
        _logger.Warn("  安装包由 Windows 从官方更新源下载，首次可能需要数分钟。");

        if (!ConsolePrompt.Confirm("确认开始安装？", true))
        {
            _logger.Info("已取消。");
            return;
        }

        Console.WriteLine();
        IReadOnlyList<EpOutcome> outcomes = await ExecutionProviders
            .InstallAsync(targets, _logger, _cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine();
        if (outcomes.All(o => o.Success))
        {
            _logger.Success("  安装完成，后续 auto 模式会自动优先使用这些 EP。");
        }
        else
        {
            _logger.Warn("  部分 EP 安装失败，失败原因已在上方列出。");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// 若存在可安装但未安装的 EP，给出提示并提供一次安装机会。
    /// 返回后继续原有流程（安装与否由用户决定）。
    /// </summary>
    private async Task OfferMissingExecutionProvidersAsync()
    {
        IReadOnlyList<EpCatalogEntry> missing = EpProvisioner.Enumerate(_logger)
            .Where(e => !e.IsInstalled)
            .ToArray();

        if (missing.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        _logger.Warn($"  提示：本机有 {missing.Count} 个可安装但仍未安装的执行提供程序：");
        foreach (EpCatalogEntry entry in missing)
        {
            _logger.Info($"    · {entry.Label}（要求：{entry.Requirement}）");
        }

        _logger.Info("  安装后可获得 NPU/GPU 加速，推理速度可能提升数倍。");
        Console.WriteLine();

        if (!ConsolePrompt.Confirm("  现在安装吗？（也可稍后从主菜单「安装 / 管理执行提供程序」进入）", false))
        {
            _logger.Info("  已跳过，将使用当前可用的后端。");
            return;
        }

        Console.WriteLine();
        IReadOnlyList<EpOutcome> outcomes = await EpProvisioner
            .InstallAllAsync(_logger, _cancellationToken)
            .ConfigureAwait(false);

        ExecutionProviders.ReportInstallOutcomes(outcomes, _logger);
        Console.WriteLine();
    }

    // ------------------------------------------------------------ 模型下载流程

    private async Task RunModelDownloadFlowAsync()
    {
        Console.WriteLine();
        ConsolePromptWriteTitle("下载 RMBG-2.0 模型");

        IReadOnlyList<ModelVariant> variants = ModelCatalog.Variants;

        int variantIndex = ConsolePrompt.ChooseIndexed(
            "选择模型精度变体",
            variants.Select(v => (Label: $"{v.Key}（约 {v.ApproximateMegabytes:F0} MB）", Hint: (string?)v.Note)).ToArray(),
            0);

        if (variantIndex < 0)
        {
            return;
        }

        ModelVariant variant = variants[variantIndex];

        string defaultDirectory = ModelCatalog.DefaultDirectory();
        string? directory = ConsolePrompt.AskSinglePath("模型存放目录", PathKind.AnyPath, defaultDirectory);

        if (directory is null)
        {
            return;
        }

        string? aria2Path = Aria2Backend.Locate();
        bool preferAria2;
        bool allowInstallAria2;

        if (aria2Path is not null)
        {
            string version = Aria2Backend.TryGetVersion(aria2Path) ?? "版本未知";
            _logger.Info($"  已检测到 aria2c：{aria2Path}");
            _logger.Info($"  版本：{version}");

            preferAria2 = ConsolePrompt.ChooseIndexed(
                "下载后端",
                new (string, string?)[]
                {
                    ("aria2c 多连接下载（推荐）", "支持断点续传，速度最好"),
                    ("内置多线程下载器", "不依赖外部程序"),
                }) == 0;

            allowInstallAria2 = false;
        }
        else
        {
            int backendIndex = ConsolePrompt.ChooseIndexed(
                "下载后端（未检测到 aria2c）",
                new (string, string?)[]
                {
                    ("安装 aria2 便携版后下载（推荐）", $"官方 GitHub Release，约 2.4 MB；会校验 SHA256"),
                    ("仅使用内置多线程下载器", "零外部依赖，速度略逊"),
                });

            if (backendIndex < 0)
            {
                return;
            }

            preferAria2 = backendIndex == 0;
            allowInstallAria2 = backendIndex == 0;
        }

        int connections = ConsolePrompt.AskInt("并发连接数", preferAria2 ? 16 : 8, 1, 32);

        Console.WriteLine();
        _logger.Info("  即将开始：");
        _logger.Info($"    变体  : {variant.Key} / {variant.FileName}");
        _logger.Info($"    目录  : {Path.GetFullPath(directory)}");
        _logger.Info($"    后端  : {(preferAria2 ? (aria2Path is null ? "aria2c（将先安装便携版）" : "aria2c") : "内置多线程下载器")}");
        _logger.Info($"    连接数: {connections}");
        Console.WriteLine();

        if (!ConsolePrompt.Confirm("确认开始下载？", true))
        {
            _logger.Info("已取消。");
            return;
        }

        ModelDownloadRequest request = new(
            Variant: variant,
            Directory: directory,
            Connections: connections,
            PreferAria2: preferAria2,
            AllowInstallAria2: allowInstallAria2,
            Overwrite: true);

        try
        {
            string path = await ModelDownloader.DownloadAsync(request, _logger, _cancellationToken).ConfigureAwait(false);
            _logger.Success($"模型已就绪：{path}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RmbgException ex)
        {
            _logger.Error(ex.Message);
        }

        Console.WriteLine();
    }

    // ------------------------------------------------------------------ 环境信息

    private void PrintBanner()
    {
        Console.WriteLine();
        _logger.Section("rmbg 引导式操作");
        _logger.Info($"  {HelpText.VersionLine}");
        _logger.Info("  随时输入 q 返回上一层；在提问处按 Ctrl+C 可立即退出。");
        _logger.Info("  提示：可以从资源管理器把文件直接拖进本窗口。");
    }

    private void PrintEnvironmentSummary()
    {
        Console.WriteLine();

        if (TryFindModel(out string? modelPath))
        {
            FileInfo info = new(modelPath!);
            _logger.Success($"  ✔ 模型已就绪：{info.Name}（{info.Length / 1024d / 1024d:F0} MB）");
        }
        else
        {
            _logger.Warn("  ✘ 未找到 ONNX 模型 —— 请先在主菜单选择「下载 / 更换模型」");
        }

        string? aria2 = Aria2Backend.Locate();
        if (aria2 is not null)
        {
            _logger.Success($"  ✔ aria2c 可用：{Aria2Backend.TryGetVersion(aria2) ?? aria2}");
        }
        else
        {
            _logger.Info("  · aria2c 未安装 —— 下载模型时可自动安装便携版（也可继续用内置多线程下载器）");
        }

        // 推理后端概览：已注册的 EP 有哪些，以及是否还有可安装但未安装的加速后端。
        IReadOnlyList<string> providers = EpProvisioner.GetAvailableProviderNames();
        if (providers.Count > 0)
        {
            _logger.Success($"  ✔ 推理后端：{string.Join("、", providers.Select(KnownExecutionProviders.Describe))}");
        }

        IReadOnlyList<EpCatalogEntry> missing = EpProvisioner.Enumerate(_logger)
            .Where(e => !e.IsInstalled)
            .ToArray();

        if (missing.Count > 0)
        {
            _logger.Info($"  · 有 {missing.Count} 个执行提供程序可安装以获得加速：" +
                         $"{string.Join("、", missing.Select(e => e.Label))}");
            _logger.Info("    可在主菜单选择「安装 / 管理执行提供程序」");
        }
    }

    private void PrintEnvironmentDetails()
    {
        Console.WriteLine();

        if (TryFindModel(out string? modelPath))
        {
            _logger.Section("模型");
            _logger.Info($"  路径: {modelPath}");
            FileInfo info = new(modelPath!);
            _logger.Info($"  体积: {info.Length / 1024d / 1024d:F1} MB");
            _logger.Info($"  修改时间: {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
        }
        else
        {
            _logger.Warn("未找到 ONNX 模型。已搜索 models\\onnx 等位置，可用主菜单的「下载 / 更换模型」获取。");
        }

        Console.WriteLine();
        _logger.Section("aria2");
        string? aria2 = Aria2Backend.Locate();
        if (aria2 is null)
        {
            _logger.Info("  未安装。便携安装目录：" + Aria2Backend.PortableDirectory());
            _logger.Info("  也可用 winget 安装：winget install aria2.aria2");
        }
        else
        {
            _logger.Info($"  路径: {aria2}");
            _logger.Info($"  版本: {Aria2Backend.TryGetVersion(aria2) ?? "未知"}");
        }

        Console.WriteLine();
        ExecutionProviders.PrintAvailability(_logger);
    }

    // -------------------------------------------------------------------- 工具

    private bool TryFindModel(out string? modelPath)
    {
        try
        {
            modelPath = ModelLocator.Resolve(null, new Logger(quiet: true, verbose: false));
            return true;
        }
        catch (RmbgException)
        {
            modelPath = null;
            return false;
        }
    }

    private static string SuggestOutput(string[] inputs)
    {
        string first = inputs[0];
        bool wildcard = first.IndexOfAny(new[] { '*', '?' }) >= 0;

        if (!wildcard && File.Exists(first))
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(first)) ?? Environment.CurrentDirectory;
            string stem = Path.GetFileNameWithoutExtension(first);
            return Path.Combine(directory, stem + "_nobg.png");
        }

        string baseDirectory = wildcard
            ? Path.GetDirectoryName(Path.GetFullPath(first)) ?? Environment.CurrentDirectory
            : Path.GetFullPath(first);

        return Path.Combine(baseDirectory, "nobg");
    }

    private void PrintCutoutSummary(AppOptions options, string modelPath)
    {
        Console.WriteLine();
        _logger.Section("即将执行");
        _logger.Info($"  模型      : {Path.GetFileName(modelPath)}");
        _logger.Info($"  输入      : {string.Join("；", options.Inputs)}");
        _logger.Info($"  输出      : {options.Output}");
        _logger.Info($"  递归子目录: {(options.Recursive ? "是" : "否")}");
        _logger.Info($"  覆盖已有  : {(options.Overwrite ? "是" : "否")}");
        _logger.Info($"  格式      : {options.Format.ToString().ToLowerInvariant()}");
        _logger.Info($"  背景      : {(options.Background is null ? "透明" : options.Background.Value.ToHex())}");
        _logger.Info($"  蒙版      : {options.MaskOutput}");
        _logger.Info($"  羽化      : {options.Feather:0.##} px");
        _logger.Info($"  阈值      : {(options.Threshold <= 0f ? "软蒙版（不二值化）" : options.Threshold.ToString("0.##"))}");
        _logger.Info($"  反转蒙版  : {(options.Invert ? "是" : "否")}");
        _logger.Info($"  推理后端  : {options.Ep}");
        Console.WriteLine();
    }

    private static void ConsolePromptWriteTitle(string text)
    {
        try
        {
            ConsoleColor previous = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(text);
            Console.ForegroundColor = previous;
        }
        catch (IOException)
        {
            Console.WriteLine(text);
        }
        catch (PlatformNotSupportedException)
        {
            Console.WriteLine(text);
        }
    }
}
