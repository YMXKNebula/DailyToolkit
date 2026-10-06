# DailyToolkit 定向重构验收

基线是本地已完成并验证的 0.3.7（10×、滚轮开关、实时锐化和着色器预热）。这些改进早于本次重构；本次未回退到远端原先的 0.3.6。

## 实际改动

| 文件 | 改动及目的 |
| --- | --- |
| `src/DailyToolkit.Desktop/Presentation/GamingViewModel.cs` | 保留绑定、命令、配置、收藏、显示器选择；把原生运行时交给具体会话对象。保留既有 internal 诊断入口。 |
| `src/DailyToolkit.Desktop/Gaming/ScreenLensSession.cs` | 新增运行时资源所有者；集中启停、首帧、失败、超时、拖动、辅助线、布局和释放。 |
| `src/DailyToolkit.Desktop/Gaming/LensShortcutDiagnostics.cs` | 按注册、首次后台开启、真实拖动、滚轮、预览、反复启停、固定与重置、启动取消、按住、失败清理拆方法；临时诊断会话负责测试窗口和输入清理。 |
| `src/DailyToolkit.Desktop/Gaming/LensCapture.cs` | 只在终止性捕获异常或参数更新异常时记录原始异常；成功帧处理不变。 |
| `src/DailyToolkit.Desktop/Gaming/LensGpuRenderer.cs` | 保留渲染代码；预热失败增加原始异常记录，嵌入资源名称随项目改名。 |
| `src/DailyToolkit.Desktop/Controls/LensPreview.xaml.cs` | 保留照片渲染与交互，预览失败增加原始异常记录。 |
| `src/DailyToolkit.Desktop/Gaming/LensRenderBenchmark.cs` | 开发检查增加像素哈希；计时仍排除图片读回。 |
| `src/DailyToolkit.Desktop/Gaming/GamingPreferencesStore.cs` | 新配置目录及旧目录兼容读取；保存失败保留原始异常，JSON 格式不变。 |
| `src/DailyToolkit.Desktop/Presentation/FavoritesStore.cs` | 收藏目录使用相同兼容规则，保留旧文件及其他工具的收藏。 |
| `src/DailyToolkit.Desktop/Presentation/MainViewModel.cs` | 只更新名称与失败异常记录，不拆页面状态。 |
| `src/DailyToolkit.Desktop/MainWindow.xaml` | 名称、仓库链接更新，标题字体适配较长名称；工具布局和交互不变。 |
| `src/DailyToolkit.Desktop/MainWindow.xaml.cs` | 名称与失败异常记录，不搬动快捷键编辑及窗口焦点处理。 |
| `tests/DailyToolkit.Tests/Program.cs` | 只保留注册、执行、断言帮助方法和汇总；子进程入口继续保留。 |
| `tests/DailyToolkit.Tests/LensLayoutTests.cs` | 原有尺寸、坐标、吸附、快捷键有效性和 pacing 检查。 |
| `tests/DailyToolkit.Tests/LocalStatusTests.cs` | 原有天气解析、缓存及流量采样检查。 |
| `tests/DailyToolkit.Tests/AdaptationPolicyTests.cs` | 原有硬件适配及运行条件检查。 |
| `tests/DailyToolkit.Tests/ToolCatalogTests.cs` | 原有工具登记和报告隐私检查。 |
| `tests/DailyToolkit.Tests/ProcessToolRunnerTests.cs` | 原有 UTF-8、错误输出、超时、取消、大小限制、协议检查及 worker 实现。 |
| `tests/DailyToolkit.Desktop.Tests/Program.cs` | 保留 STA/WPF 执行入口、测试顺序和汇总。 |
| `tests/DailyToolkit.Desktop.Tests/GamingViewModelTests.cs` | 原有指针、收藏和偏好检查；指针检查使用独立配置，避免依赖用户的滚轮开关。 |
| `tests/DailyToolkit.Desktop.Tests/LensShortcutTests.cs` | 原有后台注册、切换、按住、空选和释放检查。 |
| `tests/DailyToolkit.Desktop.Tests/PreviewTests.cs` | 原有实际照片渲染、详细设置绑定和窄窗口适配检查。 |
| `tests/DailyToolkit.Desktop.Tests/LifecycleTests.cs` | 原有后台检测、导航、日期、网络天气及关闭取消检查与测试替身。 |
| `tests/DailyToolkit.Desktop.Tests/WindowsWeatherCacheTests.cs` | 原有 Windows 缓存读取、共享、损坏、大小和取消检查。 |
| `tests/DailyToolkit.Desktop.Tests/SettingsCompatibilityTests.cs` | 新增改名后的旧配置读取、新目录保存、旧文件保持及新配置优先检查。 |

