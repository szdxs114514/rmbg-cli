using RmbgCli.Cli;
using RmbgCli.Imaging;

namespace RmbgCli.Core;

/// <summary>一张待处理的图片及其输出目标。</summary>
internal sealed record ProcessingJob(string InputPath, string OutputPath, string? MaskPath);

/// <summary>被跳过的输入及其原因。</summary>
internal sealed record SkippedItem(string InputPath, string Reason);

/// <summary>批量任务的展开结果。</summary>
internal sealed record BatchPlan(
    IReadOnlyList<ProcessingJob> Jobs,
    IReadOnlyList<SkippedItem> Skipped,
    IReadOnlyList<string> Problems);

/// <summary>
/// 把命令行给出的输入（文件、目录、通配符）展开为具体的处理任务，
/// 并推导每个任务的输出路径。
/// </summary>
internal static class BatchPlanner
{
    private static readonly char[] WildcardCharacters = { '*', '?' };

    public static BatchPlan Build(AppOptions options, Logger logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> problems = new();
        List<SkippedItem> skipped = new();

        List<(string Path, string Root)> discovered = ExpandInputs(options, logger, problems);

        if (discovered.Count == 0)
        {
            return new BatchPlan(Array.Empty<ProcessingJob>(), skipped, problems);
        }

        string outputArgument = Path.GetFullPath(options.Output!);
        bool single = discovered.Count == 1;
        bool outputIsExplicitFile = single && !Directory.Exists(outputArgument) && LooksLikeFilePath(outputArgument);

        List<ProcessingJob> jobs = new();

        foreach ((string path, string root) in discovered)
        {
            string outputPath = outputIsExplicitFile
                ? outputArgument
                : BuildOutputPath(outputArgument, path, root, options);

            string? maskPath = options.MaskOutput == MaskOutputMode.None
                ? null
                : BuildMaskPath(outputPath, options);

            if (string.Equals(outputPath, path, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"输出路径与输入相同，已跳过（请修改 --suffix 或 -o）：{path}");
                continue;
            }

            if (maskPath is not null && string.Equals(maskPath, path, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"蒙版输出路径与输入相同，已跳过：{path}");
                continue;
            }

            if (!options.Overwrite)
            {
                if (File.Exists(outputPath))
                {
                    skipped.Add(new SkippedItem(path, $"输出已存在（加 --overwrite 可覆盖）：{outputPath}"));
                    continue;
                }

                if (maskPath is not null && File.Exists(maskPath))
                {
                    skipped.Add(new SkippedItem(path, $"蒙版已存在（加 --overwrite 可覆盖）：{maskPath}"));
                    continue;
                }
            }

            jobs.Add(new ProcessingJob(path, outputPath, maskPath));
        }

        if (!outputIsExplicitFile)
        {
            ImageFile.EnsureOutputDirectoryWritable(outputArgument);
        }
        else if (jobs.Count > 0)
        {
            ImageFile.EnsureOutputDirectoryWritable(
                Path.GetDirectoryName(jobs[0].OutputPath) ?? outputArgument);
        }

        logger.Detail($"输出目录: {(outputIsExplicitFile ? Path.GetDirectoryName(jobs.FirstOrDefault()?.OutputPath ?? outputArgument) : outputArgument)}");

        return new BatchPlan(jobs, skipped, problems);
    }

    private static List<(string Path, string Root)> ExpandInputs(
        AppOptions options,
        Logger logger,
        List<string> problems)
    {
        List<(string Path, string Root)> results = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in options.Inputs)
        {
            string trimmed = raw.Trim().Trim('"');

            if (trimmed.IndexOfAny(WildcardCharacters) >= 0)
            {
                ExpandWildcard(trimmed, options, logger, problems, results, seen);
                continue;
            }

            string full = Path.GetFullPath(trimmed);

            if (Directory.Exists(full))
            {
                SearchOption searchOption = options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                List<string> files = Directory.EnumerateFiles(full, "*", searchOption)
                    .Where(ImageFile.HasSupportedExtension)
                    .OrderBy(static f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (files.Count == 0)
                {
                    problems.Add($"目录中没有可处理的图片：{full}" +
                                 (options.Recursive ? string.Empty : "（可加 --recursive 搜索子目录）"));
                    continue;
                }

                foreach (string file in files)
                {
                    AddResult(file, full, results, seen);
                }

                continue;
            }

            if (File.Exists(full))
            {
                if (!ImageFile.HasSupportedExtension(full))
                {
                    problems.Add($"不支持的图片格式：{full}");
                    continue;
                }

                AddResult(full, Path.GetDirectoryName(full) ?? Environment.CurrentDirectory, results, seen);
                continue;
            }

            problems.Add($"输入路径不存在：{full}");
        }

        return results;
    }

