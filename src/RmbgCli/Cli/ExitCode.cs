namespace RmbgCli.Cli;

/// <summary>
/// 进程退出码。约定：0 表示全部成功，非 0 用于脚本化调用时区分失败原因。
/// </summary>
internal enum ExitCode
{
    /// <summary>全部图片处理成功。</summary>
    Success = 0,

    /// <summary>部分图片处理失败（批量场景下仍处理完成了其余文件）。</summary>
    PartialFailure = 1,

    /// <summary>命令行参数错误。</summary>
    UsageError = 2,

    /// <summary>运行环境问题：模型缺失、执行提供程序不可用等。</summary>
    EnvironmentError = 3,

    /// <summary>推理执行失败。</summary>
    InferenceError = 4,

    /// <summary>输入输出失败：路径无效、无权限、磁盘写入失败。</summary>
    IoError = 5,

    /// <summary>用户中断（Ctrl+C）。</summary>
    Cancelled = 130,
}

/// <summary>
/// 携带退出码的业务异常，用于在顶层统一转换为清晰的用户提示。
/// </summary>
internal sealed class RmbgException : Exception
{
    public RmbgException(ExitCode exitCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        ExitCode = exitCode;
    }

    public ExitCode ExitCode { get; }
}
