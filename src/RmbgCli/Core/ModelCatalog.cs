namespace RmbgCli.Core;

/// <summary>一个可下载的 RMBG-2.0 ONNX 变体。</summary>
/// <param name="Key">命令行使用的短标识，如 fp16。</param>
/// <param name="Aliases">额外可接受的写法。</param>
/// <param name="FileName">仓库内文件名。</param>
/// <param name="ApproximateBytes">体积（用于进度显示与下载前提示）。</param>
/// <param name="Note">适用场景说明。</param>
internal sealed record ModelVariant(
    string Key,
    string[] Aliases,
    string FileName,
    long ApproximateBytes,
    string Note)
{
    public double ApproximateMegabytes => ApproximateBytes / 1024d / 1024d;

    public bool Matches(string value)
        => string.Equals(Key, value, StringComparison.OrdinalIgnoreCase)
           || Array.Exists(Aliases, a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// RMBG-2.0 权重清单。
///
/// 选择 ModelScope 而非 Hugging Face 作为来源：国内可直连，无需代理。
/// 该仓库同时是 scripts/Get-RmbgModel.ps1 与内置下载器的唯一数据源，避免两处清单漂移。
/// </summary>
internal static class ModelCatalog
{
    /// <summary>ModelScope 上的 onnx 目录直链前缀。</summary>
    public const string BaseUri = "https://www.modelscope.cn/models/AI-ModelScope/RMBG-2.0/resolve/master/onnx";

    /// <summary>模型仓库主页，用于错误提示。</summary>
    public const string RepositoryUri = "https://www.modelscope.cn/models/AI-ModelScope/RMBG-2.0";

    public static IReadOnlyList<ModelVariant> Variants { get; } = new[]
    {
        new ModelVariant(
            "fp16",
            new[] { "half", "f16" },
            "model_fp16.onnx",
            513_576_499,
            "推荐默认：质量/速度/体积平衡，GPU 与 NPU 友好"),

        new ModelVariant(
            "fp32",
            new[] { "float32", "full", "original" },
            "model.onnx",
            1_024_331_469,
            "官方参考导出，数值最保守；CPU 上最稳妥"),

        new ModelVariant(
            "int8",
            new[] { "quantized", "q8" },
            "model_int8.onnx",
            366_087_445,
            "体积小、CPU 友好，适合无独显机器"),

        new ModelVariant(
            "uint8",
            Array.Empty<string>(),
            "model_uint8.onnx",
            366_087_549,
            "无符号 8bit 量化变体"),

        new ModelVariant(
            "q4f16",
            new[] { "q4" },
            "model_q4f16.onnx",
            233_815_293,
            "体积最小，细节边缘有质量损失"),

        new ModelVariant(
            "q4",
            new[] { "q4int" },
            "model_q4.onnx",
            367_451_512,
            "4bit 权重，体积接近 int8"),

        new ModelVariant(
            "bnb4",
            new[] { "bitsandbytes" },
            "model_bnb4.onnx",
            355_288_046,
            "bitsandbytes 4bit 导出"),

        new ModelVariant(
            "quantized",
            new[] { "qint8" },
            "model_quantized.onnx",
            366_087_549,
            "与 int8 等价的量化导出（文件名不同）"),
    };

    /// <summary>默认变体：fp16。</summary>
    public static ModelVariant Default => Variants[0];

    /// <summary>按 key 或别名查找变体；找不到返回 null。</summary>
    public static ModelVariant? Find(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        foreach (ModelVariant variant in Variants)
        {
            if (variant.Matches(value))
            {
                return variant;
            }
        }

        return null;
    }

    /// <summary>拼出直链。</summary>
    public static string GetUrl(ModelVariant variant) => $"{BaseUri}/{variant.FileName}";

    /// <summary>所有可接受的 key，用于错误提示。</summary>
    public static string DescribeKeys() => string.Join(" | ", Variants.Select(v => v.Key));

    /// <summary>默认存放目录：<仓库根>\models\onnx。</summary>
    public static string DefaultDirectory()
    {
        // 从程序目录向上回溯，找到含 src 或 scripts 的仓库根，否则退回程序目录下的 models\onnx。
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int depth = 0; depth < 9 && directory is not null; depth++)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src"))
                || Directory.Exists(Path.Combine(directory.FullName, "scripts")))
            {
                return Path.Combine(directory.FullName, "models", "onnx");
            }

            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "models", "onnx");
    }
}