项目更名还机械更新了解决方案、四个工程名称、命名空间、XAML 类名、嵌入资源名、脚本、工作流、文档及公开仓库地址。Core 业务算法和现有低层组件不因更名重写。荷花素材仅更换所在工程路径，图片内容不变。

## 明确保留

- `MainViewModel` 暂不拆；继续保留主页、导航和本机状态，避免父子模型转发和同步。
- 放大设置暂留 `MainWindow`，保留全局快捷键暂停、焦点和详细设置控件的现有关系；后续新增工具使用独立控件和 ViewModel。
- 自定义测试执行器不换框架，断言和检查语义保留。
- 开发诊断保留在桌面工程，用现有命令显式进入；另建工程会增加 CLI 与 Windows 图形程序集之间的依赖。
- 现有直接、命令式 GPU 实现保留，不增加 Factory、接口、DI 或帧消息层。

## 行为变化

**工具行为变化：无。** 固定锁住当前位置，恢复默认回中，真实浮窗与照片位置独立；同键切换、按住松键、后台呼出、空快捷键、滚轮开关和配置格式保持原样。

用户要求的更名另行生效：显示名称、程序、文件夹和仓库为 DailyToolkit；保存目录改为新名称，并兼容旧设置。较长的侧栏标题缩小字体以完整显示。内部异常仅通过 Trace 记录，不新增日志文件或逐帧输出。

## 性能与画质路径

`Windows Graphics Capture → LensCapture → LensGpuRenderer → Lens.hlsl → LensNativeWindow` **没有变化**。`ScreenLensSession` 只拥有资源和生命周期，不中转图像帧。

首帧和失败保留原有一次 Dispatcher 回调，代次检查继续阻止已关闭会话的旧回调重新打开浮窗；没有增加 Task、await、定时器或 CPU copy。唯一启动超时定时器仍为原有 5 秒，不能驱动画面。

拖动及滚轮同步调用，未新增 Dispatcher 排队。倍率、位置、锐化只更新已有窗口和捕获参数。Follow / 30 / 60 / 120 / 144 / 240 pacing 不变。

重构没有改变 Shader、Sampling、Sharpening、source/output rectangle、Texture format 或 Texture path。`Lens.hlsl` 与重构基线内容相同；几何、pacing、指针及照片渲染实现除项目名称引用外相同。GPU renderer 核心绘制及捕获成功帧路径相同。

三组 GPU 输出对照（640×384 的 2×、10×，1600×1200 的 10×）像素哈希全部相同。基线 / 重构后单次 GPU 着色器耗时约为 0.00569 / 0.00543、0.00539 / 0.00539、0.04169 / 0.04304 ms；这属于微秒级批次测量波动，不包含 Windows 捕获和呈现，不据此声称具体游戏帧率完全一致。

## 验证

- Build：成功，0 警告、0 错误。
- Core tests：30/30；原有检查保留。
- Desktop tests：17/17；原有 16 项加改名配置兼容检查。
- Lens diagnostics：成功；真实捕获、首帧、排除浮窗、鼠标穿透、shader 和关闭清理通过。
- Shortcut diagnostics：43/43；保留原 40 项，增加运行中参数复用资源、模拟失败清理及当前显示器选择和活动会话释放。
- `--preview` 与 `--diagnose`：成功，输出仅保存在本地。
- 三组渲染像素哈希对照：全部一致。

