namespace RmbgCli.Cli;

/// <summary>
/// 控制台输出封装。所有面向用户的提示都经由此处，便于统一控制静默/详细级别与颜色。
/// </summary>
internal sealed class Logger
{
    private readonly bool _quiet;
    private readonly bool _verbose;

    public Logger(bool quiet, bool verbose)
    {
        _quiet = quiet;
        _verbose = verbose;
    }

    /// <summary>是否处于详细模式（用于决定是否把底层运行时的日志一并透出）。</summary>
    public bool Verbose => _verbose;

    /// <summary>常规进度信息。</summary>
    public void Info(string message)
    {
        if (!_quiet)
        {
            Console.WriteLine(message);
        }
    }

    /// <summary>成功提示（绿色）。--quiet 下同样静默。</summary>
    public void Success(string message)
    {
        if (!_quiet)
        {
            WriteColored(message, ConsoleColor.Green, Console.Out);
        }
    }

    /// <summary>警告（黄色）。</summary>
    public void Warn(string message)
    {
        WriteColored(message, ConsoleColor.Yellow, Console.Error);
    }

    /// <summary>错误（红色）。</summary>
    public void Error(string message)
    {
        WriteColored(message, ConsoleColor.Red, Console.Error);
    }

    /// <summary>仅在 --verbose 下输出的细节。</summary>
    public void Detail(string message)
    {
        if (_verbose)
        {
            WriteColored(message, ConsoleColor.DarkGray, Console.Out);
        }
    }

    /// <summary>分节标题。</summary>
    public void Section(string title)
    {
        if (!_quiet)
        {
            WriteColored(title, ConsoleColor.Cyan, Console.Out);
        }
    }

    /// <summary>是否可以在同一行原地刷新进度（未静默且输出未被重定向）。</summary>
    public bool CanRenderProgress
    {
        get
        {
            try
            {
                return !_quiet && !Console.IsOutputRedirected;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 原地刷新一行进度。输出被重定向或 --quiet 时静默丢弃，
    /// 以保证日志文件与管道输出保持整洁。
    /// </summary>
    public void Progress(string text)
    {
        if (!CanRenderProgress)
        {
            return;
        }

        try
        {
            int width = SafeWindowWidth();
            string line = text.Length >= width ? text[..Math.Max(0, width - 1)] : text.PadRight(width - 1);
            Console.Write('\r');
            Console.Write(line);
        }
        catch (IOException)
        {
            // 忽略
        }
    }

    /// <summary>结束原地进度行（换行），避免后续输出与进度残留混在一起。</summary>
    public void ProgressComplete()
    {
        if (!CanRenderProgress)
        {
            return;
        }

        try
        {
            Console.WriteLine();
        }
        catch (IOException)
        {
            // 忽略
        }
    }

    private static int SafeWindowWidth()
    {
        try
        {
            int width = Console.WindowWidth;
            return width is > 20 and < 400 ? width : 100;
        }
        catch (IOException)
        {
            return 100;
        }
        catch (PlatformNotSupportedException)
        {
            return 100;
        }
    }

    private static void WriteColored(string message, ConsoleColor color, TextWriter writer)
    {
        bool restore = false;
        try
        {
            // 重定向到管道时 Console 颜色操作可能抛异常，需容错。
            writer.Flush();
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            restore = true;
            writer.WriteLine(message);
            Console.ForegroundColor = previous;
            restore = false;
        }
        catch (IOException)
        {
            writer.WriteLine(message);
        }
        catch (PlatformNotSupportedException)
        {
            writer.WriteLine(message);
        }
        finally
        {
            if (restore)
            {
                try
                {
                    Console.ResetColor();
                }
                catch (IOException)
                {
                    // 忽略
                }
                catch (PlatformNotSupportedException)
                {
                    // 忽略
                }
            }
        }
    }
}
