namespace RmbgCli.Core;

/// <summary>
/// Windows ML 可按需下载的执行提供程序元数据。
///
/// 数据来源：官方文档
/// https://learn.microsoft.com/windows/ai/new-windows-ml/supported-execution-providers
/// 其中 CPU 与 DirectML 属于「包含的执行提供程序」（随运行时内置），
/// 其余六个需要通过 <c>ExecutionProviderCatalog</c> 动态下载安装。
/// </summary>
/// <param name="Name">EP 名称（与 <c>OrtEpDevice.EpName</c> 一致）。</param>
/// <param name="Aliases">命令行可接受的简写。</param>
/// <param name="Vendor">供应商。</param>
/// <param name="Hardware">硬件类型（NPU / GPU / CPU）。</param>
/// <param name="Requirement">硬件与驱动要求。</param>
/// <param name="Priority">显式选择时的优先级，数值越大越优先。</param>
internal sealed record KnownExecutionProvider(
    string Name,
    string[] Aliases,
    string Vendor,
    string Hardware,
    string Requirement,
    int Priority)
{
    public bool Matches(string value)
        => string.Equals(Name, value, StringComparison.OrdinalIgnoreCase)
           || Array.Exists(Aliases, a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>面向用户的标签，如「高通 QNN（NPU）」。</summary>
    public string Label => $"{Vendor} {ShortName}（{Hardware}）";

    /// <summary>去掉 ExecutionProvider 后缀的短名。</summary>
    public string ShortName => Name.EndsWith("ExecutionProvider", StringComparison.Ordinal)
        ? Name[..^"ExecutionProvider".Length]
        : Name;
}

/// <summary>Windows ML 执行提供程序清单与解析。</summary>
internal static class KnownExecutionProviders
{
    /// <summary>随 Windows ML 运行时内置的 EP（无需下载）。</summary>
    public const string CpuProvider = "CPUExecutionProvider";

    public const string DmlProvider = "DmlExecutionProvider";

    /// <summary>可通过目录动态下载安装的 EP，按推荐优先级降序。</summary>
    public static IReadOnlyList<KnownExecutionProvider> Downloadable { get; } = new[]
    {
        new KnownExecutionProvider(
            "QNNExecutionProvider",
            new[] { "qnn", "qualcomm", "hexagon" },
            "高通 Qualcomm",
            "NPU",
            "Snapdragon X Elite / X Plus，Hexagon NPU 驱动 ≥ 30.0.140.0",
            100),

        new KnownExecutionProvider(
            "VitisAIExecutionProvider",
            new[] { "vitisai", "vitis", "amd" },
            "AMD",
            "NPU",
            "Ryzen AI；Adrenalin 25.6.3–25.9.1 + NPU 驱动 32.00.0203.280–297",
            95),

        new KnownExecutionProvider(
            "OpenVINOExecutionProvider",
            new[] { "openvino", "intel" },
            "英特尔 Intel",
            "NPU / GPU / CPU",
            "11 代 Core 及以上；NPU 需 Core Ultra 系列 1 及以上",
            90),

        new KnownExecutionProvider(
            "NvTensorRtRtxExecutionProvider",
            new[] { "nvtensorrtrtx", "nvidia", "tensorrt" },
            "NVIDIA",
            "GPU",
            "GeForce RTX 30 系及以上，驱动 ≥ 32.0.15.5585 + CUDA 12.5",
            85),

        new KnownExecutionProvider(
            "MIGraphXExecutionProvider",
            new[] { "migraphx" },
            "AMD",
            "GPU",
            "RDNA 3 及以上，驱动 ≥ 25.10.13.09（不支持 GenAI 场景）",
            80),

        new KnownExecutionProvider(
            "WebGpuExecutionProvider",
            new[] { "webgpu" },
            "Microsoft",
            "GPU",
            "实验性；需 Microsoft.Windows.AI.MachineLearning ≥ 2.4.66-preview",
            60),
    };

    /// <summary>按名称或简写查找；未知返回 null。</summary>
    public static KnownExecutionProvider? Find(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        foreach (KnownExecutionProvider provider in Downloadable)
        {
            if (provider.Matches(value))
            {
                return provider;
            }
        }

        return null;
    }

    /// <summary>
    /// 解析用户输入的 EP 标识：支持 <c>all</c> 与各 EP 的名称/简写。
    /// </summary>
    /// <returns>解析成功返回目标 EP 列表；<c>all</c> 返回全部可下载 EP；失败返回 null。</returns>
    public static IReadOnlyList<KnownExecutionProvider>? Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return Downloadable;
        }

        KnownExecutionProvider? single = Find(value);
        return single is null ? null : new[] { single };
    }

    /// <summary>所有可接受的标识，用于错误提示。</summary>
    public static string DescribeKeys()
        => "all | " + string.Join(" | ", Downloadable.Select(p => p.Aliases.FirstOrDefault() ?? p.Name));

    /// <summary>给出显式选择优先级；内置 EP 也有固定分值。</summary>
    public static int PriorityOf(string epName)
    {
        if (epName.Equals(CpuProvider, StringComparison.OrdinalIgnoreCase))
        {
            return 5;
        }

        if (epName.Equals(DmlProvider, StringComparison.OrdinalIgnoreCase))
        {
            return 50;
        }

        KnownExecutionProvider? known = Find(epName);
        return known?.Priority ?? 40;
    }

    /// <summary>面向用户的标签；未知 EP 原样返回。</summary>
    public static string Describe(string epName)
    {
        if (epName.Equals(CpuProvider, StringComparison.OrdinalIgnoreCase))
        {
            return "CPU（内置）";
        }

        if (epName.Equals(DmlProvider, StringComparison.OrdinalIgnoreCase))
        {
            return "DirectML GPU（内置）";
        }

        KnownExecutionProvider? known = Find(epName);
        return known is null ? epName : $"{known.Label} · {epName}";
    }
}