模拟失败验证失败信号至 UI 清理的实际路径，没有模拟物理断线或驱动崩溃；显示器验证覆盖本机当前枚举出的显示器，不替代其他多屏组合和各类游戏的运行验证。反复启停检查确认原生窗口、捕获、渲染器、监听和定时器所有权释放，未做长时间内存压力测试。

本地证据：`artifacts/refactor-shortcut-check.json`、`artifacts/refactor-capture-check.json`、`artifacts/refactor-render-check.json`。电脑报告、测试区域截图和临时基线不提交公开仓库。

## 0.3.8 复查修复

复查发现三处遗漏，按原有实现修复：

- 参数刷新任务结束后只重新安排重绘，不再写回先前读取的参数，避免覆盖用户刚调整的倍率、采样位置或锐化。
- 照片框在屏幕边缘扩大后，按限制后的实际框中心取样。下一次拖动也从显示出来的中心计算偏移，避免拖动起点仍停在扩大前的位置。预览和实际浮窗的位置继续独立。
- 硬件检测失败时通过 Trace 保留原始异常，页面继续显示可用的部分报告；没有新增日志文件。

成功捕获帧、着色器、插值、锐化和限帧代码未改动。构建为 0 警告、0 错误；Core 30/30、Desktop 18/18、真实桌面快捷键与交互 45/45 通过。桌面检查增加检测异常记录验证，照片检查覆盖边缘扩大后的真实着色器像素；交互检查增加连续参数调节和边缘扩大后的实际鼠标拖动。

便携程序的真实捕获、浮窗排除和鼠标穿透通过，三组生成纹理的 GPU 像素输出与 0.3.7 相同。GPU 耗时检查仍不包含 Windows 捕获和呈现，不能据此保证具体游戏的帧率。

本地证据：`artifacts/fix-shortcut-check.json`、`artifacts/fix-capture-check.json`、`artifacts/fix-render-check.json`；这些诊断输出不提交公开仓库。

## 0.3.9 交互调整

移除屏幕选择和锐化调节，开启时自动识别鼠标所在屏幕；画面继续使用原有默认处理强度。通过 Windows 无边框捕获许可关闭屏幕四周的黄色提示，渲染框边线保留。

可移动模式支持按住左键跨屏往返，切换对应的捕获来源和中心辅助线，保留浮窗和鼠标拖动。输出尺寸相同时复用渲染器，进入较小屏幕时限制尺寸，返回大屏时恢复；资源切换从鼠标钩子移到界面线程，合并待处理位置。每个捕获实例各自发送首帧信号，防止复用渲染器后漏掉新来源的首帧。停止和重新开启仍有代次检查。

本次改变了屏幕识别和跨屏行为，实时帧仍直接进入 GPU 渲染器。构建为 0 警告、0 错误，Core 31/31、Desktop 18/18、桌面交互 47/47 通过。本机两块屏幕实际验证了自动选择、10× 大尺寸框跨屏往返、对应画面来源及尺寸恢复；无边框捕获返回 `Allowed`，会话的 `IsBorderRequired` 为 `false`。

本地证据：`artifacts/screens-shortcut-check.json`、`artifacts/screens-capture-check.json`、`artifacts/DailyToolkit-gaming.png`。单屏运行时跨屏检查记为未执行，不计为通过。未覆盖所有显卡、屏幕组合或系统权限策略。

## 0.3.10 跨屏拖动修复

捕获来源切换曾使已经排队的移动请求失效，但仍保留“正在排队”标记，后续鼠标移动只能覆盖待处理位置，无法再执行。拖动请求现在使用独立的有效期：切换捕获来源时保留，停止工具时作废，不再与首帧和失败回调共用代次。

新增检查在 0.3.9 中复现了切换中丢失移动请求的问题。修复后的便携包通过 Core 31/31、Desktop 18/18、实际桌面交互 49/49；双屏检查包含切换中的排队输入、连续小步的相对鼠标移动、在另一屏幕继续拖动和返回原屏幕。显卡渲染、着色器和限帧代码未改动。

本地证据：`artifacts/drag-queue-before.json.error.txt`、`artifacts/drag-queue-fixed.json`；未提交公开仓库。

