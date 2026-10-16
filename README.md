# DailyToolkit

Windows 桌面工具集：放大镜、浮笺、剪贴回溯、文件集装箱和电脑信息。

[下载最新版](https://github.com/YMXKNebula/DailyToolkit/releases/latest) · [安装与更新](docs/INSTALLATION.md) · [更新说明](docs/RELEASE_083.md)

## 下载

| 版本 | 用法 |
| --- | --- |
| 安装版 EXE | 运行安装，可直接升级已有安装版 |
| 便携版 ZIP | 解压后打开 `DailyToolkit.exe` |

包含运行环境，无需另外安装 .NET。目前提供 Windows x64 版本，已在 Windows 11 x64 上测试。

## 工具

| 工具 | 用途 | 默认快捷键 |
| --- | --- | --- |
| [放大镜](docs/SCREEN_LENS.md) | 局部放大，调整倍率、画质、尺寸和位置 | `Ctrl + Shift + F8` |
| [浮笺](docs/FLOATING_NOTES.md) | 桌面笔记，自动保存 | `Ctrl + Alt + N` |
| [剪贴回溯](docs/CLIPRECALL.md) | 查找、重新复制和管理剪贴历史 | `Ctrl + Alt + V` |
| [文件集装箱](docs/FILEPACK.md) | 收集不同磁盘的文件，打包为 ZIP、7z 或 TAR | `Ctrl + Alt + P` |

快捷键均可修改。工具可收藏、排序和单独开关；支持托盘运行、开机自启和自定义配色。

配色预设点击即保存；自定义颜色只改变预览，保存后应用。剪贴历史面板、文件集装箱快速窗口和详细打包窗口可分别配色。

剪贴回溯默认关闭，开启后持续记录剪贴板，仅在本机加密保存。复制敏感内容前请暂停记录或排除来源应用，无法自动识别所有密码。文件默认只记路径，开启缓存后可保存文件副本。

> 放大镜用于带反作弊的游戏时可能导致封号，请谨慎使用。

## 设置与数据

“电脑详情”可查看硬件、网络、电源和软件安装记录，支持复制摘要和导出报告。信息从 Windows 读取，不上传检测结果；网络流量读取系统统计，不进行测速。

在“设置 → 关于”检查更新。安装版自动升级；便携版下载 ZIP 后解压使用。

笔记、剪贴历史和设置位于 `%LOCALAPPDATA%\DailyToolkit\`，更新或卸载会保留。重要资料请另行备份。

## 反馈与源码

[提交问题或建议](https://github.com/YMXKNebula/DailyToolkit/issues)时，请附版本和复现步骤。

[源码构建与贡献](CONTRIBUTING.md) · [MIT 许可证](LICENSE) · [第三方声明](THIRD_PARTY_NOTICES.txt)
