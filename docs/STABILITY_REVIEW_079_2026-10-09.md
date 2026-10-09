# DailyToolkit 0.7.9 审查记录

日期：2026-10-09。审查开始时，本地与 GitHub main 均为 `dc6b634cb815ede890ec508a5915f099d5273e38`。下文记录检查和修复情况；修复随同一天的 [0.7.9 重打包](RELEASE_079.md)提供。

## A. 问题清单

### 确认的问题

| 文件位置 | 问题与复现条件 | 程度 | 确认结果与影响 |
| --- | --- | --- | --- |
| [ScreenLensSession.cs](../src/DailyToolkit.Desktop/Gaming/ScreenLensSession.cs)，BeginCapture / StartCaptureAsync | 启动 Task 未完成时先处理首帧，再完成 Task，原代码重新启动五秒超时，超时处理也不检查首帧是否已到达。 | P1，属于 P0 排查 | 确定性测试调用实际生产回调，修复前失败，修复后通过。这一顺序下正常会话可能被关闭；没有证明真实 WGC 的发生频率。 |
| [LensGpuRenderer.cs](../src/DailyToolkit.Desktop/Gaming/LensGpuRenderer.cs)，Dispose；ScreenLensSession.ApplyMovement | 连续两次 Dispose，第二次访问已释放 Context。跨屏尺寸变化后，新渲染器构造失败时，Stop 也可能再次清理旧对象。 | P1，属于 P0 排查 | WARP 渲染器重复释放实测抛 NullReferenceException；异常清理路径经代码核对。没有诱发真实显卡故障。 |
| [LensShortcutDiagnostics.cs](../src/DailyToolkit.Desktop/Gaming/LensShortcutDiagnostics.cs)，CheckWheelZoomAsync / CheckFixedAndResetAsync | 检查仍使用 10× 上限，滚轮次数不足；上限及固定模式检查都在 Dispatcher 处理前断言。当前上限为 16×，每步 0.25×。 | P2 | 实际运行分别触发旧上限与固定模式断言；不能据此认定产品滚轮失效。修正后完整专项通过。 |

### 尚未确认的风险和待做功能

| 位置 | 条件与影响 | 程度 / 状态 | 处理 |
| --- | --- | --- | --- |
| LensCapture / LensGpuRenderer / ScreenLensSession | 原生释放若抛异常，后续清理可能没有执行；设备丢失后是否安全释放。 | P1，未复现 | 先做设备故障测试，不在无证据时改写释放链。 |
| 同上 | 数小时运行是否持续增加 COM、显存或句柄。 | P1，未验证 | 短时测试最终比预热多 9 handles、2 USER 对象，归属未查明。补长时测试，不能宣称无泄漏。 |
| LensCapture.OnFrame / GamingViewModel | 捕获尺寸变化会停止并提示重新开启；没有自动重建或设备恢复。 | P1，现有行为与功能缺口 | 保留手动重开规则；睡眠、拔屏和 TDR 仍需实测。 |
| [NotesController.cs](../src/DailyToolkit.Desktop/Notes/NotesController.cs)，RunAsync | 未被现有 catch 处理的意外异常，可能通过 previous Task 影响后续队列。 | P2，未复现 | 正常快速操作与存储失败通过；不改成吞掉所有异常。 |
| [NotesMousePassThrough.cs](../src/DailyToolkit.Desktop/Notes/NotesMousePassThrough.cs) / LensPointerController | 部分卸载钩子、窗口样式 API 没有检查失败返回值。 | P2，未复现 API 失败 | 正常隐藏和退出钩子清零已测，权限矩阵仍需补测。 |
| [NotesStore.cs](../src/DailyToolkit.Desktop/Notes/NotesStore.cs) / NotesViewModel | 强杀或断电可能丢失尚未保存的输入；正文与设置不是跨文件事务。 | P1，故障验证未完成 | 锁文件和损坏数据已测；真实磁盘满、ACL 拒绝、强杀与断电恢复未测。 |
| LensShortcutDiagnostics.CheckNativeDragAsync | 早期一次相对 SendInput 后没有观察到预期位移。 | P2，历史失败原因未确认 | 静置输入后完整专项通过，产品鼠标逻辑未改；保留早期失败记录。 |
| AI 原型、帧率选项、安装发布 | AI 未接入，帧率没有动态管理，只有便携包。 | 已确认功能缺口 | 本次仅分析，不加入正式运行路径。 |

没有确认 P0 缺陷。“P0 排查”是检查顺序，不是对问题严重程度的预先判断。

## B. 修复

