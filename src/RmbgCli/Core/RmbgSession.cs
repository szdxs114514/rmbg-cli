using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RmbgCli.Cli;
using RmbgCli.Imaging;

namespace RmbgCli.Core;

/// <summary>模型的输入/输出张量描述。</summary>
/// <remarks>
/// 注意：NodeMetadata.ElementType 暴露的是 CLR 类型（System.Type），不是 TensorElementType 枚举。
/// </remarks>
internal sealed record TensorDescriptor(string Name, int[] Dimensions, Type ElementType)
{
    public bool HasDynamicShape => Array.Exists(Dimensions, static d => d <= 0);

    public string ShapeText => "[" + string.Join(", ", Dimensions.Select(static d => d <= 0 ? "N" : d.ToString())) + "]";

    public string ElementTypeText => DescribeElementType(ElementType);

    /// <summary>把 CLR 类型映射成 ONNX 语境下可读的元素类型名。</summary>
    public static string DescribeElementType(Type type) => type switch
    {
        _ when type == typeof(float) => "Float32",
        _ when type == typeof(double) => "Float64",
        _ when type == typeof(Half) => "Float16",
        _ when type == typeof(byte) => "UInt8",
        _ when type == typeof(sbyte) => "Int8",
        _ when type == typeof(short) => "Int16",
        _ when type == typeof(ushort) => "UInt16",
        _ when type == typeof(int) => "Int32",
        _ when type == typeof(long) => "Int64",
        _ => type.Name,
    };
}

/// <summary>已解析的模型信息。</summary>
internal sealed record ModelDescriptor(
    TensorDescriptor Input,
    TensorDescriptor Output,
    int? FixedInputSize,
    string ModelPath)
{
    public int? FixedOutputSize => Output.Dimensions.Length == 4 && Output.Dimensions[2] > 0 && Output.Dimensions[3] > 0
        ? Output.Dimensions[2]
        : null;
}

/// <summary>
/// 封装 InferenceSession：负责执行提供程序回退、张量元数据校验与推理调用。
/// </summary>
internal sealed class RmbgSession : IDisposable
{
    private readonly InferenceSession _session;

    private RmbgSession(InferenceSession session, ModelDescriptor model, ExecutionProviderChoice provider)
    {
        _session = session;
        Model = model;
        Provider = provider;
    }

    public ModelDescriptor Model { get; }

    public ExecutionProviderChoice Provider { get; }

    /// <summary>
    /// 图优化级别尝试阶梯。
    ///
    /// 必须从 ORT_ENABLE_ALL 降级到 ORT_ENABLE_BASIC 的原因：float16 导出模型里含有
    /// float16 转换器插入的 <c>InsertedPrecisionFreeCast_*</c> 节点，ORT 的
    /// <c>SimplifiedLayerNormFusion</c>（CPU EP 的扩展优化项）会因找不到这些常量名而
    /// 在会话初始化阶段直接失败：
    ///   "GetIndexFromName ... a name which does not exist: InsertedPrecisionFreeCast_..."
    /// 降级到 BASIC 可跳过该融合，使 fp16 模型在 CPU 上也能正常推理。
    /// </summary>
    private static readonly GraphOptimizationLevel[] OptimizationLadder =
    {
        GraphOptimizationLevel.ORT_ENABLE_ALL,
        GraphOptimizationLevel.ORT_ENABLE_BASIC,
    };

    /// <summary>
    /// 依次尝试候选执行提供程序创建会话；第一个成功者生效。
    /// 每个候选内部还会按 <see cref="OptimizationLadder"/> 逐级降低图优化。
    /// 记录所有失败原因，全部失败时汇总抛出。
    /// </summary>
    public static RmbgSession Create(
        string modelPath,
        IReadOnlyList<ExecutionProviderChoice> candidates,
        int? threads,
        Logger logger,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            throw new RmbgException(ExitCode.EnvironmentError, "没有可用的执行提供程序候选。");
        }

        List<string> failures = new();

