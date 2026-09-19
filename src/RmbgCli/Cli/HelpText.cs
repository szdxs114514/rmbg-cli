using System.Reflection;

namespace RmbgCli.Cli;

/// <summary>命令行帮助与版本文本。</summary>
internal static class HelpText
{
    public static string VersionLine
    {
        get
        {
            Assembly assembly = typeof(HelpText).Assembly;
            string version = assembly.GetName().Version?.ToString(3) ?? "1.0.0";
            return $"rmbg {version} (.NET {Environment.Version}, {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})";
        }
    }

    public static string Full =>
        $"""
         {VersionLine}

         基于 Windows ML / ONNX Runtime 的 RMBG-2.0 图像抠图（背景移除）命令行工具。

         用法:
           rmbg -i <输入> -o <输出> [选项]

         输入与输出:
           -i, --input <路径>        输入图片、目录或通配符（如 "*.png"）；可重复指定多次
           -o, --output <路径>       输出文件（单张）或输出目录（多张）
           -r, --recursive           输入为目录/通配符时递归子目录
           -y, --overwrite           覆盖已存在的输出文件
               --suffix <字符串>     输出文件名后缀，默认 "_nobg"（设为 "" 则不加后缀）
               --format <png|webp>   输出格式，默认 png
               --bg <颜色>           合成到纯色背景而非透明，如 #FFFFFF / white / black / gray

         模型与推理:
           -m, --model <路径>        ONNX 模型文件或所在目录；默认自动搜索 models\onnx
               --size <整数>         送入模型的边长，默认 1024，须为 32 的倍数
               --ep <模式>           执行提供程序：auto | winml | dml | cpu | list，默认 auto
               --device-id <整数>    DirectML 设备序号，默认 0（多显卡时可选 1、2…）
               --threads <整数>      CPU 推理线程数，默认由 ONNX Runtime 决定

         蒙版后处理:
               --threshold <0..1>    二值化阈值，默认 0 表示保留软蒙版（推荐用于发丝等细节）
           -f, --feather <像素>      边缘羽化半径，默认 0；等效高斯 σ ≈ 1.2 × 半径
               --invert              反转蒙版（主体与背景判断相反时使用）
               --mask <none|mask|both>
                                     是否额外输出 8bit 灰度蒙版，默认 none
               --mask-resampler <算法>
                                     蒙版回缩放算法：bilinear | bicubic | nearest，默认 bicubic

         引导式操作与模型下载:
           -g, --guide               进入引导式操作（逐项问答；直接执行且不带任何参数时自动进入）
               --download-model [变体]
                                     下载模型后退出，不传变体则使用 fp16
                                     变体: fp16 | fp32 | int8 | quantized | uint8 | q4f16 | q4 | bnb4
               --model-dir <目录>     模型存放目录，默认 models\onnx
           -j, --connections <1..32> 下载并发连接数，默认 8
               --install-aria2       安装 aria2 便携版（单独使用时安装完即退出）
               --no-aria2            强制使用内置多线程下载器，不调用 aria2c
               --install-ep [目标]   安装 Windows ML 执行提供程序后退出
                                     目标: all（默认）| qnn | vitisai | openvino | nvidia | migraphx | webgpu
               --allow-ep-install    允许在 auto 模式下自动下载安装缺失的 EP（默认关闭）

         其它:
           -v, --verbose             输出详细诊断信息（模型张量、EP 选择过程、ORT 日志）
           -q, --quiet               只输出错误
           -h, --help                显示本帮助
               --version             显示版本号

         示例:
           # 零参数直接进入引导式操作（推荐给不熟悉命令行的场景）
           rmbg

           # 查看本机 EP 状态：哪些已安装、哪些可安装、硬件是什么
           rmbg --ep list

           # 安装全部兼容的执行提供程序（NPU/GPU 加速）
           rmbg --install-ep all

           # 只安装高通的 QNN（NPU）
           rmbg --install-ep qnn

           # 下载模型的默认变体（fp16）；aria2c 缺失时提示是否安装便携版
           rmbg --download-model --install-aria2

           # 下载 fp32 模型，16 连接，强制走内置下载器
           rmbg --download-model fp32 -j 16 --no-aria2

           # 只安装 aria2 便携版
           rmbg --install-aria2

           # 单张图片，默认输出透明背景 PNG
           rmbg -i photo.jpg -o photo_nobg.png

           # 批量处理目录并递归子目录，同时导出蒙版
           rmbg -i .\photos -o .\cutouts --recursive --mask both

           # 电商场景：换白底、边缘羽化 2 像素
           rmbg -i .\products\*.jpg -o .\out --bg #FFFFFF --feather 2

           # 指定 fp32 模型并强制使用 CPU
           rmbg -i in.png -o out.png --model D:\models\model.onnx --ep cpu

           # 允许自动安装缺失的 EP，然后自动选最优后端
           rmbg -i in.png -o out.png --allow-ep-install

         退出码:
           0 成功    1 部分失败    2 参数错误    3 环境/模型错误    4 推理失败    5 读写失败

         执行提供程序:
           CPU 与 DirectML 随 Windows ML 运行时内置，始终可用。
           其余 EP 需按需下载安装（要求 Windows 11 24H2 / build 26100 及以上）：
             QNN(高通 NPU) · VitisAI(AMD NPU) · OpenVINO(Intel) · NvTensorRtRtx(NVIDIA GPU)
             · MIGraphX(AMD GPU) · WebGpu(实验性)
           安装后需注册到 ONNX Runtime 才能使用，本工具会自动完成注册。
           auto 模式的候选顺序：Windows ML 已注册 EP（NPU 优先）→ DirectML → CPU。

         模型来源（ModelScope，国内可直连）:
           https://www.modelscope.cn/models/AI-ModelScope/RMBG-2.0
           推荐文件: onnx/model_fp16.onnx（约 490 MB，质量与速度平衡）
           下载器优先使用 aria2c（多连接 + 断点续传），未安装时可自动安装便携版；
           未使用 aria2 时回退到内置多连接下载器（零外部依赖）。
         """;
}
