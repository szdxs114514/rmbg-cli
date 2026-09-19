using System.Globalization;
using RmbgCli.Core;
using SixLabors.ImageSharp.PixelFormats;

namespace RmbgCli.Cli;

/// <summary>
/// 命令行解析器（手写实现）。不引入预览版依赖，同时提供可读的错误定位。
/// </summary>
internal static class CommandLineParser
{
    public static bool TryParse(string[] args, out AppOptions options, out string? error)
    {
        options = new AppOptions();
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];

            // 支持 --name=value 形式，先拆分。
            string name = token;
            string? inlineValue = null;
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                int eq = token.IndexOf('=');
                if (eq > 0)
                {
                    name = token[..eq];
                    inlineValue = token[(eq + 1)..];
                }
            }

            string? TakeValue()
            {
                if (inlineValue is not null)
                {
                    return inlineValue;
                }

                if (i + 1 < args.Length && !IsOptionLike(args[i + 1]))
                {
                    i++;
                    return args[i];
                }

                return null;
            }

            switch (name)
            {
                case "-h" or "--help" or "/?":
                    options.ShowHelp = true;
                    return true;

                case "--version":
                    options.ShowVersion = true;
                    return true;

                case "-i" or "--input" or "--inputs":
                {
                    string? v = TakeValue();
                    if (string.IsNullOrWhiteSpace(v))
                    {
                        error = $"参数 {name} 缺少取值（示例：-i photo.jpg）。";
                        return false;
                    }

                    options.Inputs.Add(v);
                    break;
                }

                case "-o" or "--output" or "--out":
                {
                    string? v = TakeValue();
                    if (string.IsNullOrWhiteSpace(v))
                    {
                        error = $"参数 {name} 缺少取值（示例：-o out\\）。";
                        return false;
                    }

                    options.Output = v;
                    break;
                }

                case "-m" or "--model":
                {
                    string? v = TakeValue();
                    if (string.IsNullOrWhiteSpace(v))
                    {
                        error = $"参数 {name} 缺少取值（示例：--model models\\onnx\\model_fp16.onnx）。";
                        return false;
                    }

                    options.ModelPath = v;
                    break;
                }

                case "--size" or "--input-size":
                {
                    if (!TryParseInt(TakeValue(), name, out int size, out error))
                    {
                        return false;
                    }

                    // BiRefNet 主干含 5 级下采样，输入边长需为 32 的整数倍。
                    if (size < 256 || size > 2048 || size % 32 != 0)
                    {
                        error = $"参数 {name} 取值 {size} 非法：需为 32 的整数倍且位于 [256, 2048]（推荐 1024）。";
                        return false;
                    }

                    options.Size = size;
                    break;
                }

                case "--ep" or "--provider":
                {
                    string? v = TakeValue();
                    if (v is null || !TryParseEpMode(v, out var ep))
                    {
                        error = $"参数 {name} 取值非法：可选 auto | winml | dml | cpu | list。";
                        return false;
                    }

                    options.Ep = ep;
                    break;
                }

                case "--device-id":
                {
                    if (!TryParseInt(TakeValue(), name, out int id, out error))
                    {
                        return false;
                    }

                    if (id < 0)
                    {
                        error = $"参数 {name} 不能为负数。";
                        return false;
                    }

                    options.DeviceId = id;
                    break;
                }

                case "--threads":
                {
                    if (!TryParseInt(TakeValue(), name, out int t, out error))
                    {
                        return false;
                    }

                    if (t < 1 || t > 512)
                    {
                        error = $"参数 {name} 取值 {t} 非法：需位于 [1, 512]。";
                        return false;
                    }

                    options.Threads = t;
                    break;
                }

                case "--threshold" or "-t":
                {
                    if (!TryParseFloat(TakeValue(), name, out float th, out error))
                    {
                        return false;
                    }

                    if (th < 0f || th > 1f)
                    {
                        error = $"参数 {name} 取值 {th} 非法：需位于 [0, 1]。0 表示保留软蒙版。";
                        return false;
                    }

                    options.Threshold = th;
                    break;
                }

                case "--feather" or "-f":
                {
                    if (!TryParseFloat(TakeValue(), name, out float feather, out error))
                    {
                        return false;
                    }

                    if (feather < 0f || feather > 512f)
                    {
                        error = $"参数 {name} 取值 {feather} 非法：需位于 [0, 512] 像素。";
                        return false;
                    }

                    options.Feather = feather;
                    break;
                }

                case "--invert":
                    options.Invert = true;
                    break;

                case "--mask":
                {
                    string? v = TakeValue();
                    if (v is null)
                    {
                        // --mask 单独出现时等价于 --mask=both
                        options.MaskOutput = MaskOutputMode.Both;
                        break;
                    }

                    switch (v.ToLowerInvariant())
                    {
                        case "none": options.MaskOutput = MaskOutputMode.None; break;
                        case "mask": options.MaskOutput = MaskOutputMode.Mask; break;
                        case "both": options.MaskOutput = MaskOutputMode.Both; break;
                        default:
                            error = $"参数 {name} 取值 '{v}' 非法：可选 none | mask | both。";
                            return false;
                    }

                    break;
                }

                case "--mask-resampler":
                {
                    string? v = TakeValue();
                    if (v is null || !TryParseResampler(v, out var rs))
                    {
                        error = $"参数 {name} 取值非法：可选 bilinear | bicubic | nearest。";
                        return false;
                    }

                    options.MaskResampler = rs;
                    break;
                }

                case "--bg" or "--background":
                {
                    string? v = TakeValue();
                    if (v is null || !TryParseColor(v, out Rgba32? color))
                    {
                        error = $"参数 {name} 取值非法：支持 #RRGGBB / #RRGGBBAA / transparent / white / black / gray。";
                        return false;
                    }

                    options.Background = color;
                    break;
                }

                case "--format":
                {
                    string? v = TakeValue()?.ToLowerInvariant();
                    switch (v)
                    {
                        case "png": options.Format = OutputFormat.Png; break;
                        case "webp": options.Format = OutputFormat.Webp; break;
                        default:
                            error = $"参数 {name} 取值非法：可选 png | webp。";
                            return false;
                    }

                    break;
                }

                case "--suffix":
                {
                    string? v = TakeValue();
                    if (v is null)
                    {
                        error = $"参数 {name} 缺少取值。";
                        return false;
                    }

                    if (v.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    {
                        error = $"参数 {name} 含有非法文件名字符。";
                        return false;
                    }

                    options.Suffix = v;
                    break;
                }

                case "-r" or "--recursive":
                    options.Recursive = true;
                    break;

                case "-y" or "--overwrite" or "--force":
                    options.Overwrite = true;
                    break;

                case "-q" or "--quiet":
                    options.Quiet = true;
                    break;

                case "-v" or "--verbose":
                    options.Verbose = true;
                    break;

                case "-g" or "--guide" or "--interactive" or "--wizard":
                    options.Guide = true;
                    break;

                case "--download-model" or "--get-model":
                {
                    options.DownloadModel = true;

                    // 变体值是"可选位置参数"：
                    //   --download-model=fp32   显式赋值
                    //   --download-model fp32   跟随一个非选项 token
                    //   --download-model        使用默认 fp16（下一个 token 以 '-' 开头或已到末尾）
                    // 只要出现非选项 token 就按变体解析并校验，避免把拼错的变体名静默忽略。
                    string? variant = inlineValue;

                    if (variant is null && i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                    {
                        i++;
                        variant = args[i];
                    }

                    if (variant is not null)
                    {
                        if (ModelCatalog.Find(variant) is null)
                        {
                            error = $"未知的模型变体 '{variant}'。可选：{ModelCatalog.DescribeKeys()}。";
                            return false;
                        }

                        options.ModelVariant = variant;
                    }

                    break;
                }

                case "--model-dir" or "--download-dir":
                {
                    string? v = TakeValue();
                    if (string.IsNullOrWhiteSpace(v))
                    {
                        error = $"参数 {name} 缺少取值（模型存放目录）。";
                        return false;
                    }

                    options.DownloadDirectory = v;
                    break;
                }

                case "--connections" or "-j":
                {
                    if (!TryParseInt(TakeValue(), name, out int connections, out error))
                    {
                        return false;
                    }

                    if (connections < 1 || connections > 32)
                    {
                        error = $"参数 {name} 取值 {connections} 非法：需位于 [1, 32]。";
                        return false;
                    }

                    options.Connections = connections;
                    break;
                }

                case "--install-aria2" or "--get-aria2":
                    options.InstallAria2 = true;
                    break;

                case "--no-aria2":
                    options.PreferAria2 = false;
                    break;

                case "--install-ep" or "--get-ep":
                {
                    options.InstallEp = true;

                    // 与 --download-model 同样的可选位置参数规则：
                    // 出现非选项 token 就按 EP 标识解析并校验，避免拼错的名称被静默忽略。
                    string? spec = inlineValue;

                    if (spec is null && i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                    {
                        i++;
                        spec = args[i];
                    }

                    if (spec is not null)
                    {
                        if (KnownExecutionProviders.Resolve(spec) is null)
                        {
                            error = $"无法识别的执行提供程序 '{spec}'。可选：{KnownExecutionProviders.DescribeKeys()}。";
                            return false;
                        }

                        options.EpSpec = spec;
                    }

                    break;
                }

                case "--allow-ep-install":
                    options.AllowEpInstall = true;
                    break;

                default:
                    if (name.StartsWith('-') && name.Length > 1)
                    {
                        error = $"无法识别的参数 '{token}'。使用 --help 查看完整用法。";
                        return false;
                    }

                    // 位置参数：直接视为输入路径，方便 `rmbg photo.jpg -o out` 这类用法。
                    options.Inputs.Add(token);
                    break;
            }
        }

        if (options.ShowHelp || options.ShowVersion)
        {
            return true;
        }

        // 以下模式只做单件事，不需要输入与输出路径：
        //   --ep list      环境自检
        //   --guide        进入引导式交互
        //   --download-model / --install-ep / 单独的 --install-aria2
        if (options.Ep == EpMode.List || options.Guide || options.DownloadModel || options.InstallEp)
        {
            return true;
        }

        if (options.InstallAria2 && options.Inputs.Count == 0 && string.IsNullOrWhiteSpace(options.Output))
        {
            return true;
        }

        if (options.Inputs.Count == 0)
        {
            error = "缺少输入图片。请用 -i <路径> 指定图片、目录或通配符。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.Output))
        {
            error = "缺少输出路径。请用 -o <路径> 指定输出文件或目录。";
            return false;
        }

        if (options.Quiet && options.Verbose)
        {
            error = "--quiet 与 --verbose 不能同时使用。";
            return false;
        }

        return true;
    }

    private static bool IsOptionLike(string token)
        => token.Length > 1 && token[0] == '-' && !double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static bool TryParseInt(string? value, string name, out int result, out string? error)
    {
        if (value is not null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
        {
            error = null;
            return true;
        }

        result = 0;
        error = $"参数 {name} 需要一个整数，实际收到 '{value ?? "(空)"}'。";
        return false;
    }

    private static bool TryParseFloat(string? value, string name, out float result, out string? error)
    {
        if (value is not null && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
        {
            error = null;
            return true;
        }

        result = 0f;
        error = $"参数 {name} 需要一个数字，实际收到 '{value ?? "(空)"}'。";
        return false;
    }

    private static bool TryParseEpMode(string value, out EpMode mode)
    {
        switch (value.ToLowerInvariant())
        {
            case "auto": mode = EpMode.Auto; return true;
            case "winml" or "windowsml" or "windows-ml": mode = EpMode.WinMl; return true;
            case "dml" or "directml": mode = EpMode.Dml; return true;
            case "cpu": mode = EpMode.Cpu; return true;
            case "list": mode = EpMode.List; return true;
            default: mode = EpMode.Auto; return false;
        }
    }

    private static bool TryParseResampler(string value, out MaskResampler resampler)
    {
        switch (value.ToLowerInvariant())
        {
            case "bilinear": resampler = MaskResampler.Bilinear; return true;
            case "bicubic": resampler = MaskResampler.Bicubic; return true;
            case "nearest": resampler = MaskResampler.Nearest; return true;
            default: resampler = MaskResampler.Bicubic; return false;
        }
    }

    private static bool TryParseColor(string value, out Rgba32? color)
    {
        color = null;
        string v = value.Trim();

        switch (v.ToLowerInvariant())
        {
            case "transparent" or "none":
                return true;
            case "white":
                color = new Rgba32(255, 255, 255, 255);
                return true;
            case "black":
                color = new Rgba32(0, 0, 0, 255);
                return true;
            case "gray" or "grey":
                color = new Rgba32(128, 128, 128, 255);
                return true;
            case "red":
                color = new Rgba32(255, 0, 0, 255);
                return true;
            case "green":
                color = new Rgba32(0, 128, 0, 255);
                return true;
            case "blue":
                color = new Rgba32(0, 0, 255, 255);
                return true;
        }

        if (!v.StartsWith('#'))
        {
            return false;
        }

        string hex = v[1..];
        static bool TryByte(string s, int offset, out byte b)
        {
            return byte.TryParse(s.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
        }

        if (hex.Length == 6 && TryByte(hex, 0, out byte r) && TryByte(hex, 2, out byte g) && TryByte(hex, 4, out byte b6))
        {
            color = new Rgba32(r, g, b6, 255);
            return true;
        }

        if (hex.Length == 8 && TryByte(hex, 0, out byte r8) && TryByte(hex, 2, out byte g8)
            && TryByte(hex, 4, out byte b8) && TryByte(hex, 6, out byte a8))
        {
            color = new Rgba32(r8, g8, b8, a8);
            return true;
        }

        return false;
    }
}