        foreach (ExecutionProviderChoice candidate in candidates)
        {
            foreach (GraphOptimizationLevel optimizationLevel in OptimizationLadder)
            {
                cancellationToken.ThrowIfCancellationRequested();

                SessionOptions options = new()
                {
                    GraphOptimizationLevel = optimizationLevel,
                    EnableMemoryPattern = false,

                    // RMBG-2.0 的动态维度导出会让 ORT 的内存规划器打印大量
                    // "Shape mismatch attempting to re-use buffer" 警告（仅影响缓冲区复用优化，
                    // 不影响正确性）。默认屏蔽运行时日志，避免污染命令行输出；
                    // --verbose 时放开到 WARNING 便于排查。
                    // 注意：异常信息本身不受影响，仍会通过 OnnxRuntimeException.Message 完整给出。
                    LogSeverityLevel = logger.Verbose
                        ? OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
                        : OrtLoggingLevel.ORT_LOGGING_LEVEL_FATAL,
                };

                if (threads is > 0)
                {
                    options.IntraOpNumThreads = threads.Value;
                    options.InterOpNumThreads = 1;
                }

                try
                {
                    candidate.Configure(options);

                    logger.Detail($"尝试创建会话：{candidate.DisplayName} ({candidate.Detail}), 图优化 {optimizationLevel} …");
                    InferenceSession session = new(modelPath, options);
                    ModelDescriptor model = Inspect(session, modelPath);

                    if (optimizationLevel != GraphOptimizationLevel.ORT_ENABLE_ALL)
                    {
                        logger.Warn($"图优化已降级为 {optimizationLevel}（原因：模型与 ORT 扩展优化不兼容），性能可能略低。");
                    }

                    logger.Info($"执行提供程序: {candidate.DisplayName} ({candidate.Detail})");
                    return new RmbgSession(session, model, candidate);
                }
                catch (OperationCanceledException)
                {
                    options.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    options.Dispose();

                    string reason = ex.Message;
                    logger.Detail($"  失败（图优化 {optimizationLevel}）：{reason}");

                    // 扩展优化失败时先降级重试同一 EP，而不是直接判定该 EP 不可用。
                    if (optimizationLevel == GraphOptimizationLevel.ORT_ENABLE_ALL
                        && LooksLikeGraphOptimizationFailure(ex))
                    {
                        continue;
                    }

                    failures.Add($"{candidate.DisplayName} ({candidate.Detail}): {reason}");

                    // 模型本身无法加载时，换 EP 也不会成功，直接给出明确反馈。
                    if (IsModelLevelFailure(ex))
                    {
                        throw BuildLoadFailure(modelPath, ex, candidate);
                    }

                    // 该 EP 不可用，换下一个候选。
                    break;
                }
            }
        }