首帧增加按捕获 generation 保存的就绪标记。启动完成和超时回调都检查该标记，旧回调不能影响新捕获。不能以 IsVisible 判断新捕获就绪，因为跨屏时旧画面仍可见。原首帧和超时处理提取成私有方法，测试可以控制启动 Task 完成顺序；没有引入 Factory 或 DI。

渲染器 Dispose 增加幂等检查，重复释放直接返回，原资源释放顺序不变。此保护适用于现有串行清理方式，不代表多线程并发 Dispose 已受支持。

滚轮诊断根据当前上限、下限和步长计算次数；上限与固定模式检查都等待延迟处理结束。诊断 JSON 的 TenTimes 标志改为 MaximumZoom；用户配置格式没有变化。

新增回归测试：

- [LensStartupTests.cs](../tests/DailyToolkit.Desktop.Tests/LensStartupTests.cs)：首帧早到、正常顺序、排队超时、无首帧、停止/重开、旧启动成功或失败、跨屏旧画面可见及新捕获就绪。
- [LensResourceStabilityTests.cs](../tests/DailyToolkit.Desktop.Tests/LensResourceStabilityTests.cs)：重复释放，以及显式运行的真实 WGC 启停、质量纹理和资源计数检查。
- [NotesStabilityTests.cs](../tests/DailyToolkit.Desktop.Tests/NotesStabilityTests.cs)：保存过程中再编辑、真实 File.Replace 失败、隐藏/退出取消、恢复编辑、主文件与备份保留、快速隐藏/呼出和钩子清理。

跨进程鼠标测试增加错误信息、双击消息计数、唯一的子窗口就绪文件名及重复运行入口。没有改产品单双击规则或浮笺运行代码。

### 保留的设计

捕获回调和刷新工作项用同一 gate 保护渲染器。Dispose 在锁内设置 stopped，锁外解绑并关闭捕获，迟到工作项检查 stopped 后返回。跨屏先使旧 generation 失效，尺寸相同复用渲染器，尺寸改变才重建。移动与滚轮仍使用各自的 generation。

质量模式按需分配 RGBA16F，中间 Texture / RTV / SRV 在离开质量、Resize 和 Dispose 时释放，Shader 在渲染器寿命内缓存。测试逐次检查 HWND 销毁、窗口表清空、设备包装指针清零、质量纹理为空及钩子句柄为零；这不是全部 COM 活对象审计。

浮笺的 400ms 防抖、保存锁和 revision 循环保留。隐藏/退出刷盘期间暂时禁止输入；失败后恢复编辑并阻止退出。严格 UTF-8 / JSON 读取失败时禁止正常保存覆盖主文件，并保留备份。写入使用唯一临时文件、WriteThrough、Flush 和 File.Replace。用户明确执行“恢复全部默认设置”是另一路径，旧设置可能写入 .bak；正常损坏保护不能替代这一操作的恢复说明。

焦点恢复只尝试一次合法的非自身目标，系统拒绝或目标关闭时提示并释放输入焦点。失焦穿透和仅勾选后接收左键的规则不变。已接住的左键按下需要配对处理弹起；这段短暂钩子保留是既有规则。真实 125% / 150% 定位检查通过，物理拔屏仍未验证。

MainViewModel 已按 Appearance / Startup / Notes 分开；工具有独立 ViewModel、Control 和 Controller。新工具可沿浮笺方式接入导航、收藏、初始化和 Dispose。没有发现必须靠机械拆分大文件解决的问题。主窗口关闭、预览卸载及异步结果的解除订阅、停止计时器和 generation 检查保留，不引入事件总线或通用服务层。

## C. 验证

环境：Windows 10.0.26200.0、.NET 10、RTX 5070 Ti Laptop GPU；两屏为 2560×1440 和 2560×1600，缩放 125% / 150%。存储检查使用临时笔记，不修改用户数据。

