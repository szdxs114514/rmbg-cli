using System.Diagnostics;
using RmbgCli.Cli;
using RmbgCli.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RmbgCli.Core;

/// <summary>单张图片的处理结果与耗时分解。</summary>
internal sealed record ProcessingResult(
    string InputPath,
    string OutputPath,
    string? MaskPath,
    int Width,
    int Height,
    TimeSpan PreprocessTime,
    TimeSpan InferenceTime,
    TimeSpan PostprocessTime,
    MaskStatistics Statistics)
{
    public TimeSpan TotalTime => PreprocessTime + InferenceTime + PostprocessTime;
}

/// <summary>
/// 单张图片的抠图流程编排：载入 → 预处理 → 推理 → 蒙版后处理 → 合成 → 落盘。
/// </summary>
internal sealed class BackgroundRemover
{
    private readonly RmbgSession _session;
    private readonly AppOptions _options;
    private readonly Logger _logger;
    private readonly int _inputSize;

    public BackgroundRemover(RmbgSession session, AppOptions options, Logger logger, int inputSize)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _inputSize = inputSize;
    }

    /// <summary>处理一张图片。</summary>
    public async Task<ProcessingResult> ProcessAsync(ProcessingJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        Image<Rgba32> source = LoadSource(job.InputPath);

        try
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            // ---- 1. 预处理：缩放 → 归一化 → NCHW ----
            float[] nchw = RmbgPreprocessor.ToNchwFloat(source, _inputSize);
            TimeSpan preprocessTime = stopwatch.Elapsed;
            cancellationToken.ThrowIfCancellationRequested();

            // ---- 2. 推理 ----
            stopwatch.Restart();
            float[] mask = _session.Run(nchw, _inputSize);
            TimeSpan inferenceTime = stopwatch.Elapsed;
            cancellationToken.ThrowIfCancellationRequested();

            // ---- 3. 后处理：量化 → 回缩放到原图尺寸 → 二值化/羽化 → 合成 ----
            stopwatch.Restart();

            int maskSize = ResolveMaskSize(mask.Length);
            MaskStatistics statistics = RmbgPostprocessor.ComputeStatistics(mask);

            using Image<L8> rawMask = RmbgPostprocessor.CreateMaskImage(mask, maskSize);
            using Image<L8> scaledMask = RmbgPostprocessor.ResizeMask(rawMask, source.Width, source.Height, _options.MaskResampler);
            using Image<L8> refinedMask = RmbgPostprocessor.RefineMask(
                scaledMask,
                new MaskRefineOptions(_options.Threshold, _options.Feather, _options.Invert),
                _logger);

            using Image<Rgba32> composed = RmbgPostprocessor.Compose(source, refinedMask, _options.Background, _logger);

            await ImageFile.SaveAsync(composed, job.OutputPath, _options.Format, cancellationToken).ConfigureAwait(false);

            if (job.MaskPath is not null)
            {
                await ImageFile.SaveMaskAsync(refinedMask, job.MaskPath, cancellationToken).ConfigureAwait(false);
            }

            TimeSpan postprocessTime = stopwatch.Elapsed;

            return new ProcessingResult(
                job.InputPath,
                job.OutputPath,
                job.MaskPath,
                source.Width,
                source.Height,
                preprocessTime,
                inferenceTime,
                postprocessTime,
                statistics);
        }
        finally
        {
            source.Dispose();
        }
    }

    private Image<Rgba32> LoadSource(string path)
    {
        try
        {
            return ImageFile.Load(path);
        }
        catch (FileNotFoundException ex)
        {
            throw new RmbgException(ExitCode.IoError, $"输入图片不存在：{path}", ex);
        }
        catch (UnknownImageFormatException ex)
        {
            throw new RmbgException(
                ExitCode.IoError,
                $"无法识别的图片格式或文件已损坏：{path}（{ex.Message}）",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new RmbgException(ExitCode.IoError, $"没有读取权限：{path}", ex);
        }
        catch (IOException ex)
        {
            throw new RmbgException(ExitCode.IoError, $"读取图片失败：{path}（{ex.Message}）", ex);
        }
        catch (ImageFormatException ex)
        {
            throw new RmbgException(ExitCode.IoError, $"图片解码失败：{path}（{ex.Message}）", ex);
        }
    }

    /// <summary>由输出张量元素个数反推方形蒙版边长。</summary>
    private int ResolveMaskSize(int maskLength)
    {
        int size = (int)Math.Round(Math.Sqrt(maskLength), MidpointRounding.AwayFromZero);

        if (size * size != maskLength)
        {
            throw new RmbgException(
                ExitCode.InferenceError,
                $"模型输出蒙版不是正方形张量（元素个数 {maskLength}，无法开平方），" +
                "请确认使用的是 RMBG-2.0 的标准导出版本。");
        }

        if (size != _inputSize)
        {
            _logger.Detail($"注意：模型输出边长 {size} 与输入边长 {_inputSize} 不一致，已按输出尺寸处理。");
        }

        return size;
    }
}
