# DailyUSE

把平时用得上的小工具放到一个软件里。

这个项目主要给自己日常使用。遇到想省点事的操作，就写成一个小工具，慢慢加进来。

工具的功能自己实现，通用库按需要使用，并遵守各自的许可证。

## 进度

现在有一个 Windows 桌面框架，主程序用 C# / .NET 10 和 WPF 编写。

窗口先打开，再在后台读取系统、处理器、内存、显卡、磁盘、显示和运行环境信息。根据内存、电量和窗口大小调整检测并发及界面。检测结果只在本机使用，不上传。

首页把日期、天气、网络、电源和电脑信息放在一起。配置、运行环境和软件安装记录可以展开查看，也能搜索软件名称、复制配置摘要和保存 JSON 报告。其他日常工具会慢慢加进来，见 [开发计划](docs/PROJECT_PLAN.md)。

左侧“游戏”页提供局部放大：一个浮窗放大屏幕中央或鼠标附近的区域，框外保持原样。框内通过显卡做高质量插值和锐化，可调倍率、大小和帧率上限。快捷键可以自行设置或清空，具体用法见 [局部放大](docs/SCREEN_LENS.md)。这个分区属于 DailyUSE，与其他游戏开发项目独立。

日期用 Windows 本机时间，网络读取 Windows 的连接状态和网卡计数器，不发起测速或连通性请求。天气优先读取 Windows 小组件保存在本机的天气缓存，显示温度、天气、地点和更新时间；没有可用缓存时再尝试读取任务栏天气文字。缓存由 Windows 更新，DailyUSE 不请求天气服务。

## 运行和构建

已打包的版本解压后打开 `DailyUSE.exe`，不需要另外安装 .NET。当前在 Windows 11 x64 上验证。

从源码构建需要 Windows 和 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)：

```powershell
.\scripts\build.ps1
.\src\DailyUSE.Desktop\bin\Release\net10.0-windows10.0.26100.0\DailyUSE.exe
```

`build.ps1` 会编译并运行检查。生成便携包：

```powershell
.\scripts\publish.ps1
```

输出位于 `artifacts/DailyUSE-win-x64/` 和同目录的 ZIP。首次打包需要下载官方 .NET 运行时包，软件本身离线运行。GitHub Actions 也会生成便携包，见仓库的 Actions 页面。

## 增加工具

C# 工具可以直接调用公共代码。Python、Node.js 或原生可执行程序可以通过本地进程和 JSON 交换数据，不需要启动网页或服务器。接口和目录见 [框架说明](docs/FRAMEWORK.md)。

软件安装列表来自 Windows 安装记录，可能漏掉便携软件和部分商店应用。检测失败的项目会标为不可用，不阻止打开界面。保存的报告包含硬件配置、软件名称和页面读取到的本机状态，分享前请自行查看。

## 想法和反馈

有想要的小工具，可以开个 Issue 说说具体用途。想参与开发的话，先看一下 [贡献说明](CONTRIBUTING.md)。

## 许可证

[MIT](LICENSE)
