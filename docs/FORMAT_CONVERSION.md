# 格式转换

所有转换在本机执行，不调用在线服务或 AI。

## 使用

在左侧启用“格式转换”，选择类型和输出格式，添加或拖入文件，选择保存位置后开始。每批最多 100 个文件，依次转换；原文件只读，同名输出自动更名。失败或取消的项目可以再次点击“开始转换”重试，已经完成的项目不重复执行。

文件清单保留在本次运行中，重启后不恢复转换历史；输出位置、格式和本地引擎设置可以保存。

关闭或收起主窗口不打断已经提交的转换。停用功能会取消本批剩余任务，等待本次转换释放文件、流、编解码对象和自有进程；已经完成的结果保留。退出软件沿用现有任务确认与保存保护流程。

## 格式与本地依赖

| 类型 | 输入 | 输出与说明 |
| --- | --- | --- |
| 图片 | PNG、JPG/JPEG、BMP、TIFF、静态 GIF | PNG、JPG、BMP、TIFF、PDF；使用 Windows 图片组件 |
| WebP | 静态 WebP，以及上述图片 | WebP 与其他图片格式互转，需要本地 FFmpeg |
| PDF | PDF | 每页输出 PNG 或 JPG，可选 72–300 DPI；使用 Windows PDF 组件 |
| 音频 | MP3、M4A、AAC、WAV、FLAC、OGG、WMA、Opus 及受支持视频中的音轨 | MP3、M4A、WAV 可使用 Windows 编解码器；FLAC、OGG 需要 FFmpeg |
| 视频 | MP4、M4V、MKV、MOV、AVI、WebM、WMV、MPEG | MP4、WMV 可使用 Windows 编解码器；MKV、WebM 需要 FFmpeg。实际输入支持取决于系统或所选引擎的解码器 |
| 文字 | DOCX、ODT、TXT | PDF、DOCX、ODT、TXT，需要 LibreOffice |
| 表格 | XLSX、ODS、CSV | PDF、XLSX、ODS、CSV，需要 LibreOffice；CSV 是单表文本格式，不保留多表、公式和样式 |
| 演示文稿 | PPTX、ODP | PDF、PPTX、ODP，需要 LibreOffice |

Windows N 等缺少媒体组件的系统，以及系统无法解码的输入，可以选择本地 FFmpeg。FFmpeg 构建需要包含所选编码器，例如 MP4 使用 libx264、WebP 使用 libwebp、WebM 使用 libvpx-vp9/libopus。配置 FFmpeg 后，音视频统一使用该引擎。

“本地引擎”只选择本机已有的 `ffmpeg.exe`、LibreOffice `program/soffice.exe` 或 `soffice.com`，不下载、安装或自动更新引擎。当前程序没有捆绑这两项依赖；准备好本地引擎后，转换过程不需要互联网。参考 [FFmpeg 官方下载说明](https://ffmpeg.org/download.html)和 [LibreOffice 官方下载](https://www.libreoffice.org/download/download-libreoffice/)。

## 结果与限制

- JPG、BMP 和图片 PDF 使用白底；JPG、WebP 和音视频重编码可能有损。图片 PDF 使用 RGB 无损压缩，不额外进行 JPEG 有损编码。普通图片编码器创建新的图像帧，不保证保留 EXIF、ICC、注释等全部元数据。
- 尊重常见 EXIF 方向。多页 TIFF 转图片会逐页输出，转 TIFF/PDF 保留页序；动态图明确拒绝，不静默丢帧。
- 图片 PDF 每个输入文件生成一个 PDF，多页 TIFF 生成多页 PDF；不是 OCR，不提取或重建文字。PDF 转图片也不等于 PDF 转 Word。
- 单张图片或 PDF 页面最多 4000 万像素，图片输入不超过 512 MB，最多 500 页；多页 TIFF 输出总计最多 8000 万像素。
- 音视频保留一个主要视频轨和一个主要音轨，不保留字幕、附件及全部原始元数据。系统转换保持源视频尺寸和帧率；不同编码器、色彩空间和 HDR 的效果仍需按实际素材检查。
- Office 转换使用 LibreOffice 的格式实现，字体、复杂排版、公式和动画可能变化，不能承诺与 Microsoft Office 完全一致。旧二进制 DOC/XLS/PPT、受密码保护文件及 PDF 转可编辑 Office 文档不在本次范围。
- Office 使用本任务独立的配置和输入副本，禁用宏与更新检查，不接入已有用户窗口。带宏、需读取外部内容或外部数据公式的文档明确拒绝；普通网页超链接可以保留。
- 输入、输出和引擎路径必须是本机磁盘路径；不接受 URL、UNC 或映射网络盘。不是任意第三方程序的网络沙箱，应选择可信的本地引擎。

## 实现与检查

核心规划、文件提交、引擎参数和独立进程运行在 `src/DailyToolkit.Core/Conversion/`，Windows 编解码、状态与界面位于 `src/DailyToolkit.Desktop/Conversion/`。没有新增 NuGet 依赖，也不改现有依赖版本。

每个输入使用输出目录内的独立临时目录，完成并校验后提交，拒绝覆盖；同一输入的多页结果在发布前统一校验，失败回滚本次已提交结果。FFmpeg 结果在提交前完整解码验证。外部进程使用独立 Windows Job，取消只终止本次进程树，LibreOffice 使用独立用户配置；没有全局轮询或常驻转换进程。

界面复用主窗口标题、收藏、侧栏 `FeatureSwitch`、`Card`、`SectionTitle`、`MutedText`、`AccentButton` 和现有输入/选择/列表模板。只有局部进度条模板和继承原模板的文本框属性，没有另建主题或第二个总开关。页面按需创建，接入原有编辑状态保护和闲置释放。

Core 检查继续使用 `scripts/check-cloud.ps1`。设置 `DAILYTOOLKIT_TEST_FFMPEG` 为自有测试引擎路径时，额外执行真实无损音频往返；未配置时明确输出 SKIP。原有非 Windows 文件身份夹具继续明确跳过。

Windows 隔离验收入口：

```powershell
DailyToolkit.Desktop.Tests.exe --clipboard-native-conversion-checks <图片输出目录>
```

已接入 `scripts/check-quality.ps1 -IsolatedCi`，覆盖图片透明度、TIFF 多页、图片/PDF 往返、动态图拒绝、原件保护、停用取消、失败保存，以及深浅主题、宽窄页面和 100/150/200% DPI 渲染。渲染检查不代替实际屏幕与混合 DPI 验收。

Linux 可进行 Core 检查、配置引擎的实际转换与 Windows 交叉编译；这些结果不能表述为已完成 Windows 桌面、系统编解码、真实拖放、用户数据、安装或本机内存验收。每次实际执行结果以本次日志为准。