## 其他更名文件（仅名称和引用）

- `DailyToolkit.slnx`
- `src/DailyToolkit.Core/DailyToolkit.Core.csproj`
- `src/DailyToolkit.Core/Environment/AdaptationPolicy.cs`
- `src/DailyToolkit.Core/Environment/IEnvironmentProbe.cs`
- `src/DailyToolkit.Core/Environment/LocalStatus.cs`
- `src/DailyToolkit.Core/Environment/MachineReport.cs`
- `src/DailyToolkit.Core/Environment/WindowsWeatherCache.cs`
- `src/DailyToolkit.Core/Gaming/KeyboardShortcut.cs`
- `src/DailyToolkit.Core/Gaming/LensFramePacer.cs`
- `src/DailyToolkit.Core/Gaming/LensLayout.cs`
- `src/DailyToolkit.Core/Gaming/LensPlacement.cs`
- `src/DailyToolkit.Core/Tools/ProcessToolRunner.cs`
- `src/DailyToolkit.Core/Tools/ToolCatalog.cs`
- `src/DailyToolkit.Core/Tools/ToolRequirements.cs`
- `src/DailyToolkit.Desktop/App.xaml`
- `src/DailyToolkit.Desktop/App.xaml.cs`
- `src/DailyToolkit.Desktop/Assets/README.md`
- `src/DailyToolkit.Desktop/Assets/lens-preview.jpg`
- `src/DailyToolkit.Desktop/Controls/LensPreview.xaml`
- `src/DailyToolkit.Desktop/DailyToolkit.Desktop.csproj`
- `src/DailyToolkit.Desktop/Environment/HardwareProbe.ps1`
- `src/DailyToolkit.Desktop/Environment/InstalledSoftwareReader.cs`
- `src/DailyToolkit.Desktop/Environment/NativeWindowsInfo.cs`
- `src/DailyToolkit.Desktop/Environment/RuntimeDetector.cs`
- `src/DailyToolkit.Desktop/Environment/WindowsEnvironmentProbe.cs`
- `src/DailyToolkit.Desktop/Environment/WindowsLocalStatusProbe.cs`
- `src/DailyToolkit.Desktop/Environment/WindowsTaskbarWeatherReader.cs`
- `src/DailyToolkit.Desktop/Environment/WindowsWeatherCacheReader.cs`
- `src/DailyToolkit.Desktop/Gaming/CaptureInterop.cs`
- `src/DailyToolkit.Desktop/Gaming/Lens.hlsl`
- `src/DailyToolkit.Desktop/Gaming/LensDesktopGuides.cs`
- `src/DailyToolkit.Desktop/Gaming/LensDesktopSnapshot.cs`
- `src/DailyToolkit.Desktop/Gaming/LensDiagnostics.cs`
- `src/DailyToolkit.Desktop/Gaming/LensNativeWindow.cs`
- `src/DailyToolkit.Desktop/Gaming/LensPhotoPreviewRenderer.cs`
- `src/DailyToolkit.Desktop/Gaming/LensPointerController.cs`
- `src/DailyToolkit.Desktop/Gaming/LensShortcutController.cs`
- `src/DailyToolkit.Desktop/GlobalUsings.cs`
- `src/DailyToolkit.Desktop/Presentation/ObservableObject.cs`
- `src/DailyToolkit.Desktop/Presentation/RelayCommand.cs`
- `src/DailyToolkit.Desktop/Theme.xaml`
- `src/DailyToolkit.Desktop/app.manifest`
- `tests/DailyToolkit.Desktop.Tests/DailyToolkit.Desktop.Tests.csproj`
- `tests/DailyToolkit.Tests/DailyToolkit.Tests.csproj`

名称及打包引用另在以下文件更新：

- `Directory.Build.props`
- `.github/workflows/build.yml`
- `scripts/build.ps1`
- `scripts/publish.ps1`
- `README.md`
- `THIRD_PARTY_NOTICES.txt`
- `docs/FRAMEWORK.md`
- `docs/PORTABLE_README.txt`
- `docs/SCREEN_LENS.md`
