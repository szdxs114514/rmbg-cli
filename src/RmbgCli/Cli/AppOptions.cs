using SixLabors.ImageSharp.PixelFormats;

namespace RmbgCli.Cli;

/// <summary>执行提供程序（Execution Provider）选择模式。</summary>
internal enum EpMode
{
    /// <summary>Windows ML 已注册的 EP → DirectML → CPU，逐级回退。</summary>
    Auto,

    /// <summary>仅使用 Windows ML 目录中已就绪的 EP（如 NPU 的 QNN / VitisAI / OpenVINO），不可用则报错。</summary>
    WinMl,

    /// <summary>仅使用 DirectML（任意支持 D3D12 的 GPU）。</summary>
    Dml,

    /// <summary>仅使用 CPU。</summary>
    Cpu,

    /// <summary>列出当前机器可用的执行提供程序后退出。</summary>
    List,
}

/// <summary>灰度蒙版的额外输出方式。</summary>
internal enum MaskOutputMode
{
    /// <summary>不输出蒙版。</summary>
    None,

    /// <summary>输出独立蒙版文件。</summary>
    Mask,

    /// <summary>同时输出抠图结果与蒙版文件。</summary>
    Both,
}

/// <summary>分辨率重采样算法（用于蒙版回缩放到原图尺寸）。</summary>
internal enum MaskResampler
{
    /// <summary>双线性，边缘更柔和。</summary>
    Bilinear,

    /// <summary>双三次，与官方 Python 参考实现一致（PIL Image.resize 默认值）。</summary>
    Bicubic,

    /// <summary>最近邻，保留硬边缘，配合 --threshold 使用。</summary>
    Nearest,
}

/// <summary>输出容器格式。</summary>
internal enum OutputFormat
{
    Png,
    Webp,
}

/// <summary>命令行解析结果。</summary>
internal sealed class AppOptions
{
    // ---- 输入输出 ----
    public List<string> Inputs { get; } = new();

    public string? Output { get; set; }

    public bool Recursive { get; set; }

    public bool Overwrite { get; set; }

    public string Suffix { get; set; } = "_nobg";

    public OutputFormat Format { get; set; } = OutputFormat.Png;

    // ---- 模型与推理 ----
    public string? ModelPath { get; set; }

    /// <summary>送入模型的方形边长。RMBG-2.0 训练分辨率为 1024。</summary>
    public int Size { get; set; } = 1024;

    public EpMode Ep { get; set; } = EpMode.Auto;

    public int DeviceId { get; set; }

    public int? Threads { get; set; }

    // ---- 蒙版后处理 ----
    /// <summary>二值化阈值；0 表示保留软蒙版（不做二值化）。</summary>
    public float Threshold { get; set; }

    /// <summary>边缘羽化半径（像素），0 表示不羽化。</summary>
    public float Feather { get; set; }

    public bool Invert { get; set; }

    public MaskResampler MaskResampler { get; set; } = MaskResampler.Bicubic;

    public MaskOutputMode MaskOutput { get; set; } = MaskOutputMode.None;

    /// <summary>合成背景色；null 表示输出透明背景。</summary>
    public Rgba32? Background { get; set; }

    // ---- 日志 ----
    public bool Quiet { get; set; }

    public bool Verbose { get; set; }

    public bool ShowHelp { get; set; }

    public bool ShowVersion { get; set; }

    // ---- 引导式操作与模型下载 ----
    /// <summary>进入引导式交互流程。</summary>
    public bool Guide { get; set; }

    /// <summary>只执行模型下载后退出。</summary>
    public bool DownloadModel { get; set; }

    /// <summary>下载的模型变体 key（fp16 / fp32 / int8 …）。</summary>
    public string? ModelVariant { get; set; }

    /// <summary>模型存放目录；null 表示使用默认 models\onnx。</summary>
    public string? DownloadDirectory { get; set; }

    /// <summary>下载并发连接数。</summary>
    public int Connections { get; set; } = 8;

    /// <summary>是否允许使用 aria2c；false 表示强制使用内置下载器。</summary>
    public bool PreferAria2 { get; set; } = true;

    /// <summary>是否允许自动安装 aria2 便携版。</summary>
    public bool InstallAria2 { get; set; }

    // ---- 执行提供程序安装 ----
    /// <summary>只执行 EP 安装后退出。</summary>
    public bool InstallEp { get; set; }

    /// <summary>要安装的 EP 标识（all，或某个 EP 的简写 / 全名）。</summary>
    public string? EpSpec { get; set; }

    /// <summary>
    /// 允许在 auto 模式下自动下载安装缺失的 EP。
    /// 默认关闭：EP 安装包体积可观，必须由用户显式同意后再下载。
    /// </summary>
    public bool AllowEpInstall { get; set; }
}
