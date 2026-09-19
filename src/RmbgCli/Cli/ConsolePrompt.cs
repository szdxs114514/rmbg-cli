using System.Globalization;
using SixLabors.ImageSharp.PixelFormats;

namespace RmbgCli.Cli;

/// <summary>
/// 交互式提问原语。
///
/// 设计要点：
///   - 所有提问都可能返回 null（表示用户取消 / 输入流结束），调用方必须据此回退；
///   - 支持把文件从资源管理器直接拖进终端：Windows 会插入带引号的路径，需剥离引号；
///   - 在提问阻塞期间按 Ctrl+C 直接终止进程（此时没有在途的写文件操作），
///     而在执行阶段按 Ctrl+C 走优雅取消（由 Program 注册的处理器负责）。
/// </summary>
internal static class ConsolePrompt
{
    /// <summary>为 true 表示当前正阻塞在提问上，此时 Ctrl+C 允许直接终止。</summary>
    private static volatile bool _promptActive;

    private static bool _handlerInstalled;

    private static void EnsureHandler()
    {
        if (_handlerInstalled)
        {
            return;
        }

        _handlerInstalled = true;

        // Program 中注册的处理器会把 Cancel 置为 true 以走优雅取消；
        // 这里在后注册，提问期间把它改回 false，使 Ctrl+C 立即生效。
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            if (_promptActive)
            {
                eventArgs.Cancel = false;
            }
        };
    }

    /// <summary>读取一行输入；返回 null 表示取消（q / quit / Ctrl+Z / 输入流结束）。</summary>
    public static string? ReadLine(string label, string? defaultValue = null, string? hint = null)
    {
        EnsureHandler();

        string suffix = defaultValue is null ? string.Empty : $" [默认 {defaultValue}]";
        if (!string.IsNullOrEmpty(hint))
        {
            WriteHint(hint);
        }

        WritePrompt($"{label}{suffix}: ");

        _promptActive = true;
        string? input;
        try
        {
            input = Console.ReadLine();
        }
        finally
        {
            _promptActive = false;
        }

        if (input is null)
        {
            // 输入流关闭：视为取消。
            return null;
        }

        input = input.Trim();

        if (IsCancel(input))
        {
            return null;
        }

        if (input.Length == 0 && defaultValue is not null)
        {
            return defaultValue;
        }

        return input;
    }

    /// <summary>
    /// 让用户在编号菜单中选一项。
    /// </summary>
    /// <returns>选中的索引；返回 -1 表示取消。</returns>
    public static int ChooseIndexed(
        string title,
        IReadOnlyList<(string Label, string? Hint)> options,
        int defaultIndex = 0,
        bool allowCancel = true)
    {
        if (options.Count == 0)
        {
            return -1;
        }

        Console.WriteLine();
        WriteTitle($"  {title}");

        for (int i = 0; i < options.Count; i++)
        {
            (string label, string? hint) = options[i];
            string marker = i == defaultIndex ? "*" : " ";
            Console.WriteLine($"  {marker} {i + 1}) {label}");

            if (!string.IsNullOrWhiteSpace(hint))
            {
                WriteHint($"        {hint}");
            }
        }

        Console.WriteLine();

        while (true)
        {
            string prompt = allowCancel
                ? $"  请输入序号后回车 [默认 {defaultIndex + 1}]，或输入 q 返回: "
                : $"  请输入序号后回车 [默认 {defaultIndex + 1}]: ";

            WritePrompt(prompt);

            _promptActive = true;
            string? input;
            try
            {
                input = Console.ReadLine();
            }
            finally
            {
                _promptActive = false;
            }

            if (input is null)
            {
                return allowCancel ? -1 : defaultIndex;
            }

            input = input.Trim();

            if (IsCancel(input) && allowCancel)
            {
                return -1;
            }

            if (input.Length == 0)
            {
                return defaultIndex;
            }

            // 允许用 0 表示"返回上一层 / 退出"，符合大多数命令行向导的直觉。
            if (input == "0" && allowCancel)
            {
                return -1;
            }

            if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out int choice)
                && choice >= 1
                && choice <= options.Count)
            {
                return choice - 1;
            }

            WriteWarning($"  无效输入 '{input}'，请输入 1 - {options.Count} 之间的序号。");
        }
    }

    /// <summary>显示一句提示并等待用户回车（用于让结果停留在屏幕上）。</summary>
    public static void Pause(string message)
    {
        ReadLine($"  {message}");
    }

    /// <summary>是/否确认。</summary>
    public static bool Confirm(string question, bool defaultValue = true)
    {
        string hint = defaultValue ? "Y/n" : "y/N";

        while (true)
        {
            string? input = ReadLine($"  {question} ({hint})");

            if (input is null)
            {
                return false;
            }

            switch (input.ToLowerInvariant())
            {
                case "y" or "yes" or "是":
                    return true;
                case "n" or "no" or "否":
                    return false;
                case "":
                    return defaultValue;
                default:
                    WriteWarning("  请输入 y 或 n。");
                    break;
            }
        }
    }

    /// <summary>询问单个路径（支持拖拽）。返回 null 表示取消。</summary>
    public static string? AskSinglePath(string label, PathKind kind, string? defaultValue = null)
    {
        string[]? paths = AskPaths(label, kind, defaultValue);
        return paths is null ? null : paths[0];
    }

    /// <summary>
    /// 询问一个或多个路径（支持从资源管理器拖入多个文件）。
    /// 返回 null 表示取消。
    /// </summary>
    /// <param name="label">提示文本。</param>
    /// <param name="kind">路径类型约束。</param>
    /// <param name="defaultValue">默认值（直接回车采用）。</param>
    public static string[]? AskPaths(string label, PathKind kind, string? defaultValue = null)
    {
        while (true)
        {
            string? input = ReadLine(
                $"  {label}",
                defaultValue,
                kind switch
                {
                    PathKind.ExistingFile => "  （可把文件从资源管理器直接拖到本窗口）",
                    PathKind.ExistingFileOrDirectory => "  （可拖入文件或文件夹；也支持 *.png 这类通配符）",
                    _ => null,
                });

            if (input is null)
            {
                return null;
            }

            string[] candidates = SplitDroppedPaths(input);

            if (candidates.Length == 0)
            {
                WriteWarning("  路径为空，请重新输入。");
                continue;
            }

            if (candidates.Length > 1)
            {
                // 一次拖入多个文件时全部返回，由调用方逐个加入输入列表。
                bool allExist = Array.TrueForAll(candidates, static c => File.Exists(c) || Directory.Exists(c));
                if (!allExist)
                {
                    WriteWarning("  部分拖入的路径不存在，请重新输入。");
                    continue;
                }

                return candidates;
            }

            string path = candidates[0];

            if (path.IndexOfAny(new[] { '*', '?' }) >= 0)
            {
                if (kind == PathKind.ExistingFile)
                {
                    WriteWarning("  此处不支持通配符，请给出具体文件路径。");
                    continue;
                }

                return new[] { path };
            }

            bool exists = File.Exists(path) || Directory.Exists(path);

            if (kind == PathKind.ExistingFileOrDirectory && !exists)
            {
                WriteWarning($"  路径不存在：{path}");
                continue;
            }

            if (kind == PathKind.ExistingFile)
            {
                if (Directory.Exists(path))
                {
                    WriteWarning("  这里需要一个图片文件，但你给的是目录。");
                    continue;
                }

                if (!exists)
                {
                    WriteWarning($"  文件不存在：{path}");
                    continue;
                }

                if (!Imaging.ImageFile.HasSupportedExtension(path))
                {
                    WriteWarning($"  不支持的图片扩展名：{Path.GetExtension(path)}");
                    continue;
                }
            }

            return new[] { path };
        }
    }

    /// <summary>询问一个整数。</summary>
    public static int AskInt(string label, int defaultValue, int min, int max)
    {
        while (true)
        {
            string? input = ReadLine($"  {label}（{min} - {max}）", defaultValue.ToString(CultureInfo.InvariantCulture));

            if (input is null)
            {
                return defaultValue;
            }

            if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                && value >= min
                && value <= max)
            {
                return value;
            }

            WriteWarning($"  请输入 {min} - {max} 之间的整数。");
        }
    }

    /// <summary>询问一个浮点数。</summary>
    public static float AskFloat(string label, float defaultValue, float min, float max)
    {
        while (true)
        {
            string? input = ReadLine(
                $"  {label}（{min} - {max}）",
                defaultValue.ToString("0.##", CultureInfo.InvariantCulture));

            if (input is null)
            {
                return defaultValue;
            }

            if (float.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                && value >= min
                && value <= max)
            {
                return value;
            }

            WriteWarning($"  请输入 {min} - {max} 之间的数字。");
        }
    }

    /// <summary>询问背景色。返回 null 表示透明背景；取消时通过 <paramref name="cancelled"/> 反馈。</summary>
    public static Rgba32? AskBackground(out bool cancelled)
    {
        int index = ChooseIndexed(
            "合成背景",
            new (string, string?)[]
            {
                ("透明背景（保留 Alpha 通道）", "推荐：后续可在任意软件里叠加"),
                ("白色", "电商主图常用"),
                ("黑色", null),
                ("中灰", null),
                ("自定义颜色（#RRGGBB）", null),
            });

        cancelled = index < 0;

        if (cancelled)
        {
            return null;
        }

        switch (index)
        {
            case 1:
                return new Rgba32(255, 255, 255, 255);
            case 2:
                return new Rgba32(0, 0, 0, 255);
            case 3:
                return new Rgba32(128, 128, 128, 255);
            case 4:
                while (true)
                {
                    string? input = ReadLine("  请输入颜色（如 #1E90FF 或 #1E90FFFF）");

                    if (input is null)
                    {
                        cancelled = true;
                        return null;
                    }

                    if (TryParseHexColor(input, out Rgba32 color))
                    {
                        return color;
                    }

                    WriteWarning("  颜色格式不正确，请使用 #RRGGBB 或 #RRGGBBAA。");
                }

            default:
                return null;
        }
    }

    /// <summary>把一段用户输入拆成若干路径。优先解析引号段（拖拽多文件的情形）。</summary>
    public static string[] SplitDroppedPaths(string raw)
    {
        string trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return Array.Empty<string>();
        }

        if (trimmed.Contains('"'))
        {
            List<string> quoted = new();
            bool inside = false;
            int start = 0;

            for (int i = 0; i < trimmed.Length; i++)
            {
                if (trimmed[i] != '"')
                {
                    continue;
                }

                if (inside)
                {
                    quoted.Add(trimmed[start..i]);
                    inside = false;
                }
                else
                {
                    start = i + 1;
                    inside = true;
                }
            }

            if (quoted.Count > 0)
            {
                return quoted
                    .Select(static p => p.Trim())
                    .Where(static p => p.Length > 0)
                    .ToArray();
            }
        }

        // 无引号时整串视为一个路径，这样含空格的路径依然可用。
        return new[] { trimmed };
    }

    private static bool TryParseHexColor(string value, out Rgba32 color)
    {
        color = default;

        string hex = value.Trim().TrimStart('#');
        if (hex.Length is not (6 or 8))
        {
            return false;
        }

        static bool TryByte(string source, int offset, out byte result)
            => byte.TryParse(source.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);

        if (hex.Length == 6)
        {
            if (TryByte(hex, 0, out byte r) && TryByte(hex, 2, out byte g) && TryByte(hex, 4, out byte b))
            {
                color = new Rgba32(r, g, b, 255);
                return true;
            }

            return false;
        }

        if (TryByte(hex, 0, out byte r8) && TryByte(hex, 2, out byte g8)
            && TryByte(hex, 4, out byte b8) && TryByte(hex, 6, out byte a8))
        {
            color = new Rgba32(r8, g8, b8, a8);
            return true;
        }

        return false;
    }

    private static bool IsCancel(string input)
        => input.Equals("q", StringComparison.OrdinalIgnoreCase)
           || input.Equals("quit", StringComparison.OrdinalIgnoreCase)
           || input.Equals("exit", StringComparison.OrdinalIgnoreCase)
           || input.Equals("back", StringComparison.OrdinalIgnoreCase);

    private static void WritePrompt(string text) => WriteColored(text, ConsoleColor.White, newLine: false);

    private static void WriteTitle(string text) => WriteColored(text, ConsoleColor.Cyan, newLine: true);

    private static void WriteHint(string text) => WriteColored(text, ConsoleColor.DarkGray, newLine: true);

    private static void WriteWarning(string text) => WriteColored(text, ConsoleColor.Yellow, newLine: true);

    private static void WriteColored(string text, ConsoleColor color, bool newLine)
    {
        try
        {
            ConsoleColor previous = Console.ForegroundColor;
            Console.ForegroundColor = color;

            if (newLine)
            {
                Console.WriteLine(text);
            }
            else
            {
                Console.Write(text);
            }

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

/// <summary>路径约束类型。</summary>
internal enum PathKind
{
    /// <summary>必须是一个存在且受支持的图片文件。</summary>
    ExistingFile,

    /// <summary>存在的文件或目录，或通配符。</summary>
    ExistingFileOrDirectory,

    /// <summary>任意路径（可以尚不存在），用于输出路径。</summary>
    AnyPath,
}
