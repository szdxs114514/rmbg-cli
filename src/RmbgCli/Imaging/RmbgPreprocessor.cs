using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RmbgCli.Imaging;

/// <summary>
/// RMBG-2.0 输入张量构造。
///
/// 与官方 Python 参考实现保持一致的预处理链路（参考模型仓库的 preprocessor_config.json）：
///   do_resize   : 等比拉伸到 1024x1024（resample=2 → PIL BILINEAR → ImageSharp Triangle）
///   do_rescale  : 像素值 * 1/255
///   do_normalize: (x - mean) / std，mean=[0.485,0.456,0.406]，
///                 std=[0.229,0.224,0.225]（ImageNet 统计量）
///   do_pad      : 无
/// 通道顺序为 RGB，张量布局为 NCHW（1,3,H,W）float32。
/// </summary>
internal static class RmbgPreprocessor
{
    private const float RescaleFactor = 1f / 255f;

    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };

    // 预先取倒数，把每次推理的 300 万次除法降为乘法。
    private static readonly float[] InverseStd = { 1f / 0.229f, 1f / 0.224f, 1f / 0.225f };

    /// <summary>通道数（RMBG-2.0 固定为 RGB 三通道）。</summary>
    public const int Channels = 3;

    /// <summary>
    /// 将源图像转换为模型输入张量数据（NCHW，float32）。
    /// </summary>
    /// <param name="source">已应用 EXIF 方向的源图像。</param>
    /// <param name="size">模型输入边长（正方形）。</param>
    /// <returns>长度为 <c>3 * size * size</c> 的一维数组，按 [C][H][W] 连续排列。</returns>
    public static float[] ToNchwFloat(Image<Rgba32> source, int size)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);

        // 尺寸已经匹配时跳过重采样，避免无谓的插值损失与内存拷贝。
        Image<Rgba32>? resized = null;
        Image<Rgba32> working = source;
        if (source.Width != size || source.Height != size)
        {
            resized = source.Clone(context => context.Resize(new ResizeOptions
            {
                Size = new Size(size, size),
                Mode = ResizeMode.Stretch,          // 官方实现直接 resize 到 (1024,1024)，不保持宽高比
                Sampler = KnownResamplers.Triangle, // Triangle ≈ PIL BILINEAR
            }));
            working = resized;
        }

        try
        {
            int plane = size * size;
            float[] buffer = new float[Channels * plane];

            working.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < size; y++)
                {
                    Span<Rgba32> row = accessor.GetRowSpan(y);
                    int rowOffset = y * size;

                    for (int x = 0; x < size; x++)
                    {
                        Rgba32 pixel = row[x];
                        int index = rowOffset + x;

                        // NCHW 布局：先写满 R 平面，再写 G、B 平面。
                        buffer[index] = ((pixel.R * RescaleFactor) - Mean[0]) * InverseStd[0];
                        buffer[plane + index] = ((pixel.G * RescaleFactor) - Mean[1]) * InverseStd[1];
                        buffer[(2 * plane) + index] = ((pixel.B * RescaleFactor) - Mean[2]) * InverseStd[2];
                    }
                }
            });

            return buffer;
        }
        finally
        {
            resized?.Dispose();
        }
    }
}
