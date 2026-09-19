using RmbgCli.Cli;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RmbgCli.Imaging;

/// <summary>
/// 图像读写。所有写入都采用"临时文件 + 原子替换"，避免失败时留下半截文件。
/// </summary>
internal static class ImageFile
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".tga", ".pbm", ".qoi",
    };

    /// <summary>判断扩展名是否为可解码的图片格式。</summary>
    public static bool HasSupportedExtension(string path)
        => SupportedExtensions.Contains(Path.GetExtension(path));

    /// <summary>输出格式对应的扩展名。</summary>
    public static string GetExtension(OutputFormat format) => format switch
    {
        OutputFormat.Webp => ".webp",
        _ => ".png",
    };

    /// <summary>
    /// 载入图片为 RGBA32，并按 EXIF 方向信息自动摆正，
    /// 确保送模型的张量与最终输出的像素方向一致。
    /// </summary>
    public static Image<Rgba32> Load(string path)
    {
        Image<Rgba32> image = Image.Load<Rgba32>(path);
        try
        {
            image.Mutate(context => context.AutoOrient());
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <summary>保存抠图结果。</summary>
    public static Task SaveAsync(Image<Rgba32> image, string path, OutputFormat format, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);

        return WriteAtomicallyAsync(path, stream => format switch
        {
            OutputFormat.Webp => image.SaveAsWebpAsync(stream, new WebpEncoder { Quality = 95, SkipMetadata = true }, cancellationToken),
            _ => image.SaveAsPngAsync(stream, new PngEncoder { ColorType = PngColorType.RgbWithAlpha, SkipMetadata = true }, cancellationToken),
        });
    }

    /// <summary>保存灰度蒙版（8bit PNG）。</summary>
    public static Task SaveMaskAsync(Image<L8> mask, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mask);

        return WriteAtomicallyAsync(
            path,
            stream => mask.SaveAsPngAsync(stream, new PngEncoder { ColorType = PngColorType.Grayscale, SkipMetadata = true }, cancellationToken));
    }

    /// <summary>
    /// 在输出开始前做一次目标目录可写性检查，避免处理完所有图片才发现无法落盘。
    /// </summary>
    public static void EnsureOutputDirectoryWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string probe = Path.Combine(directory, $".rmbg-write-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new RmbgException(ExitCode.IoError, $"输出目录不可写：{directory}（{ex.Message}）", ex);
        }
    }

    private static async Task WriteAtomicallyAsync(string path, Func<Stream, Task> write)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new RmbgException(ExitCode.IoError, $"无法解析输出路径的目录部分：{path}");

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new RmbgException(ExitCode.IoError, $"无法创建输出目录：{directory}（{ex.Message}）", ex);
        }

        string tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (FileStream stream = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await write(stream).ConfigureAwait(false);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            throw new RmbgException(ExitCode.IoError, $"写入输出文件失败：{fullPath}（{ex.Message}）", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响主流程。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }
}
