# rmbg — 一键抠图，自动去掉图片背景

[![CI](https://github.com/szdxs114514/rmbg-cli/actions/workflows/ci.yml/badge.svg)](https://github.com/szdxs114514/rmbg-cli/actions/workflows/ci.yml)
[![Release](https://github.com/szdxs114514/rmbg-cli/actions/workflows/release.yml/badge.svg)](https://github.com/szdxs114514/rmbg-cli/actions/workflows/release.yml)

把照片里的**主体**（人、商品、宠物、logo…）自动抠出来，去掉背景，得到一张透明底的 PNG。

- **全自动**：不需要用套索或魔棒一点点描边，选中图片就能出结果。
- **本地运行**：图片不会上传到任何服务器，断网也能用。
- **免费**：用的开源模型，没有次数限制、没有水印。

技术上是一个 .NET 控制台程序：加载 RMBG-2.0（BiRefNet）的 ONNX 权重，
经 **Windows ML** 提供的 ONNX Runtime 执行提供程序（Execution Provider, EP）推理后，
把模型输出的前景概率作为 Alpha 通道写回原图。

---

## 目录

- [1. 能做什么](#1-能做什么)
- [2. 快速开始](#2-快速开始)
- [3. 命令行参数完整参考](#3-命令行参数完整参考)
- [4. 效果调整指南](#4-效果调整指南)
- [5. 技术实现](#5-技术实现)
- [6. 引导式操作与模型下载器](#6-引导式操作与模型下载器)
- [7. 执行提供程序的安装与选择](#7-执行提供程序的安装与选择)
- [8. 运行环境与依赖](#8-运行环境与依赖)
- [9. 项目结构](#9-项目结构)
- [10. CI、发布与代码签名](#10-ci发布与代码签名)
- [11. 实测性能](#11-实测性能)
- [12. 故障排查](#12-故障排查)
- [13. 已知限制](#13-已知限制)
- [14. 隐私与费用](#14-隐私与费用)
- [15. 许可证](#15-许可证)

---

## 1. 能做什么

| 你想要的效果 | 怎么做 |
| --- | --- |
| 透明底图片（方便叠到任何背景上） | 默认就是这样 |
| 电商白底主图 | 加 `--bg #FFFFFF` |
| 发丝、绒毛等细软边缘 | 默认保留柔和边缘；想更柔和加 `--feather 2` |
| 一次处理一整个文件夹 | 直接把文件夹给它，自动逐张处理 |
| 单独的灰度蒙版（拿去 Photoshop 精修） | 加 `--mask both` |
| 网页用的小体积图 | 加 `--format webp` |

**输入格式**：PNG / JPEG / WebP / BMP / GIF / TIFF / TGA（含 `.jpe`、`.tif`、`.tga`、`.pbm`、`.qoi`）
**输出格式**：PNG（含透明通道）或 WebP；蒙版固定输出 8bit 灰度 PNG

单张、多张、整个目录、通配符（如 `*.jpg`）都支持，目录可递归。

---

## 2. 快速开始

### 2.1 你需要的

| 项目 | 要求 |
| --- | --- |
| 系统 | Windows 10 版本 1903（2019 年 5 月更新）或更新版本，64 位 |
| 运行环境 | 下载预编译版本则**无需安装任何东西**（已内置 .NET 运行时）；从源码构建需 .NET 8 SDK |
| 磁盘空间 | 约 1 GB（其中模型权重 490 MB） |
| 显卡 | 可选。有独显更快，没有也能用 CPU 处理 |

**先拿到程序**，两条路选一条：

| 方式 | 做法 |
| --- | --- |
| 下载预编译版本（推荐） | 到 [Releases](https://github.com/szdxs114514/rmbg-cli/releases/latest) 下载 `rmbg-cli-<版本>-win-x64.zip`（ARM64 设备用 `win-arm64`），**解压后直接运行 `rmbg.exe`**。这是自包含发布，已含 .NET 运行时，不需要再装别的东西 |
| 从源码构建 | 见 [2.5 从源码构建](#25-从源码构建)，需要自行安装 .NET SDK |

如果从源码构建后启动时提示缺少 .NET，去 https://dotnet.microsoft.com/download/dotnet/8.0 下载
**.NET Runtime** 下的 **Windows x64** 安装即可。

> 最低系统版本由 `Microsoft.Windows.AI.MachineLearning` 包强制要求（Windows 10 19H1 / build 18362），
> 且该要求在**构建期**就会校验，低于此版本会直接构建失败并提示替代方案。

> 模型权重体积较大（490 MB），没有打进发布包，首次使用需下载一次，见 [2.2](#22-最简单的用法双击运行) 与 [6.2](#62-模型下载器aria2-优先)。

### 2.2 最简单的用法：双击运行

拿到 `rmbg.exe` 之后，**双击它**就会进入**问答模式**（只有源码的话，先看 [2.5 从源码构建](#25-从源码构建)）。
程序一步步问你：

```
主菜单
  * 1) 开始抠图
    2) 下载 / 更换模型
    3) 安装 / 管理执行提供程序
    4) 查看环境与执行提供程序
    5) 查看完整命令行用法
    6) 退出
```

选 `1` 之后，它会依次问你要处理哪张图、结果存到哪里、要什么背景……每一项都有默认值，
**不认识的项目直接按回车**就能用推荐设置。全程不需要记任何参数。

第一次使用时会提示还没有模型，选「下载 / 更换模型」按提示下载即可。

> 小技巧：选择输入图片时，可以直接把文件从资源管理器**拖进窗口**，省得手打路径。
> 一次拖入多个文件也会被全部接受。

> 如果窗口里的中文显示成乱码，换用 [Windows Terminal](https://aka.ms/terminal) 打开即可。

### 2.3 命令行用法

```bash
rmbg -i photo.jpg -o photo_nobg.png
```

用命令行时，如果模型还没下载，程序会**直接告诉你该怎么做**：

```
还没有下载模型文件。

执行下面任意一条即可：
  rmbg --download-model       自动下载推荐模型（约 490 MB，只需一次）
  rmbg                        进入问答模式，按提示操作
```

对照着敲一遍就好，不需要去翻文档。下载只需一次，之后所有处理都不再需要联网。

### 2.4 常用场景（复制就能用）

```bash
# 1. 单张图片 → 透明底 PNG
rmbg -i 照片.jpg -o 结果.png

# 2. 商品图 → 白底（电商平台常用）
rmbg -i 商品.jpg -o 商品白底.png --bg #FFFFFF

# 3. 整批处理：把 photos 文件夹里所有图片抠出来，放到 cutouts 文件夹
rmbg -i .\photos -o .\cutouts

# 4. 连子文件夹一起处理，并且额外导出蒙版
rmbg -i .\photos -o .\cutouts --recursive --mask both

# 5. 只要某类文件
rmbg -i ".\photos\*.jpg" -o .\cutouts

# 6. 想体积更小（网页用）
rmbg -i 照片.jpg -o 结果.webp --format webp

# 7. 效果不对？主体和背景弄反了
rmbg -i 照片.jpg -o 结果.png --invert

# 8. 看看自己电脑能用哪些加速方式
rmbg --ep list

# 9. 安装 NPU / GPU 加速（可选，见第 7 章）
rmbg --install-ep all

# 10. 下载 / 重新下载模型
rmbg --download-model
rmbg --download-model fp32 --overwrite
```

### 2.5 从源码构建

```bash
# 需要 .NET SDK 8.0 或更高版本
dotnet publish src/RmbgCli -c Release -r win-x64

# 产物目录（其中的 rmbg.exe 即可直接分发）
src/RmbgCli/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/
```

ARM64 设备改用 `-r win-arm64`。

---

## 3. 命令行参数完整参考

```
用法:
  rmbg -i <输入> -o <输出> [选项]

输入与输出:
  -i, --input <路径>        输入图片、目录或通配符（如 "*.png"）；可重复指定多次
  -o, --output <路径>       输出文件（单张）或输出目录（多张）
  -r, --recursive           输入为目录/通配符时递归子目录
  -y, --overwrite           覆盖已存在的输出文件
      --suffix <字符串>     输出文件名后缀，默认 "_nobg"（设为 "" 则不加后缀）
      --format <png|webp>   输出格式，默认 png
      --bg <颜色>           合成到纯色背景而非透明
                            支持 #RRGGBB / #RRGGBBAA / transparent / white
                            / black / gray / red / green / blue

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
  -g, --guide               进入引导式操作（零参数运行时自动进入）
      --download-model [变体]
                            下载模型后退出，不传变体则使用 fp16
                            变体: fp16 | fp32 | int8 | quantized | uint8 | q4f16 | q4 | bnb4
      --model-dir <目录>    模型存放目录，默认 models\onnx
  -j, --connections <1..32> 下载并发连接数，默认 8
      --install-aria2       安装 aria2 便携版（单独使用时安装完即退出）
      --no-aria2            强制使用内置多线程下载器，不调用 aria2c

执行提供程序:
      --install-ep [目标]   安装 Windows ML 执行提供程序后退出
                            目标: all（默认）| qnn | vitisai | openvino
                                  | nvtensorrtrtx | migraphx | webgpu
      --allow-ep-install    允许 auto 模式自动下载安装缺失的 EP（默认关闭）

其它:
  -v, --verbose             输出详细诊断信息（模型张量、EP 选择过程、ORT 日志）
  -q, --quiet               只输出错误
  -h, --help                显示帮助
      --version             显示版本号
```

也可以直接运行 `rmbg --help` 查看同样的内容。

### 退出码

| 码 | 含义 |
| --- | --- |
| 0 | 全部成功（含"全部跳过"） |
| 1 | 部分失败（批量场景下其余文件已处理完成） |
| 2 | 命令行参数错误 |
| 3 | 环境/模型错误（模型缺失、EP 不可用、张量规格不符） |
| 4 | 推理失败 |
| 5 | 读写失败（路径无效、无权限、蒙版尺寸不符） |
| 130 | 用户 Ctrl+C 中断 |

---

## 4. 效果调整指南

程序默认输出**柔和边缘**的蒙版，这对发丝、绒毛、半透明物体最友好，**大多数情况不需要调**。

只有下面几种情况才需要动参数：

| 你的情况 | 建议 |
| --- | --- |
| 需要硬边（纯色 logo、剪影、图标） | `--threshold 0.5` |
| 边缘有一圈残留的背景色 | 先 `--feather 2`；不行再 `--threshold 0.5` |
| 主体和背景判断反了 | `--invert` |
| 图片主体特别小 / 特别大 | 不用管，程序自动适配 |
| 想要更高质量 | 下载更精细的模型并显式指定（见下） |

**换成更大更精细的模型**（fp32，约 977 MB，速度会慢一些）：

```bash
rmbg --download-model fp32                                      # 先下载
rmbg -i 照片.jpg -o 结果.png --model models\onnx\model.onnx      # 再显式指定
```

最后一步的 `--model` 不能省：程序默认优先使用体积更小的 `model_fp16.onnx`，
所以下载了 fp32 之后必须显式指向它，否则仍会走 fp16。

**速度**取决于硬件。有独立显卡通常一张图一秒以内到几秒；纯 CPU 可能要几十秒。
程序会自动选择最快的可用方式，不用手动配置。

想启用 NPU（新款笔记本的 AI 芯片）加速，运行 `rmbg --install-ep all`，
或从主菜单进入「安装 / 管理执行提供程序」。这是可选的，不装也能正常用。

---

## 5. 技术实现

### 5.1 模型 I/O 规格（对本仓库所用导出图实测确认）

| 项 | 值 |
| --- | --- |
| 输入名 | `pixel_values` |
| 输入类型 | `tensor(float)`，布局 NCHW = `[1, 3, H, W]` |
| 输入维度 | **H/W 为动态维度**（`dim_param` = `height` / `width`），可任意指定边长 |
| 输出名 | `alphas` |
| 输出类型 | `tensor(float)`，形状 `[1, 1, H, W]` |
| 输出语义 | **已是 Sigmoid 之后的 `[0,1]` 前景概率** |

> ⚠️ **关键陷阱：不要对输出再次施加 Sigmoid。**
> 该导出图的末端节点是 `Sigmoid`（fp16 / q4 变体后接 `Cast(to=FLOAT)`），
> 输出 `alphas` 已经是概率。若照搬官方 Python 样例里的 `.sigmoid()`，会把动态范围压缩到
> 约 `[0.5, 0.73]`，导致蒙版近乎全灰、边缘严重劣化。代码中已对此加了注释与校验。

上述规格是通过 HTTP Range 分段抓取 ONNX 文件的 protobuf 首尾解析得到的
（图输入/输出名位于文件尾部，节点名位于头部），无需下载完整权重即可确认。

### 5.2 预处理（与官方 `preprocessor_config.json` 逐项对齐）

| 步骤 | 参数 | 本实现 | 官方 Python 对应 |
| --- | --- | --- | --- |
| 尺寸 | 1024 × 1024 | `ResizeMode.Stretch`（拉伸，不保持宽高比） | `transforms.Resize((1024, 1024))` |
| 重采样 | `resample = 2` | `KnownResamplers.Triangle` | PIL `BILINEAR`（2 抽头，支撑 1.0） |
| 缩放 | `rescale_factor = 1/255` | `pixel * (1f/255f)` | `ToTensor()` |
| 归一化 | `do_normalize = true` | 见下 | `Normalize(...)` |
| 均值 | `[0.485, 0.456, 0.406]` | 同 | ImageNet 统计量 |
| 标准差 | `[0.229, 0.224, 0.225]` | 预取倒数，把除法降为乘法 | 同 |
| 通道顺序 | RGB | `Rgba32.R/G/B`（忽略源 Alpha，与 `convert('RGB')` 一致） | `image.convert('RGB')` |
| 布局 | NCHW | 先写满 R 平面，再 G、B | `unsqueeze(0)` |

若源图尺寸已等于目标边长，跳过重采样以避免无谓插值损失。

### 5.3 后处理

```
模型输出 float[0,1] (1024×1024)
  → 8bit 量化（对齐 PIL ToPILImage 的行为，ImageSharp L8）
  → 重采样回原图尺寸（默认 bicubic，对齐 PIL Image.resize 默认值）
  → 可选：反转 / 二值化(阈值) / 羽化(3 次可分离盒式滤波近似高斯)
  → 合成：dst.A = mask × src.A / 255
          transparent 模式 → 保留 RGB，写入 Alpha
          --bg 纯色模式  → out = (src × a + bg × (255-a)) / 255，A = bg.A
  → 原子落盘（临时文件 + File.Move 覆盖），避免中断留下半截文件
```

蒙版值与原图已有 Alpha 相乘，保证对半透明素材重复处理时不会"补回"已透明的区域。
图像载入时统一执行 `AutoOrient`，保证送模型的张量与最终输出的像素方向一致。

### 5.4 后处理参数的行为

| 参数 | 效果 |
| --- | --- |
| 默认（`--threshold 0`） | 保留软蒙版，边缘为连续过渡值 |
| `--threshold 0.5` | 二值化，半透明像素归零，得到硬边 |
| `--feather N` | 扩大半透明过渡带，N 越大边缘越柔 |
| `--invert` | Alpha 分布精确取反（前景与背景互换） |
| `--bg <颜色>` | 按 `out = fg×a + bg×(1-a)` 合成，输出不再透明 |

---

## 6. 引导式操作与模型下载器

### 6.1 引导式操作（`-g` / `--guide`）

- **触发方式**：显式传 `--guide`；或**不带任何参数**且检测到交互式终端时自动进入
  （若 stdin/stdout 被重定向则不会自动进入，避免脚本环境卡在等待输入）。
- **主菜单**：开始抠图 / 下载更换模型 / 安装管理执行提供程序 / 查看环境与执行提供程序 /
  查看完整用法 / 退出。
- **抠图向导**分 6 步，每步都有默认值与校验循环：
  1. 输入（支持拖拽文件、文件夹、通配符；一次拖入多个文件也会被全部接受）
  2. 输出（自动给出建议路径）＋ 是否递归子目录、是否覆盖已有
  3. 输出格式与合成背景
  4. 是否额外导出 8bit 灰度蒙版
  5. 边缘羽化半径、二值化阈值、是否反转蒙版
  6. 执行提供程序（自动 / DirectML / CPU）
- 执行前打印完整参数摘要并要求确认；执行结束后回到主菜单，可继续处理下一批。
- **Ctrl+C 语义**：在提问处按 Ctrl+C 立即退出；在推理/下载过程中按 Ctrl+C 走优雅取消
  （不会留下写了一半的输出文件）。

### 6.2 模型下载器（aria2 优先）

后端按以下顺序选择，任一环节失败都会自动降级，**不会因为缺少外部程序而无法下载**：

| 优先级 | 后端 | 特点 |
| --- | --- | --- |
| 1 | **aria2c** | 多连接（`-x/-s`，默认 16）+ 断点续传（`--continue`）+ 分片粒度 1 MiB；支持 `.aria2` 控制文件跨进程续传 |
| 2 | **内置多连接下载器** | 零外部依赖。用 `Range` 分片并行下载，`RandomAccess` 按偏移写入避免竞态，分段失败按指数退避重试并从该段断点续传；服务器不支持分片时退化为单流并支持 `.partial` 续传 |
| 3 | curl.exe | 仅 PowerShell 脚本使用（`aria2c` 与 `curl` 均缺失时提示手动下载） |

aria2 的定位顺序：`tools/aria2/aria2c.exe`（便携安装目录）→ `PATH`。
缺失时可用 `--install-aria2` 自动安装便携版，安装包来自官方 GitHub Release 并**校验 SHA256**：

```
URL    : https://github.com/aria2/aria2/releases/download/release-1.37.0/aria2-1.37.0-win-64bit-build1.zip
SHA256 : 67D015301EEF0B612191212D564C5BB0A14B5B9C4796B76454276A4D28D9B288
体积   : 2,475,379 字节（约 2.4 MB，aria2c.exe 为静态链接单文件）
```

校验不通过会**中止安装**并打印期望值与实际值，避免使用被镜像改写或篡改的二进制。
也可自行安装：`winget install aria2.aria2`。

> **为什么必须显式设置 `User-Agent`**：ModelScope 的 CDN（`cdn-lfs-*.modelscope.cn`）
> 会对缺少该头部的请求返回 403，且该 403 发生在跟随 302 跳转之后，表现为
> "浏览器能下载但程序报 403"。.NET 的 `HttpClient` 默认不发送 `User-Agent`，
> 而 curl / aria2 / Python 都会自动附带——因此只有自研下载器会踩到这个坑。

### 6.3 三个后端的实测结果

同一文件（`model_q4f16.onnx`，233,815,293 字节），16 连接：

| 路径 | 耗时 | 平均速率 | 产物 SHA256 前 16 位 |
| --- | --- | --- | --- |
| CLI + aria2c | 11 s | 20.3 MB/s | `8BFEB5F93220EB19` |
| CLI + 内置下载器（`--no-aria2`） | 8 s | 25.6 MB/s | `8BFEB5F93220EB19` |
| PowerShell 脚本 + aria2c | — | — | `8BFEB5F93220EB19` |

三者**逐字节一致**（SHA256 完全相同），说明分片边界与写入偏移实现正确。

### 6.4 模型变体与定位顺序

来源：**ModelScope**（`https://www.modelscope.cn/models/AI-ModelScope/RMBG-2.0`），国内可直连。

| 变体 | 文件 | 体积 | 建议用途 |
| --- | --- | --- | --- |
| fp16 | `onnx/model_fp16.onnx` | 490 MB | **默认推荐**：质量/速度/体积平衡，GPU/NPU 友好 |
| fp32 | `onnx/model.onnx` | 977 MB | 参考导出，数值最保守；CPU 上最稳妥 |
| int8 / quantized | `onnx/model_int8.onnx` | 349 MB | CPU 专用场景 |
| q4f16 | `onnx/model_q4f16.onnx` | 223 MB | 体积极小，细节边缘有质量损失 |

**定位顺序**：

1. 命令行 `--model <路径>`（可为文件或目录）
2. 环境变量 `RMBG_MODEL`
3. 从当前目录与程序目录向上回溯 9 层，查找 `models\onnx`、`models`、`onnx` 目录

候选文件优先顺序：`model_fp16.onnx` → `model.onnx` → `model_fp32.onnx` → `model_bf16.onnx`
→ `model_q4f16.onnx` → `model_int8.onnx` → `model_quantized.onnx` → `model_uint8.onnx`；
都不匹配时取目录内体积最大的 `.onnx`。

定位阶段会检查文件体积与首字节（ONNX protobuf 的 `ir_version` 字段应为 `0x08`），
**能识别 Git LFS 指针文件**（最常见的下载失败形态）并给出明确提示。

---

## 7. 执行提供程序的安装与选择

Windows ML 里 **CPU 与 DirectML 是随运行时内置的**（"包含的执行提供程序"，始终可用）；
其余 EP **不在运行时里，必须按需下载安装**，否则 `auto` 模式永远只能回退到 DirectML/CPU。

### 7.1 可安装的 EP 一览

要求 **Windows 11 24H2（build 26100）或更高版本**，且硬件/驱动满足下表：

| EpName | 供应商 | 硬件 | 要求 |
| --- | --- | --- | --- |
| `QNNExecutionProvider` | 高通 Qualcomm | NPU | Snapdragon X Elite / X Plus，Hexagon NPU 驱动 ≥ 30.0.140.0 |
| `VitisAIExecutionProvider` | AMD | NPU | Ryzen AI；Adrenalin 25.6.3–25.9.1 + NPU 驱动 32.00.0203.280–297 |
| `OpenVINOExecutionProvider` | 英特尔 Intel | NPU / GPU / CPU | 11 代 Core 及以上；NPU 需 Core Ultra 系列 1 及以上 |
| `NvTensorRtRtxExecutionProvider` | NVIDIA | GPU | GeForce RTX 30 系及以上，驱动 ≥ 32.0.15.5585 + CUDA 12.5 |
| `MIGraphXExecutionProvider` | AMD | GPU | RDNA 3 及以上，驱动 ≥ 25.10.13.09（不支持 GenAI 场景） |
| `WebGpuExecutionProvider` | Microsoft | GPU | 实验性；需 `Microsoft.Windows.AI.MachineLearning` ≥ 2.4.66-preview |

使用前请阅读各 EP 对应的许可条款（AMD/NVIDIA/Intel/Qualcomm 的 SDK 许可各不相同）。

### 7.2 官方三步流程与本工具的对应实现

```
① 安装    ExecutionProviderCatalog.EnsureReadyAsync()          → --install-ep
② 注册    RegisterCertifiedAsync() / provider.TryRegister()    → 每次运行自动执行
③ 选择    OrtEnv.GetEpDevices() + SessionOptions.AppendExecutionProvider(env, devices, opts)
                                                               → auto / --ep winml
```

**ReadyState 状态机**（决定了每一步该调什么）：

| 状态 | 含义 | 应执行的动作 |
| --- | --- | --- |
| `NotPresent` | 本机未安装 | `EnsureReadyAsync()` — **下载并安装**，同时加入应用运行时依赖图 |
| `NotReady` | 已安装，但未加入依赖图 | `EnsureReadyAsync()` — 仅加入依赖图（本地操作，不下载） |
| `Ready` | 已安装且已加入依赖图 | `TryRegister()` — 注册到 ONNX Runtime |

关键点：**只有注册之后，该 EP 才会出现在 `OrtEnv.GetEpDevices()` 里**；
`GetEpDevices()` 的输出因此也是"安装+注册是否真的生效"的唯一可信判据。

### 7.3 安装策略：绝不静默下载

`NotPresent` 的 EP **只有在用户显式同意时才会下载**。这样做是有意的：

- EP 安装包体积可观，首次安装可能需要数分钟；
- 安装是**系统级**行为（写入登记表、被其它应用共享），不应由工具擅自决定。

因此：

| 场景 | 行为 |
| --- | --- |
| `rmbg --ep list` | 只读：列出已安装/可安装的 EP、要求、已注册的 EP 与硬件清单 |
| `rmbg --install-ep all` | 显式同意：安装本机所有兼容的 EP |
| `rmbg --install-ep qnn` | 显式同意：只安装指定 EP |
| `rmbg -i x.png -o y.png` | **不安装**。若存在可安装的 EP，只提示可用 `--install-ep` |
| `rmbg ... --allow-ep-install` | 显式授权 auto 模式在推理前安装缺失的 EP（供脚本/CI 使用） |
| 引导式操作 | 第 6 步会先展示可安装的 EP 并询问是否安装；主菜单另有「安装 / 管理执行提供程序」 |

### 7.4 候选构建与设备级选择

注册完成后，本工具用官方推荐的**设备级显式选择**：

```csharp
OrtEnv env = OrtEnv.Instance();
var devices = env.GetEpDevices();                      // 注册后才看得到 Windows ML EP
var device  = devices.First(d => d.EpName == "QNNExecutionProvider"
                              && d.HardwareDevice.Type == OrtHardwareDeviceType.NPU);
sessionOptions.AppendExecutionProvider(env, new[] { device }, new Dictionary<string, string>());
```

相比按名称追加，设备级选择的好处是能**区分同一 EP 的不同硬件实例**
（例如 QNN 同时提供 NPU 与 GPU 设备，我们要的是 NPU）。

`--ep auto`（默认）按以下顺序构造候选，**逐个尝试创建会话，第一个成功的生效**：

1. **已安装的 Windows ML EP** —— 先把本机已安装的 EP 加入依赖图并注册
   （`RegisterCertifiedAsync()`，不触发下载），再用 `OrtEnv.GetEpDevices()` 枚举设备。
   按 `硬件类型（NPU 30 > GPU 20 > CPU 10）` 降序，同硬件类型内按供应商优先级
   （QNN 100 > VitisAI 95 > OpenVINO 90 > NvTensorRtRtx 85 > MIGraphX 80 > WebGpu 60）排序。
2. **DirectML**（`AppendExecutionProvider_DML(deviceId)`）—— 内置 EP；仅当 ORT 报告存在
   `DmlExecutionProvider` 设备时才加入候选。
3. **CPU**（`AppendExecutionProvider_CPU`）—— 始终可用。

`--ep winml` 只保留第 1 类，且在没有可用的 Windows ML EP 时**报错并直接列出可安装项与安装命令**；
`--ep dml` / `--ep cpu` 各自锁定单一后端。

之所以采用"候选列表 + 逐个试创建会话"而非直接选一个 EP，是因为 **EP 能否真正工作只有在创建
`InferenceSession` 时才能确定**（例如机器有 GPU 但显卡不支持 D3D12、NPU 驱动版本不符）；
而且官方明确提示 **EP 设备列表会在运行时动态变化**（EP 自动更新或驱动更新），
设备消失时必须能自动降级而不是直接失败。

### 7.5 图优化降级重试（绕过 ORT 缺陷）

每个候选会先以 `ORT_ENABLE_ALL` 尝试，若失败且错误特征属于 ORT 图优化问题，
则自动降级到 `ORT_ENABLE_BASIC` 重试。这用于绕过 ONNX Runtime 的一个已知缺陷：

> float16 导出模型中的 `InsertedPrecisionFreeCast_*` 节点会让 CPU EP 的
> `SimplifiedLayerNormFusion` 在会话初始化阶段抛
> `GetIndexFromName ... a name which does not exist`。

降级后 fp16 模型在 CPU 上也可正常推理。运行时日志默认屏蔽
（`ORT_LOGGING_LEVEL_FATAL`）以避免动态维度导出产生的大量
`Shape mismatch attempting to re-use buffer` 警告污染输出；`--verbose` 会放开到 `WARNING`。
异常信息本身不受影响，仍通过 `OnnxRuntimeException.Message` 完整呈现。

### 7.6 实测：本机（AMD 核显 + 无 NPU 驱动）

```
$ rmbg --ep list
Windows ML 执行提供程序目录（含未安装项）
  · AMD MIGraphX（GPU） · MIGraphXExecutionProvider
      状态    : 未安装（NotPresent）
      认证    : Certified
      要求    : RDNA 3 及以上，驱动 ≥ 25.10.13.09（不支持 GenAI 场景）
  有 1 个 EP 尚未安装。安装命令：
    rmbg --install-ep all

ONNX Runtime 已注册的执行提供程序
  · DirectML GPU（内置）      · CPU（内置）

ONNX Runtime EP 设备
  · CPUExecutionProvider  硬件=CPU     · DmlExecutionProvider  硬件=GPU ×2

本机硬件设备
  · GPU x2  Advanced Micro Devices, Inc. (vendorId=0x1002, deviceId=0x1900)
  · CPU x1  AMD (vendorId=0x1022, deviceId=0x0007)
```

`--install-ep all` 会调用 `EnsureReadyAsync()` 真实尝试安装，本机结果：

```
✘ AMD MIGraphX（GPU） · MIGraphXExecutionProvider — 安装失败：产品不适用或找不到。
    | 诊断：Failed to ensure provider ready.
    该 EP 的硬件要求：RDNA 3 及以上，驱动 ≥ 25.10.13.09（不支持 GenAI 场景）
    安装失败通常意味着本机硬件或驱动不满足上述要求，可改用 --ep dml 或 --ep cpu。
```

这正是预期行为：本机是较老的 AMD 核显（`deviceId=0x1900`），不满足 RDNA 3 要求，
Windows 因此拒绝安装。工具把 `ExtendedError` 与 `DiagnosticText` 原样透出，
并补上该 EP 的硬件要求（Windows 的原文"产品不适用或找不到"对用户没有指导意义），
退出码为 3。

---

## 8. 运行环境与依赖

### 8.1 目标运行环境

| 项目 | 取值 | 说明 |
| --- | --- | --- |
| 目标框架 | `net8.0-windows10.0.19041.0` | .NET 8 为 LTS；需 Windows SDK 投影（由 SDK 自动引入） |
| 最低 OS | Windows 10 19H1（build 18362） | 由 `Microsoft.Windows.AI.MachineLearning` 强制要求，构建期即校验 |
| 目标架构 | `win-x64`（默认）/ `win-arm64` | 通过 RID 选择 |
| 构建工具链 | .NET SDK 8.0+ | 实测使用 .NET SDK 10.0.300 构建 net8.0 目标 |
| 运行时 | .NET 8 运行时 | 框架依赖部署（`SelfContained=false`） |

> **为什么必须指定 RID**：本包通过 NuGet 的 `runtimes/{rid}/native` 机制部署原生库
> （`onnxruntime.dll`、`DirectML.dll`、`Microsoft.Windows.AI.MachineLearning.dll`）。
> 不指定 RID 时这些原生库不会被复制到输出目录，程序启动即失败。

### 8.2 依赖包

| 包 | 版本 | 作用 |
| --- | --- | --- |
| `Microsoft.Windows.AI.MachineLearning` | 2.3.42 | **核心依赖**。提供：① Windows ML 的 EP 目录（WinRT 投影 `ExecutionProviderCatalog`）；② 托管 ONNX Runtime 绑定 `Microsoft.ML.OnnxRuntime.dll`；③ 原生 `onnxruntime.dll`（内置 ONNX Runtime **1.27.1**）；④ `DirectML.dll`（18 MB） |
| `SixLabors.ImageSharp` | 3.1.12 | 纯托管图像解码/编码/重采样；支持 PNG、JPEG、WebP、BMP、GIF、TIFF、TGA 等 |

**不要额外引用 `Microsoft.ML.OnnxRuntime` 或 `Microsoft.ML.OnnxRuntime.DirectML`**：
Windows ML 包已自带同名的托管绑定与原生 `onnxruntime.dll`，重复引用会导致原生库冲突
（两个不同版本的 `onnxruntime.dll` 争抢加载）。

**关于 ImageSharp 为什么停在 3.x**：`Microsoft.Windows.AI.MachineLearning` 最新稳定版就是
2.3.42（更高只有 `2.6.x-rc` 预览版），已是最新。而 ImageSharp 的**稳定版已到 4.1.2**，
但 4.x 起改为商业授权，构建期会强制要求许可证，缺失就直接编译失败：

```
error : No Six Labors license found. Set $(SixLaborsLicenseKey), set $(SixLaborsLicenseFile),
        or add a 'sixlabors.lic' file to the project/workspace.
error : Please obtain a license from https://sixlabors.com/pricing/
```

所以本项目有意停留在 **3.1.12**——这是免费授权（Six Labors Split License）线路上的最高版本。
除非你持有 ImageSharp 商业许可证，否则不要升级到 4.x；`dependabot.yml` 里也已显式忽略
`>=4.0.0`，避免反复收到无法合并的升级 PR。

原生库在输出目录中的部署形态：

```
bin/Debug/net8.0-windows10.0.19041.0/win-x64/
├─ rmbg.exe
├─ Microsoft.Windows.AI.MachineLearning.dll        890 KB
├─ Microsoft.Windows.AI.MachineLearning.Projection.dll
├─ Microsoft.ML.OnnxRuntime.dll                    237 KB（托管绑定）
├─ onnxruntime.dll                                  23 MB（原生，含 CPU/DML 等 EP）
├─ DirectML.dll                                     18 MB
├─ SixLabors.ImageSharp.dll
└─ System.Numerics.Tensors.dll
```

---

## 9. 项目结构

```
RmbgCli/
├─ RmbgCli.sln                     解决方案
├─ README.md                       本文档
├─ .gitignore
├─ .gitattributes                  换行规范化规则
├─ .github/
│  ├─ workflows/
│  │  ├─ ci.yml                    CI：编译 + 依赖审计 + 退出码契约测试 + 双 RID 发布包校验
│  │  └─ release.yml               发布：打标签出包 + 可选 SignPath 签名
│  ├─ dependabot.yml               每周检查 NuGet / Actions 更新（忽略 ImageSharp 4.x）
│  └─ release.yml                  定制 Release Notes 分类
├─ .signpath/
│  └─ artifact-configuration.xml   SignPath 制品配置（版本控制副本，见第 10 章）
├─ scripts/
│  └─ Get-RmbgModel.ps1            从 ModelScope 下载 ONNX 权重（优先 aria2，回退 curl）
├─ models/
│  └─ onnx/
│     └─ model_fp16.onnx           模型权重（不入库，由脚本或 --download-model 下载）
├─ tools/
│  └─ aria2/                       aria2 便携版（由 --install-aria2 自动安装，不入库）
└─ src/
   └─ RmbgCli/
      ├─ RmbgCli.csproj            依赖与构建配置
      ├─ Program.cs                入口：模式分发 + 编排 + Ctrl+C 处理 + 汇总 + 退出码
      ├─ Cli/
      │  ├─ AppOptions.cs          选项模型与枚举（EpMode / MaskOutputMode / MaskResampler）
      │  ├─ CommandLineParser.cs   手写解析器（不依赖预览版 System.CommandLine）
      │  ├─ ConsolePrompt.cs       交互提问原语（编号菜单、路径拖拽识别、校验循环）
      │  ├─ InteractiveWizard.cs   引导式操作主流程
      │  ├─ HelpText.cs            帮助与版本文本
      │  ├─ ExitCode.cs            退出码枚举 + RmbgException
      │  └─ Logger.cs              分级彩色输出（quiet / verbose / 单行进度）
      ├─ Core/
      │  ├─ ModelCatalog.cs        模型变体清单与直链（CLI 与 PS1 的唯一数据源）
      │  ├─ ModelDownloader.cs     下载编排：aria2 → 内置下载器
      │  ├─ Aria2Backend.cs        aria2 定位、调用、便携版安装与 SHA256 校验
      │  ├─ ParallelDownloader.cs  内置多连接分片下载器（零外部依赖）
      │  ├─ ModelLocator.cs        模型定位 + LFS 指针/截断文件检测
      │  ├─ KnownExecutionProviders.cs  EP 元数据表（名称/供应商/硬件/要求/优先级）
      │  ├─ EpProvisioner.cs       EP 目录枚举、安装（EnsureReadyAsync）、注册、设备查询
      │  ├─ ExecutionProviders.cs  EP 候选构建（设备级选择）、环境自检输出
      │  ├─ RmbgSession.cs         会话创建（EP 回退 + 图优化降级）、张量元数据校验、推理
      │  ├─ BackgroundRemover.cs   单张图片流程编排与耗时分解
      │  └─ BatchPlanner.cs        输入展开（文件/目录/通配符）与输出路径推导
      └─ Imaging/
         ├─ RmbgPreprocessor.cs    缩放 → 归一化 → NCHW float32
         ├─ RmbgPostprocessor.cs   量化 → 回缩放 → 二值化/羽化 → 合成
         └─ ImageFile.cs           载入（含 EXIF 自动摆正）与原子落盘
```

**执行顺序设计**：

```
参数解析 → 输入/输出展开与校验 → 模型定位 → EP 选择与会话创建 → 逐张处理 → 汇总
```

路径校验刻意**前置**于模型加载：否则一次路径拼错的调用要白等一次完整模型加载与 EP 初始化
（实测约 1.5 s 加载 + 首次推理预热）。输出目录可写性也在处理任何图片前用探针文件检查。

---

## 10. CI、发布与代码签名

### 10.1 工作流一览

| 工作流 | 触发条件 | 做什么 |
| --- | --- | --- |
| [`ci.yml`](.github/workflows/ci.yml) | push 到 `main`、PR、手动 | 编译（警告视为错误）→ 依赖漏洞审计 → CLI 退出码契约冒烟测试 → 两个 RID 的发布包校验 |
| [`release.yml`](.github/workflows/release.yml) | 推送 `v*` 标签、手动指定标签 | 解析版本号 → 按 RID 并行打包 → （可选）SignPath 签名 → 签名校验 → 创建 GitHub Release |

另有 [`dependabot.yml`](.github/dependabot.yml)：每周一 04:00（东八区）检查 NuGet 与
GitHub Actions 的版本更新，同类更新合并成一个 PR。它显式忽略 `SixLabors.ImageSharp >= 4.0.0`，
原因见 [8.2 依赖包](#82-依赖包)。

### 10.2 CI 具体检查什么

**编译与依赖审计**

- `dotnet build -warnaserror`：项目当前 0 警告，把警告升级为错误可以防止质量随时间退化。
- 依赖漏洞审计调用 `dotnet list package --vulnerable --include-transitive`，
  但**解析 JSON 而不是匹配输出文本**——后者会随 dotnet CLI 的界面语言（中文/英文）变化而失效。

**退出码契约冒烟测试**

CI 逐条核对 [第 3 章](#退出码) 承诺的退出码，共 11 条断言：

| 场景 | 期望退出码 |
| --- | --- |
| `--version` / `--help` / `--ep list` | 0 |
| 未知选项、缺少输出路径、未知模型变体、非法枚举值、`--size` 非 32 倍数 | 2 |
| 输入路径不存在 | 5 |
| 模型文件不存在 | 3 |

两个踩过的实现细节：

- **必须容忍 stderr**。被测试的程序会把错误与警告写到 stderr，而这里期望的正是非零退出码；
  在 PowerShell 7.3+ 需要 `$PSNativeCommandUseErrorActionPreference = $false`，
  更早的版本（含 5.1）还要在调用期间临时把 `$ErrorActionPreference` 设为 `Continue`。
  否则 stderr 会被当成终止性错误，测试就会假失败。
- **要触达"模型文件不存在"，输入必须是一张能解码的图片**——因为输入路径校验与格式筛选
  都在模型解析之前完成（这是有意的：无效路径不该白等一次模型加载）。
  本仓库刻意不含任何测试图片，所以 CI 会在 runner 的临时目录里现造一个 2×2 PNG。
  它只存在于运行期，不进仓库、不进产物。

**发布包校验（`win-x64` 与 `win-arm64`）**

两个 RID 各自跑在对应的**原生**运行器上（`windows-latest` / `windows-11-arm`），
所以 arm64 产物是真的被执行过，而不是只证明"能打包"。检查项：

- 逐个断言 7 个关键文件的存在：`rmbg.exe`、`rmbg.dll`、`onnxruntime.dll`、`DirectML.dll`、
  `Microsoft.Windows.AI.MachineLearning.dll`、`Microsoft.ML.OnnxRuntime.dll`、`SixLabors.ImageSharp.dll`
- 真实启动一次 `rmbg.exe --version`
- 校验压缩包**根目录**就是文件本身（不能多一层文件夹，否则签名配置会匹配不上）

> 自包含发布能编译成功、却因为找不到原生库而启动失败，是这类项目的典型故障，
> 只有真的执行一次才能发现。

**有意不做的**：端到端推理测试。那需要 490 MB 的模型权重和一张判读用的图片，
不适合每次 push 都跑。推理正确性请按 [11. 实测性能](#11-实测性能) 里的方法用自备图片在本地验证。

### 10.3 发布流程

```bash
git tag v1.0.0
git push origin v1.0.0
```

`release.yml` 随后：

1. 从标签解析版本号（`v1.0.0` → `1.0.0`）；标签格式不符会带着明确提示失败；
2. 按 RID 并行发布，并把版本号写进程序集（`-p:Version=`）；发布前用 `rmbg.exe --version`
   回读核对，避免"标签是 1.0.1、程序却自称 1.0.0"这种静默不一致；
3. 打包为 `rmbg-cli-<版本>-<RID>.zip`，压缩包根目录直接是文件；
4. 提交 SignPath 签名（若已配置），然后把产物统一成稳定命名；
5. 校验签名状态，最后用 `gh release create` 发布，并附带 `SHA256SUMS.txt`。

手动触发时需在 Actions 页面填写标签；勾选 `skip_signing` 可临时跳过签名。

### 10.4 代码签名（SignPath）

发布包默认**未签名**。配好下面这些内容后签名即自动生效，工作流无需改动：

| 类型 | 名称 | 说明 |
| --- | --- | --- |
| Secret | `SIGNPATH_API_TOKEN` | SignPath 中具备提交权限的 API 令牌 |
| Variable | `SIGNPATH_ORGANIZATION_ID` | SignPath 组织 ID |
| Variable | `SIGNPATH_PROJECT_SLUG` | 项目 slug |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | 签名策略 slug（如 `release-signing`） |
| Variable | `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG`（可选） | 制品配置名，默认 `artifact-configuration` |

在仓库 **Settings → Secrets and variables → Actions** 中配置。只要 `SIGNPATH_API_TOKEN`
或任一 variable 为空，签名步骤就会被跳过并输出一条明确的 warning，发布流程本身不会失败。

**制品配置需要在 SignPath 网站的项目里创建**（`Project → Artifact Configurations`）。
纳入版本控制的副本见 [`.signpath/artifact-configuration.xml`](.signpath/artifact-configuration.xml)：

```xml
<artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
  <zip-file>
    <pe-file path="rmbg.exe">
      <authenticode-sign/>
    </pe-file>
  </zip-file>
</artifact-configuration>
```

只签 `rmbg.exe`。压缩包内的 .NET 运行时、`onnxruntime.dll`、`DirectML.dll`、
`SixLabors.ImageSharp.dll` 等**都是第三方组件，不应使用本项目的证书签名**——
这也是 SignPath 文档明确建议的做法。

签名产物在合并进 Release 前会被校验：解出 `rmbg.exe` 读取 Authenticode 状态。
若签名流程报告成功但文件仍是未签名状态，构建直接失败。

> SignPath 免费的开源签名名额要求仓库先带有 OSI 认可的开源许可证，见 [15. 许可证](#15-许可证)。

---

## 11. 实测性能

测试机为 Windows 10.0.29667（虚拟化 GPU，无独立显卡、无 NPU），模型 `model_fp16.onnx`，
输入 `--size 1024`：

| EP | 预处理 | 推理 | 后处理 | 备注 |
| --- | --- | --- | --- | --- |
| DirectML（device 0） | 52–127 ms | 5.1–12.4 s | 155–328 ms | 首次 12 s，之后因内核缓存降至约 5 s |
| CPU（MLAS，4 线程，图优化降级为 BASIC） | 109 ms | 23.4 s | 249 ms | 降级重试自动生效 |

> 上述推理耗时明显高于同代独显（通常 0.5–2 s），原因是测试环境为虚拟化 GPU。
> DirectML 与 CPU 的输出**逐像素一致**（平均绝对差 0.00/255，最大 3/255），说明管线具备确定性。

---

## 12. 故障排查

**提示还没有下载模型**
模型还没下载。运行 `rmbg --download-model`，或直接运行 `rmbg` 在问答模式里选「下载 / 更换模型」。

**提示模型文件体积异常**
下载中断了，拿到的是不完整的文件。加上 `--overwrite` 重新下载：
`rmbg --download-model --overwrite`

**提示缺少 .NET / 双击没反应**
没装 .NET 8 运行时，见 [2.1 你需要的](#21-你需要的)。

**处理很慢**
纯 CPU 处理本来就慢。如果电脑有独立显卡，运行 `rmbg --ep list` 看看有没有识别到；
若显示只有 CPU，可能是显卡驱动太旧，更新驱动后重试。

**某个图片报错，其他都正常**
通常是那张图片损坏或格式不受支持，跳过它即可，程序会继续处理其余图片（退出码 1）。

**处理完没有透明背景**
如果用了 `--bg` 参数，输出就是纯色底而不是透明底。去掉 `--bg` 即可。

**想恢复被覆盖的文件**
程序默认**不覆盖**已存在的输出文件（会提示跳过）。只有显式加 `--overwrite` 才会覆盖。

**中文显示成乱码**
换用 Windows Terminal，或在 PowerShell 里先执行 `chcp 65001`。

### 按现象对照的排查表

| 现象 | 原因 | 处理 |
| --- | --- | --- |
| `还没有下载模型文件` | 未下载模型或路径不符 | `rmbg --download-model`，或用 `--model <路径>` 指定 |
| 模型文件体积异常（几十~几百字节） | 下载到的是 Git LFS 指针或重定向页面 | 用 `--overwrite` 重新下载；定位阶段已内置首字节与体积校验 |
| `Windows ML 中没有可用的执行提供程序` | 未安装 NPU/GPU 的 Windows ML 驱动包 | 用 `--install-ep all` 安装，或 `--ep list` 查看可安装项；也可用 `--ep dml` |
| `--install-ep` 报"产品不适用或找不到" | 本机硬件/驱动不满足该 EP 的要求 | 工具会一并打印该 EP 的硬件要求；不满足时改用 `--ep dml` 或 `--ep cpu` |
| `--ep list` 说某 EP 未安装但硬件够 | 系统版本低于 Windows 11 24H2（build 26100） | 升级系统；或按官方「自带 EP」方式把 EP 打进应用 |
| `--ep winml` 报错但没有可安装项 | 硬件类别不匹配（如非骁龙平台装不了 QNN） | 这是正常结果；`rmbg --ep list` 会列出本机实际可用的后端 |
| `所有执行提供程序都创建会话失败` | 显卡不支持 D3D12 / 驱动版本不符 | `--ep cpu` 强制 CPU；或 `--verbose` 查看各候选失败原因 |
| 图片解码失败 / 无法识别的图片格式 | 文件损坏或格式不受支持 | 用受支持的格式（PNG/JPEG/WebP/BMP/GIF/TIFF/TGA） |
| 输出目录不可写 | 权限或路径非法 | 检查 `-o` 指向的目录；程序处理前已用探针文件预检 |
| 下载探测失败 `HTTP 403 Forbidden` | 服务端要求请求携带 `User-Agent` | 内置下载器已强制设置该头部；自研客户端需自行补上 |
| aria2 安装校验失败 | 镜像/代理改写了安装包内容 | 程序会打印期望与实际 SHA256 并中止；可手动下载后把 `aria2c.exe` 放入 `tools\aria2\` |

---

## 13. 已知限制

1. **仅支持单张串行推理**。未做批处理（batch>1）张量拼接，因为 RMBG-2.0 的导出图批维度虽为
   动态，但实测在 DirectML 上多批次收益有限且显存占用显著上升。
2. **`--size` 必须为 32 的倍数**。BiRefNet 主干含 5 级下采样，非 32 倍数会破坏窗口切分假设；
   程序在参数解析阶段即拒绝。
3. **不支持 EXR / RAW / HEIC**。受 ImageSharp 解码器范围限制。
4. **蒙版量化到 8bit**。与官方 Python 实现（`ToPILImage`）保持一致，但对超高动态范围场景属有损。
5. **`--feather` 的等效 σ 约为半径的 1.2 倍**（3 次盒式滤波近似），非精确高斯参数。
6. `Microsoft.Windows.AI.MachineLearning` 要求 Windows 10 19H1+；更早系统需改用
   `Microsoft.WindowsAppSDK.ML`（框架式部署，需 MSIX 应用上下文）。
7. **内置下载器的分片模式不支持跨进程续传**。分片边界状态未持久化，进程重启后已有
   `.partial` 会被丢弃并重新下载；需要大文件断点续传时用 aria2（其 `.aria2` 控制文件会记录
   每个分片的完成状态）。单流模式支持基于 `.partial` 的续传。
8. **引导式操作要求交互式终端**。当 stdin/stdout 被重定向（脚本、CI）时不会自动进入，
   需显式传 `--guide`，且需要预先提供完整输入序列。
9. **aria2 便携版仅内置 Windows x64 版本**。ARM64 设备建议用 `winget install aria2.aria2`
   或自行放置 `tools\aria2\aria2c.exe`。
10. **EP 的动态下载安装需要 Windows 11 24H2（build 26100）及以上**，且硬件必须满足对应
    厂商要求；不满足时 Windows 会直接拒绝安装（表现为"产品不适用或找不到"）。
    受限网络、离线或需要固定版本的环境，应改用官方「自带 EP」方式把 EP 随应用分发。
11. **EP 安装是系统级行为**。安装后的 EP 会被同机其它应用共享，且可能随 Windows 自动更新。
    这既是优点（一次安装多处受益），也意味着本工具不会在未经确认的情况下安装。
12. **目前一键下载模型只能从ModelScope下载（因为Hugging Face的原版模型是Gated Model）**，如果需要指定BRIA AI原版需要自行下载并且指定模型路径
---

## 14. 隐私与费用

- **图片不出本机**。所有推理都在你的电脑上完成，程序只在首次需要时联网下载模型。
- **完全免费**。模型是开源权重，工具本身也没有任何联网校验、账号或次数限制。
- 唯一的外部依赖是首次的模型下载（国内可从 ModelScope 直连，不需要代理）；
  若你主动选择安装执行提供程序，还会从 Windows 官方更新源下载 EP 包。

### 相关链接

- 模型来源：[ModelScope · AI-ModelScope/RMBG-2.0](https://www.modelscope.cn/models/AI-ModelScope/RMBG-2.0)
  （RMBG-2.0 / BiRefNet，由 BRIA AI 发布，镜像自 https://huggingface.co/briaai/RMBG-2.0 ）
- Windows ML 文档：[执行提供程序](https://learn.microsoft.com/windows/ai/new-windows-ml/supported-execution-providers) ·
  [安装 EP](https://learn.microsoft.com/windows/ai/new-windows-ml/initialize-execution-providers) ·
  [注册 EP](https://learn.microsoft.com/windows/ai/new-windows-ml/register-execution-providers) ·
  [选择 EP](https://learn.microsoft.com/windows/ai/new-windows-ml/select-execution-providers)

---

## 15. 许可证

Apache-2.0

请注意模型权重本身另有其授权条款（[RMBG-2.0 / BRIA](使用CC BY-NC 4.0，商业用途需与 BRIA 签订商业协议)），
与本仓库代码的许可证相互独立。
