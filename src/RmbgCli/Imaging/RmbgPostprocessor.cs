using RmbgCli.Cli;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;

namespace RmbgCli.Imaging;

/// <summary>蒙版后处理参数。</summary>
internal readonly record struct MaskRefineOptions(
    float Threshold,
    float Feather,
    bool Invert);

/// <summary>蒙版统计信息，用于用户侧判断抠图结果是否合理。</summary>
internal readonly record struct MaskStatistics(
    float Min,
    float Max,
    float Mean,
    double CoverageRatio)
{
    public override string ToString()
        => $"min={Min:F3} max={Max:F3} mean={Mean:F3} 前景占比={CoverageRatio:P1}";
}

/// <summary>
/// 模型输出蒙版的后处理与合成。
///
/// 重要：本仓库使用的 RMBG-2.0 ONNX 导出图，其末端节点已经是
/// <c>Sigmoid</c>（fp16/q4 变体后接 <c>Cast(to=FLOAT)</c>），输出张量
/// <c>alphas</c> 已经是 [0,1] 区间的前景概率。**不要再次施加 sigmoid**，
/// 否则会压缩动态范围、显著劣化边缘。
///
/// 处理链路：
///   模型输出 float[0,1] → 8bit 量化（对齐 PIL ToPILImage 的行为）
///   → 重采样回原图尺寸（默认 bicubic，对齐 PIL Image.resize 默认值）
///   → 可选二值化 / 反转 / 羽化
///   → 与原图合成，写入 Alpha 通道
/// </summary>
internal static class RmbgPostprocessor
{
    private const int MinMaskValue = 0;
    private const int MaxMaskValue = 255;

    /// <summary>把模型输出的 [0,1] 浮点蒙版量化为 8bit 灰度蒙版图像。</summary>
    public static Image<L8> CreateMaskImage(ReadOnlySpan<float> mask, int size)
    {
        byte[] gray = Quantize(mask);
        return Image.LoadPixelData<L8>(gray, size, size);
    }