| 检查 | 结果与限制 |
| --- | --- |
| Release 构建 | 0 警告、0 错误。 |
| Core / Desktop | 117/117、41/41 通过。Desktop 从 38 组增加到 41 组；组数不是覆盖率。 |
| 时序、释放红/绿回归 | 修复前分别复现错误启动计时器与重复释放异常，修复后通过。 |
| 保存和原文件保护 | 保存中再编辑、锁文件替换失败、失败阻止隐藏/退出、损坏主文件及两个 .bak 保留通过。 |
| 真实捕获与接力 | 捕获刷新、4× / 16× 双屏接力、排队跨屏、固定/移动、比例与尺寸、滚轮反向和 HUD 检查通过。 |
| 浮笺两屏与穿透 | 125% / 150% 定位、跨进程输入、仅左键聚焦通过；重复穿透 12 轮通过。360×420 DIP 分别对应 450×525 和 540×630 像素，锚点误差 0 / -0.2 像素。 |
| 短时资源 | 44 次可见启动与 24 次立即停止、88 次质量切换，资源归属断言通过。首次就绪后等待 5.2 秒仍可见，总约 23.66 秒。 |
| 当前性能 | 四模式 × 四个手动帧率档 × 六倍率，共 96 组采样期间可见；不等于数小时稳定。 |
| 完整快捷键诊断 | 最终通过：真实鼠标拖动、16× 滚轮、固定/移动、双屏接力、启停、按住/切换及模拟捕获失败清理。早期失败日志保留。 |

此前部分原生输入检查受到额外点击或 Steam 前台切换影响，静置鼠标后全量通过。没有查清所有早期失败的原因，也没有降低断言来换取通过。原始日志保留在本机。

未执行：数小时持续运行、实际设备丢失、睡眠/唤醒、物理热插拔、HDR、跨驱动和不同 Windows 构建、管理员与普通权限的完整组合、真实磁盘满/ACL 拒绝/强杀/断电，以及游戏满载的端到端延迟和 p95/p99 帧时间。

可运行 `scripts/build.ps1` 做默认检查。新增 Desktop Tests 参数为 `--startup-timing-checks`、`--renderer-dispose-checks`、`--stability-checks`、`--resource-stability <报告路径>`、`--notes-cross-process-repeat`。原生输入检查需要可交互桌面和不受其他输入干扰的时段。

## D. 性能与兼容性

WGC → D3D11 → renderer → swapchain 不变。没有增加逐帧 CPU 读回、图像复制、绘制 pass、生产常驻线程或轮询。正常捕获原有两处 GPU CopyResource，质量两次 Draw，其他一次 Draw。新增检查只在启动、首帧、超时与释放时执行。

没有修改前后同条件 CPU/GPU 对照，不能量化性能差值或声称零损失。每个会话多一个 int、每个渲染器多一个 bool，对象布局实际字节数未测。算法、固定清晰度、1–16× 和 0.25× 精度、默认设置及配置格式不变。shader SHA-256 仍为 `E61080F046C7981B0B5870042E201F3148C73D8B9179FCFE0D9235EE5D20919E`，图像检查通过。

当前性能采样使用自建全屏动画、640×384 框及 1/2/4/8/12/16×，每组约 400ms，有效采样合计 38.81 秒。60 FPS 档观察到约 59.20–62.78 输出 FPS，全部档位的源计数约 123.33–166.85 FPS。120/144/240 没有稳定达到对应输出。CPU/GPU/显存包括测试画面和整个进程；GPU 是最大的进程引擎百分比，不是整卡总占用。这些短窗数据不能比较模式总体快慢，也不是输入到显示延迟。

短时资源数据：

| 时点 | handles | USER / GDI | 托管 bytes | private bytes | 本地显存 bytes |
| --- | --- | --- | --- | --- | --- |
| 预热后 | 1183 | 39 / 25 | 2018208 | 434774016 | 87609344 |
| 36 次可见轮次后 | 1186 | 40 / 25 | 1979808 | 463577088 | 87609344 |
| 快速启停后 300ms | 1236 | 41 / 25 | 1983536 | 476659712 | 87609344 |
| 后续等待 2 秒 | 1192 | 41 / 25 | 1978896 | 294072320 | 87609344 |
| 再等待 5 秒 | 1192 | 41 / 25 | 1978992 | 220147712 | 87609344 |

显存短时未持续增长，部分句柄增量随后回落，最后仍比预热多 9 handles / 2 USER。归属不明，不能擅自说是缓存或泄漏。RGBA16F 的理论像素负载为宽×高×8 bytes，640×384 为 1.875 MiB，1600×1200 约 14.648 MiB，不是总进程显存。

### AI 超分

[独立原型](../tools/DailyToolkit.LensAiPrototype/README.md)使用 ORT DirectML 1.24.4 和 FSRCNN-small 2×，1622 参数，作者权重使用 Apache-2.0。未加入主解决方案或正式包。本次核对源码与 2026-10-08 历史报告，没有重新跑推理。

| 适配器 | 输入 | 平均 / p95 | 进程显存采样 |
| --- | --- | --- | --- |
| RTX 5070 Ti Laptop | 320×192 | 2.747 / 3.495 ms | 约 94 MiB |
| RTX 5070 Ti Laptop | 640×384 | 5.629 / 6.192 ms | 约 166 MiB |
| AMD 610M | 320×192 | 7.350 / 8.156 ms | 约 32 MiB |
| AMD 610M | 640×384 | 23.831 / 25.035 ms | 约 107 MiB |

