using RmbgCli.Cli;
using RmbgCli.Imaging;

namespace RmbgCli.Core;

/// <summary>
/// 定位 RMBG-2.0 的 ONNX 模型文件。
///
/// 查找顺序：
///   1. 命令行 --model 指定的路径（可以是文件或目录）
///   2. 环境变量 RMBG_MODEL
///   3. 相对当前目录与程序目录的 models/onnx、models 目录
/// </summary>
internal static class ModelLocator
{
    /// <summary>环境变量名。</summary>
    public const string ModelEnvironmentVariable = "RMBG_MODEL";

    /// <summary>在多候选模型共存时的优先顺序：兼顾质量与体积。</summary>
    private static readonly string[] PreferredFileNames =
    {
        "model_fp16.onnx",   // 推荐默认：体积/质量/速度平衡，且 fp16 在 GPU/NPU 上更快
        "model.onnx",        // 官方 fp32 原始导出
        "model_fp32.onnx",
        "model_bf16.onnx",
        "model_q4f16.onnx",  // 低带宽设备
        "model_int8.onnx",
        "model_quantized.onnx",
        "model_uint8.onnx",
    };

    private static readonly string[] RelativeSearchDirectories =
    {
        Path.Combine("models", "onnx"),
        "models",
        "onnx",
        ".",
    };

    /// <summary>解析出模型文件的绝对路径。</summary>
    public static string Resolve(string? explicitPath, Logger logger)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            string candidate = Path.GetFullPath(explicitPath);

            if (Directory.Exists(candidate))
            {
                string? fromDirectory = FindInDirectory(candidate, logger);
                if (fromDirectory is null)
                {
                    throw new RmbgException(
                        ExitCode.EnvironmentError,
                        $"目录中未找到 .onnx 模型文件：{candidate}");
                }

                return fromDirectory;
            }

            if (!File.Exists(candidate))
            {
                throw new RmbgException(
                    ExitCode.EnvironmentError,
                    $"指定的模型文件不存在：{candidate}");
            }

            ValidateModelFile(candidate, logger);
            return candidate;
        }

        string? fromEnvironment = Environment.GetEnvironmentVariable(ModelEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            string candidate = Path.GetFullPath(fromEnvironment);
            if (File.Exists(candidate))
            {
                logger.Detail($"模型来源: 环境变量 {ModelEnvironmentVariable}");
                ValidateModelFile(candidate, logger);
                return candidate;
            }

            logger.Warn($"环境变量 {ModelEnvironmentVariable} 指向的文件不存在，已忽略：{candidate}");
        }

        List<string> searched = new();
        foreach (string root in EnumerateSearchRoots())
        {
            foreach (string relative in RelativeSearchDirectories)
            {
                string directory = Path.GetFullPath(Path.Combine(root, relative));
                searched.Add(directory);

                if (!Directory.Exists(directory))
                {
                    continue;
                }

                string? found = FindInDirectory(directory, logger);
                if (found is not null)
                {
                    logger.Detail($"模型来源: 自动搜索 {found}");
                    return found;
                }
            }
        }

        throw new RmbgException(
            ExitCode.EnvironmentError,
            BuildMissingModelMessage(logger, searched));
    }

    private static IEnumerable<string> EnumerateSearchRoots()
    {
        yield return Environment.CurrentDirectory;
        yield return AppContext.BaseDirectory;

        // 从 bin/Debug/net8.0-windows.../win-x64 向上回溯到项目根，方便开发期直接 dotnet run。
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int depth = 0; depth < 9 && directory is not null; depth++)
        {
            yield return directory.FullName;
            directory = directory.Parent;
        }
    }

    private static string? FindInDirectory(string directory, Logger logger)
    {
        foreach (string name in PreferredFileNames)
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                ValidateModelFile(path, logger);
                return Path.GetFullPath(path);
            }
        }

        // 兜底：目录内任意 .onnx，取体积最大者（经验上大文件是完整权重而非量化片段）。
        string? fallback = Directory.EnumerateFiles(directory, "*.onnx", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.Length)
            .Select(info => info.FullName)
            .FirstOrDefault();

        if (fallback is not null)
        {
            ValidateModelFile(fallback, logger);
        }

        return fallback;
    }

    /// <summary>
    /// 排除 Git LFS 指针文件等"看似存在但内容不对"的情况——这是从 Hugging Face /
    /// ModelScope 手工下载时最常见的失败原因。
    /// </summary>
    private static void ValidateModelFile(string path, Logger logger)
    {
        FileInfo info = new(path);

        const long minimumPlausibleSize = 1L * 1024 * 1024;
        if (info.Length < minimumPlausibleSize)
        {
            throw new RmbgException(
                ExitCode.EnvironmentError,
                $"""
                 模型文件体积异常（{info.Length} 字节）：{path}
                 这通常意味着下载到的是 Git LFS 指针或错误的重定向结果，而不是真正的 ONNX 权重。
                 请使用 scripts\\Get-RmbgModel.ps1 重新下载，或改用 ModelScope 的直链：
                 https://www.modelscope.cn/models/AI-ModelScope/RMBG-2.0/resolve/master/onnx/model_fp16.onnx
                 """);
        }

        if (!path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
        {
            logger.Warn($"模型文件扩展名不是 .onnx，仍将尝试加载：{path}");
        }

        // ONNX 文件是 protobuf，首字节应为 0x08（ir_version 字段）。
        try
        {
            using FileStream stream = File.OpenRead(path);
            int first = stream.ReadByte();
            if (first != 0x08)
            {
                logger.Warn($"模型文件头部字节非预期（0x{first:X2}），可能不是标准 ONNX 文件：{path}");
            }
        }
        catch (IOException)
        {
            // 读取失败交给后续 InferenceSession 报错。
        }
    }

    /// <summary>
    /// 构造"未找到模型"的提示。
    ///
    /// 默认版本面向普通用户：只说清"怎么办"，不倾倒内部搜索路径。
    /// 完整搜索列表只在 <c>--verbose</c> 下输出，供排查使用。
    /// </summary>
    private static string BuildMissingModelMessage(Logger logger, IReadOnlyList<string> searched)
    {
        string defaultDirectory = Path.Combine(Environment.CurrentDirectory, "models", "onnx");

        string message = $"""
                          还没有下载模型文件。

                          执行下面任意一条即可：
                            rmbg --download-model       自动下载推荐模型（约 490 MB，只需一次）
                            rmbg                        进入问答模式，按提示操作

                          如果你已经下载了模型放在别处，可以这样指定：
                            rmbg -i 图片.jpg -o 结果.png --model D:\模型\model_fp16.onnx
                          或设置环境变量 {ModelEnvironmentVariable} 指向模型文件。

                          程序会自动查找的目录：{defaultDirectory}
                          （以及程序所在目录下的 models\onnx）
                          """;

        if (logger.Verbose)
        {
            const string separator = "\n  - ";
            message += $"""

                        [verbose] 本次实际搜索的全部位置:{separator}{string.Join(separator, searched.Distinct())}
                        [verbose] 模型下载地址: {ModelCatalog.RepositoryUri}
                        [verbose] 也可用 PowerShell 脚本: scripts\Get-RmbgModel.ps1
                        """;
        }

        return message;
    }
}
