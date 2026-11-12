# 格式转换内置组件

0.9.2 补丁将转换组件放入安装版和便携版。正式 0.9.1 的标签、附件和校验文件保持原样；本页描述源码和构建方案，不代表 0.9.2 已公开发布。

## 运行与携带

保留完整软件目录即可运行，不要求用户安装 FFmpeg、LibreOffice、.NET 或 Java，也不下载模型、插件和运行库。程序按自身目录寻找 `tools/ffmpeg/ffmpeg.exe` 和 `tools/libreoffice/program/soffice.com`，旧设置中的绝对路径不参与启动。

转换程序只在任务中启动。结束、取消或停用后，等待自有进程、文件和管道释放；关闭或收起主窗口继续已提交任务。主程序及其他已启用工具仍有正常运行成本。

沿用主窗口 `FeatureSwitch`、`Card`、`SectionTitle`、`MutedText`、`AccentButton` 和输入、选择、列表模板；本补丁没有新增样式。移除外部程序路径配置，将“保存设置”放回转换选项。

## 组件选择

| 组件 | 固定来源与许可 | 随包内容 |
| --- | --- | --- |
| FFmpeg 9.0.2 | 官方源码 `946fcce07b6dcd0331c8cc609192aeff5e1924f8`；本构建 GPL-2.0-or-later | 一个静态转换程序、当前格式所需编码器及常用解码器、完整源码包、构建脚本和许可 |
| LibreOffice 26.2.6.3 | 官方 Windows x64 包，SHA-256 `f9877032fd908beb9c0ddf06df4af5c2e85f419c42e14876c4cce5aae5fb2660`；MPL-2.0 与第三方许可 | 文字、表格、演示转换所需运行库、注册表数据、共享控件配置、初始配置和字体 |
| Noto Sans CJK SC 2.004 | 官方固定源码版本，字体 SHA-256 `2c76254f6fc379fddfce0a7e84fb5385bb135d3e399294f6eeb6680d0365b74b`；OFL-1.1 | 一份常规中文字体及原许可；由 LibreOffice 私有加载，不注册系统字体 |
| Microsoft Visual C++ CRT | 已许可构建环境的 `VC/Redist/MSVC/.../x64/Microsoft.VC*.CRT`；Microsoft 条款 | 依赖检查实际需要的、签名有效且未改动的 DLL，放在转换程序目录；不运行运行库安装器 |

FFmpeg 禁用网络协议、设备采集、硬件加速和未使用的输出功能，不构建播放器、探测工具或 AI 过滤器；Opus 的神经网络选项关闭。精简解码器仍保留常见电影音轨、Windows 录屏和无损输入，避免只按容器扩展名削减兼容性。

LibreOffice 不带帮助包、词典、扩展、图库、模板、Java/Python 运行时、独立办公入口、更新程序或系统集成。公共 DLL 的静态与延迟导入逐项检查，缺失依赖直接失败。共享控件配置及首次启动配置属于转换所需数据：缺少前者会使文档无法加载，缺少后者会使独立用户配置初始化失败。

许可证、版权和源码取得方式随各组件提供。FFmpeg 保留全部静态链接库的固定源码和构建配方；LibreOffice 保留官方许可、版权与对应源码说明；Microsoft DLL 的再分发遵循构建环境的 Visual Studio Distributable Code 条款。

## 构建与检查

`.github/workflows/conversion-bundle.yml` 准备两套固定组件。FFmpeg 在 Linux 交叉编译，输入摘要见 `scripts/conversion-sources.json`；LibreOffice 在可丢弃 Windows 环境中仅提取官方归档，再按依赖筛选。两者生成包含逐文件摘要和配方摘要的 `bundle.json`，不接纳额外文件。构建缓存按配方变化更新。

`scripts/publish.ps1` 验证组件及精确载荷后打包；安装版与便携版必须使用相同组件字节。`.local`、开发工具、报告、测试素材和实际用户数据不进入载荷。

Windows 检查保留原有完整质量门禁，并用实际便携目录和隔离安装目录运行 `scripts/test-conversion-bundle.ps1`：仅阻止测试自有程序出站联网，清除 PATH 中的外部程序目录，检查空路径和旧路径设置、WebP 透明度、现有音视频输出、文档往返及 PDF、私有中文字体嵌入、文件独占重开和进程退出。失败不能改用电脑上另装的引擎。

深浅主题、宽窄页面和 100/150/200% DPI 检查属于 WPF 渲染验收。Core 通过不能代表 Windows 桌面；隔离 Windows CI 也不代表 Windows 10/11 实机、混合 DPI、管理员拖放、真实安装、自启或用户电脑内存场景已验收。