    private static void ExpandWildcard(
        string pattern,
        AppOptions options,
        Logger logger,
        List<string> problems,
        List<(string Path, string Root)> results,
        HashSet<string> seen)
    {
        string directory = Path.GetDirectoryName(pattern) is { Length: > 0 } dir
            ? Path.GetFullPath(dir)
            : Environment.CurrentDirectory;
        string searchPattern = Path.GetFileName(pattern);

        if (!Directory.Exists(directory))
        {
            problems.Add($"通配符对应的目录不存在：{directory}");
            return;
        }

        if (!searchPattern.Contains('.') )
        {
            // `*.` 未给出扩展名时，后面仍会用支持的扩展名过滤。
            logger.Detail($"通配符 '{searchPattern}' 未限定扩展名，将只处理受支持的图片格式。");
        }

        SearchOption searchOption = options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        List<string> matches;
        try
        {
            matches = Directory.EnumerateFiles(directory, searchPattern, searchOption)
                .Where(ImageFile.HasSupportedExtension)
                .OrderBy(static f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (ArgumentException ex)
        {
            problems.Add($"通配符 '{pattern}' 无效：{ex.Message}");
            return;
        }

        if (matches.Count == 0)
        {
            problems.Add($"通配符没有匹配到图片：{pattern}");
            return;
        }

        foreach (string match in matches)
        {
            // 目录输入与文件输入混用时，仍以通配符所在目录作为相对路径基准。
            AddResult(match, directory, results, seen);
        }
    }

    private static void AddResult(
        string path,
        string root,
        List<(string Path, string Root)> results,
        HashSet<string> seen)
    {
        string full = Path.GetFullPath(path);
        if (seen.Add(full))
        {
            results.Add((full, root));
        }
    }

    /// <summary>
    /// 目录模式下推导输出路径：保留相对子目录结构，文件名追加后缀并替换为输出格式的扩展名。
    /// </summary>
    private static string BuildOutputPath(string outputRoot, string inputPath, string inputRoot, AppOptions options)
    {
        string relativeDirectory = string.Empty;

        try
        {
            string? relative = Path.GetRelativePath(inputRoot, Path.GetDirectoryName(inputPath) ?? inputRoot);
            if (!string.IsNullOrEmpty(relative) && relative != ".")
            {
                relativeDirectory = relative;
            }
        }
        catch (ArgumentException)
        {
            // 跨盘符等无法求相对路径的场景，退化为平铺输出。
            relativeDirectory = string.Empty;
        }

        string fileName = Path.GetFileNameWithoutExtension(inputPath) + options.Suffix + ImageFile.GetExtension(options.Format);
        return Path.Combine(outputRoot, relativeDirectory, fileName);
    }

    private static string BuildMaskPath(string outputPath, AppOptions options)
    {
        _ = options;

        string directory = Path.GetDirectoryName(outputPath) ?? string.Empty;

        // outputPath 已包含后缀，这里只需再追加 _mask；蒙版固定用 PNG，避免有损压缩破坏灰度。
        string name = Path.GetFileNameWithoutExtension(outputPath) + "_mask" + ImageFile.GetExtension(OutputFormat.Png);

        return Path.Combine(directory, name);
    }

    /// <summary>判断 -o 参数是指向具体文件还是目录。</summary>
    private static bool LooksLikeFilePath(string path)
        => ImageFile.HasSupportedExtension(path)
           || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
}