报告记录 DirectML provider 执行及 CPU 对照误差 2.3841858e-7。Intel 未测。推理含 CPU 张量上传/读回，不是实际捕获 FPS，显存不是模型最低需求。4–16× 是 2× 推理后再空间放大，首次初始化比稳态更慢。

正式接入还需 D3D11/D3D12 资源同步、适配器匹配、持久设备张量、尺寸预热、最新帧异步处理、generation 取消、超时/设备故障回退、显存预算和文字/边缘/HDR 质量测试。现有 AIEnhanced 归一化到质量模式只是选项回退，推理运行时回退尚未实现。[DirectML 文档](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html)的 sequential / memory pattern 限制已遵守；[I/O Binding](https://onnxruntime.ai/docs/performance/tune-performance/iobinding.html)是后续设备侧输入输出方向，不代表当前零拷贝已经完成。

### 自动帧率建议

FrameRate=0 对每个到达帧允许绘制；固定档位按时间戳跳帧。帧池有界取最新帧，HUD/参数缓存重绘不受同一 pacer 限制。MinUpdateInterval=0 是不设上限，不是动态管理。

后续可按所选显示器刷新率、源帧间隔和异步 GPU 耗时确定目标，最多保留一个在途工作与一个最新帧，丢弃旧帧，输入优先重绘缓存。连续过载时降频，持续稳定后缓慢恢复并设迟滞；静止画面不轮询。GPU query 复用并低频读取已完成结果，不同步等待或读回图像。[MinUpdateInterval](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.minupdateinterval?view=winrt-26100)仅在可用时辅助限制。

只调节节奏，不降低模式画质或清晰度。验收需一起看帧年龄、丢弃计数、p50/p95/p99 帧间隔、输入延迟、GPU 和游戏帧时间；覆盖 VRR、静止画面、240Hz 源/60Hz 输出及质量满载。本次不取消手动档位。

### 安装和升级建议

目前是自包含便携 ZIP，没有安装器或自动更新。数据位于 `%LOCALAPPDATA%/DailyToolkit`，不在版本目录。每用户 SID 的互斥避免普通发行实例同时写数据；管理员重启的新实例先等待旧实例释放互斥，最多 15 秒，之后才初始化，不能仅凭进程启动顺序认定笔记必然读旧内容。

安装版可用稳定的每用户程序路径，普通运行不提升权限；管理员自启按 Windows 要求单独授权。便携版与安装版默认共用现有每用户数据和互斥，独立 profile 若以后提供，应由用户选择。

升级应先正常退出并确认刷盘，再校验新包、暂存替换、保留回滚，最后迁移属于本程序的启动项/任务路径并验证。保留 SID、SDDL、RunLevel、触发器、静默参数和电源设置，失败保留旧可启动路径。卸载默认保留正文、设置和备份，只清理仍指向该安装的自启；删除数据另作明确选择。

发布 EXE 当前未签名。后续为 EXE 和安装包签名并加时间戳，不能保证消除所有系统或杀毒提示。本次不引入安装框架、不修改防护。

## E. 1.0 前的事项

| 优先级 | 项目 |
| --- | --- |
| 必须 | 将两个运行修复纳入正式包，并验证候选包与源码对应、补丁 CI 和启停/接力检查。 |
| 必须 | 数小时运行与资源归属分析，不能用 23.66 秒代替。 |
| 必须 | 真实睡眠/恢复、拔屏、设备故障和权限组合，确认安全停止、清理及重开；若复现再修。 |
| 必须 | 独立数据上的磁盘满、ACL 拒绝、异常退出及备份恢复验收，说明未保存输入和显式重置的限制。 |
| 建议 | 保持独立输入环境的诊断检查；补不同 GPU/Windows/DPI/HDR 与满载帧时间。 |
| 建议 | 自动帧率另作功能任务；若 1.0 承诺自动管理，其完成成为发布条件。 |
| 建议 | 安装、签名、升级/回滚/卸载方案及 GitHub 介绍、截图、使用说明。只提供便携版时明确说明。 |
| 可延后到 1.x | AI 超分、自动设备恢复、多 profile 和更完整的历史备份。 |
| 不需要改 | 已正确的笔记存储/焦点架构、虚拟轴接力、固定/移动、画质算法、默认值和配置格式；不为缩小文件而拆分或加抽象。 |