        throw new RmbgException(
            ExitCode.EnvironmentError,
            "所有执行提供程序都创建会话失败：" + Environment.NewLine +
            "  - " + string.Join(Environment.NewLine + "  - ", failures) + Environment.NewLine +
            "可尝试 `--ep cpu` 强制使用 CPU，或运行 `--ep list` 检查环境。");
    }

    /// <summary>识别 ORT 图优化（而非模型本身）导致的初始化失败。</summary>
    private static bool LooksLikeGraphOptimizationFailure(Exception ex)
    {
        string message = ex.Message;

        return message.Contains("graph_utils", StringComparison.OrdinalIgnoreCase)
            || message.Contains("GetIndexFromName", StringComparison.OrdinalIgnoreCase)
            || message.Contains("InsertedPrecisionFreeCast", StringComparison.Ordinal)
            || message.Contains("Exception during initialization", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 执行一次推理。
    /// </summary>
    /// <param name="nchw">预处理后的 NCHW float 数据，长度必须为 3*size*size。</param>
    /// <param name="size">输入边长。</param>
    /// <returns>输出蒙版数据（已按通道/批展平，长度与输出张量一致）。</returns>
    public float[] Run(float[] nchw, int size)
    {
        ArgumentNullException.ThrowIfNull(nchw);

        int expected = RmbgPreprocessor.Channels * size * size;
        if (nchw.Length != expected)
        {
            throw new RmbgException(
                ExitCode.InferenceError,
                $"输入张量长度不正确：期望 {expected}，实际 {nchw.Length}。");
        }

        DenseTensor<float> tensor = new(nchw, new[] { 1, RmbgPreprocessor.Channels, size, size });

        // NamedOnnxValue 本身不实现 IDisposable；其生命周期由 Run 的返回集合管理。
        NamedOnnxValue input = NamedOnnxValue.CreateFromTensor(Model.Input.Name, tensor);

        try
        {
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
                _session.Run(new[] { input }, new[] { Model.Output.Name });

            if (results.Count == 0)
            {
                throw new RmbgException(ExitCode.InferenceError, "推理返回了空结果集。");
            }

            Tensor<float> output = results[0].AsTensor<float>();

            // 注意：Tensor<T> 的 this[params int[]] 是"多维索引"，对 [1,1,H,W] 张量用单个下标
            // 会被解释为第 0 维下标而越界。这里通过 IList<T> 接口做扁平索引。
            IList<float> flat = output;
            float[] mask = new float[flat.Count];
            for (int i = 0; i < mask.Length; i++)
            {
                mask[i] = flat[i];
            }

            return mask;
        }
        catch (OnnxRuntimeException ex)
        {
            throw new RmbgException(
                ExitCode.InferenceError,
                $"推理失败（{Provider.DisplayName}）：{ex.Message}" + Environment.NewLine +
                "如为显存/内存不足，可尝试降低 --size 或改用 --ep cpu。",
                ex);
        }
    }

    public void Dispose() => _session.Dispose();

    /// <summary>读取并校验模型的输入/输出元数据。</summary>
    private static ModelDescriptor Inspect(InferenceSession session, string modelPath)
    {
        IReadOnlyDictionary<string, NodeMetadata> inputs = session.InputMetadata;
        IReadOnlyDictionary<string, NodeMetadata> outputs = session.OutputMetadata;

        if (inputs.Count == 0)
        {
            throw new RmbgException(ExitCode.EnvironmentError, $"模型没有输入张量：{modelPath}");
        }

        KeyValuePair<string, NodeMetadata> inputEntry = SelectInput(inputs);
        KeyValuePair<string, NodeMetadata> outputEntry = SelectOutput(outputs);

        TensorDescriptor input = new(inputEntry.Key, inputEntry.Value.Dimensions, inputEntry.Value.ElementType);
        TensorDescriptor output = new(outputEntry.Key, outputEntry.Value.Dimensions, outputEntry.Value.ElementType);

        if (input.ElementType != typeof(float))
        {
            throw new RmbgException(
                ExitCode.EnvironmentError,
                $"模型输入张量 '{input.Name}' 的元素类型为 {input.ElementTypeText}，期望 Float32。");
        }

        if (output.ElementType != typeof(float))
        {
            throw new RmbgException(
                ExitCode.EnvironmentError,
                $"模型输出张量 '{output.Name}' 的元素类型为 {output.ElementTypeText}，期望 Float32。" + Environment.NewLine +
                "RMBG-2.0 的标准导出会在 Sigmoid 后 Cast 回 Float32；若使用自转换的模型，" +
                "请在导出时保持输出为 float32，或改用官方 ModelScope 权重。");
        }

        if (input.Dimensions.Length == 4 && input.Dimensions[1] > 0 && input.Dimensions[1] != RmbgPreprocessor.Channels)
        {
            throw new RmbgException(
                ExitCode.EnvironmentError,
                $"模型输入通道数为 {input.Dimensions[1]}，本程序按 RGB 三通道预处理，二者不匹配。");
        }

        int? fixedSize = input.Dimensions.Length == 4 && input.Dimensions[2] > 0 && input.Dimensions[3] > 0
            ? input.Dimensions[2]
            : null;

        return new ModelDescriptor(input, output, fixedSize, modelPath);
    }

    /// <summary>优先选择名为 pixel_values 的输入，其次选 4 维张量。</summary>
    private static KeyValuePair<string, NodeMetadata> SelectInput(IReadOnlyDictionary<string, NodeMetadata> inputs)
    {
        foreach (KeyValuePair<string, NodeMetadata> entry in inputs)
        {
            if (entry.Key.Contains("pixel", StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        foreach (KeyValuePair<string, NodeMetadata> entry in inputs)
        {
            if (entry.Value.Dimensions.Length == 4)
            {
                return entry;
            }
        }

        return inputs.First();
    }

    /// <summary>优先选择名为 alphas 的输出，其次选 4 维单通道张量。</summary>
    private static KeyValuePair<string, NodeMetadata> SelectOutput(IReadOnlyDictionary<string, NodeMetadata> outputs)
    {
        foreach (KeyValuePair<string, NodeMetadata> entry in outputs)
        {
            if (entry.Key.Equals("alphas", StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        foreach (KeyValuePair<string, NodeMetadata> entry in outputs)
        {
            int[] dims = entry.Value.Dimensions;
            if (dims.Length == 4 && (dims[1] == 1 || dims[1] <= 0))
            {
                return entry;
            }
        }

        return outputs.First();
    }

    /// <summary>判断异常是否说明模型文件本身有问题（换 EP 无意义）。</summary>
    private static bool IsModelLevelFailure(Exception ex)
        => ex is FileNotFoundException or FileLoadException or BadImageFormatException
           || ex is OnnxRuntimeException ort && ort.Message.Contains("load", StringComparison.OrdinalIgnoreCase);

    private static RmbgException BuildLoadFailure(string modelPath, Exception ex, ExecutionProviderChoice candidate)
    {
        string hint = ex switch
        {
            FileNotFoundException => "模型文件不存在。",
            BadImageFormatException => "模型文件不是有效的 ONNX 文件（可能下载不完整或被截断）。",
            UnauthorizedAccessException => "没有读取模型文件的权限。",
            OutOfMemoryException => "内存不足，无法载入模型。",
            _ => "加载模型失败。",
        };

        return new RmbgException(
            ExitCode.EnvironmentError,
            $"{hint}{Environment.NewLine}模型路径: {modelPath}{Environment.NewLine}" +
            $"执行提供程序: {candidate.DisplayName}{Environment.NewLine}原始错误: {ex.Message}",
            ex);
    }
}