    /// <summary>把蒙版重采样到目标尺寸（拉伸填充，不保持宽高比）。</summary>
    public static Image<L8> ResizeMask(Image<L8> mask, int width, int height, MaskResampler resampler)
    {
        ArgumentNullException.ThrowIfNull(mask);

        if (mask.Width == width && mask.Height == height)
        {
            return mask.Clone();
        }

        return mask.Clone(context => context.Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = ResizeMode.Stretch,
            Sampler = ToSampler(resampler),
        }));
    }

    /// <summary>按需执行反转、二值化与羽化，返回新的蒙版图像。</summary>
    public static Image<L8> RefineMask(Image<L8> mask, MaskRefineOptions options, Logger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(mask);

        bool needInvert = options.Invert;
        bool needThreshold = options.Threshold > 0f;
        bool needFeather = options.Feather > 0f;

        if (!needInvert && !needThreshold && !needFeather)
        {
            return mask.Clone();
        }

        int width = mask.Width;
        int height = mask.Height;
        byte[] data = new byte[width * height];
        mask.CopyPixelDataTo(data);

        if (needInvert)
        {
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(MaxMaskValue - data[i]);
            }
        }

        if (needThreshold)
        {
            byte threshold = (byte)Math.Clamp(
                (int)MathF.Round(options.Threshold * MaxMaskValue, MidpointRounding.AwayFromZero),
                MinMaskValue,
                MaxMaskValue);

            for (int i = 0; i < data.Length; i++)
            {
                data[i] = data[i] >= threshold ? (byte)MaxMaskValue : (byte)MinMaskValue;
            }
        }

        if (needFeather)
        {
            int radius = Math.Max(1, (int)MathF.Round(options.Feather, MidpointRounding.AwayFromZero));
            data = BoxBlur3Pass(data, width, height, radius);
            logger?.Detail($"    羽化: 半径 {radius}px（3 次盒式滤波近似高斯）");
        }

        return Image.LoadPixelData<L8>(data, width, height);
    }

    /// <summary>
    /// 将蒙版作为 Alpha 通道与原图合成。
    /// </summary>
    /// <param name="source">原图（含可能已存在的 Alpha）。</param>
    /// <param name="mask">与 source 同尺寸的前景蒙版。</param>
    /// <param name="background">为 null 时输出透明背景；否则把前景合成到该纯色上。</param>
    /// <param name="logger">详细日志。</param>
    public static Image<Rgba32> Compose(Image<Rgba32> source, Image<L8> mask, Rgba32? background, Logger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mask);

        if (mask.Width != source.Width || mask.Height != source.Height)
        {
            throw new ArgumentException(
                $"蒙版尺寸 {mask.Width}x{mask.Height} 与原图尺寸 {source.Width}x{source.Height} 不一致。",
                nameof(mask));
        }

        Image<Rgba32> result = source.Clone();

        result.ProcessPixelRows(mask, (sourceAccessor, maskAccessor) =>
        {
            for (int y = 0; y < sourceAccessor.Height; y++)
            {
                Span<Rgba32> sourceRow = sourceAccessor.GetRowSpan(y);
                Span<L8> maskRow = maskAccessor.GetRowSpan(y);

                for (int x = 0; x < sourceRow.Length; x++)
                {
                    Rgba32 pixel = sourceRow[x];

                    // 蒙版值与原图已有 Alpha 相乘，保证重复处理半透明素材时不会"补回"透明区域。
                    int alpha = maskRow[x].PackedValue * pixel.A / MaxMaskValue;

                    if (background is { } backgroundPixel)
                    {
                        int inverse = MaxMaskValue - alpha;
                        sourceRow[x] = new Rgba32(
                            (byte)(((pixel.R * alpha) + (backgroundPixel.R * inverse)) / MaxMaskValue),
                            (byte)(((pixel.G * alpha) + (backgroundPixel.G * inverse)) / MaxMaskValue),
                            (byte)(((pixel.B * alpha) + (backgroundPixel.B * inverse)) / MaxMaskValue),
                            backgroundPixel.A);
                    }
                    else
                    {
                        sourceRow[x] = new Rgba32(pixel.R, pixel.G, pixel.B, (byte)alpha);
                    }
                }
            }
        });

        logger?.Detail(background is null ? "    合成: 透明背景 (RGBA PNG)" : "    合成: 纯色背景");
        return result;
    }

    /// <summary>计算蒙版统计信息，帮助判断推理是否正常（例如输出恒定 0 说明模型或预处理有误）。</summary>
    public static MaskStatistics ComputeStatistics(ReadOnlySpan<float> mask)
    {
        if (mask.Length == 0)
        {
            return new MaskStatistics(0f, 0f, 0f, 0d);
        }

        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;
        double sum = 0d;
        long foreground = 0;

        foreach (float value in mask)
        {
            float v = float.IsNaN(value) ? 0f : Math.Clamp(value, 0f, 1f);
            if (v < min) { min = v; }
            if (v > max) { max = v; }
            sum += v;
            if (v >= 0.5f) { foreground++; }
        }

        return new MaskStatistics(min, max, (float)(sum / mask.Length), (double)foreground / mask.Length);
    }

    private static byte[] Quantize(ReadOnlySpan<float> mask)
    {
        byte[] gray = new byte[mask.Length];
        for (int i = 0; i < mask.Length; i++)
        {
            float value = mask[i];
            if (float.IsNaN(value))
            {
                value = 0f;
            }

            int quantized = (int)MathF.Round(
                Math.Clamp(value, 0f, 1f) * MaxMaskValue,
                MidpointRounding.AwayFromZero);

            gray[i] = (byte)Math.Clamp(quantized, MinMaskValue, MaxMaskValue);
        }

        return gray;
    }

    private static IResampler ToSampler(MaskResampler resampler) => resampler switch
    {
        MaskResampler.Bilinear => KnownResamplers.Triangle,
        MaskResampler.Nearest => KnownResamplers.NearestNeighbor,
        _ => KnownResamplers.Bicubic,
    };

    /// <summary>
    /// 3 次盒式滤波近似高斯模糊（边缘钳制），对 8bit 蒙版做 O(n) 可分离卷积。
    /// 综合方差约为单次盒式滤波的 3 倍，等效 σ ≈ 1.2 × radius。
    /// </summary>
    private static byte[] BoxBlur3Pass(byte[] source, int width, int height, int radius)
    {
        byte[] current = source;
        byte[] horizontal = new byte[source.Length];
        byte[] vertical = new byte[source.Length];

        for (int pass = 0; pass < 3; pass++)
        {
            BoxBlurHorizontal(current, horizontal, width, height, radius);
            BoxBlurVertical(horizontal, vertical, width, height, radius);
            current = vertical;

            // 下一轮写入新缓冲，避免读写同一数组。
            vertical = new byte[source.Length];
        }

        return current;
    }

    private static void BoxBlurHorizontal(byte[] source, byte[] destination, int width, int height, int radius)
    {
        int window = (2 * radius) + 1;

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * width;
            long sum = 0;

            for (int k = -radius; k <= radius; k++)
            {
                sum += source[rowStart + Math.Clamp(k, 0, width - 1)];
            }

            for (int x = 0; x < width; x++)
            {
                destination[rowStart + x] = (byte)((sum + (window / 2)) / window);
                sum += source[rowStart + Math.Clamp(x + radius + 1, 0, width - 1)];
                sum -= source[rowStart + Math.Clamp(x - radius, 0, width - 1)];
            }
        }
    }

    private static void BoxBlurVertical(byte[] source, byte[] destination, int width, int height, int radius)
    {
        int window = (2 * radius) + 1;

        for (int x = 0; x < width; x++)
        {
            long sum = 0;

            for (int k = -radius; k <= radius; k++)
            {
                sum += source[(Math.Clamp(k, 0, height - 1) * width) + x];
            }

            for (int y = 0; y < height; y++)
            {
                destination[(y * width) + x] = (byte)((sum + (window / 2)) / window);
                sum += source[(Math.Clamp(y + radius + 1, 0, height - 1) * width) + x];
                sum -= source[(Math.Clamp(y - radius, 0, height - 1) * width) + x];
            }
        }
    }
}
