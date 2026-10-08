# DailyToolkit 0.7.0

2026-10-08。0.7.0 更新了屏幕局部放大、工具开关和侧栏界面。

## 变化

- 工具开关改为红绿胶囊，白色圆钮左右滑动；支持点击、拖动、键盘和无障碍切换。
- 收藏夹侧栏使用主题的侧栏底色，避免整栏与选中项的强调色混在一起。
- 选项切换时，圆形波浪从点击位置扩散，只覆盖选中选项及对应配置区，颜色跟随主题。
- 程序、EXE、托盘及关于页共用相连的纯 DT 字母与 RGB 背景图标。
- 屏幕局部放大支持 1.00–16.00×，每步 0.25×；滚轮调节后短暂显示当前倍率，保留精密滚轮的余量与反向输入顺序。
- 提供性能、清晰（推荐）、质量、像素四种画质模式。清晰与质量固定使用 100% 清晰度，删除调节滑块；旧版的可调数值不再生效，画质模式和倍率继续保存。
- 配置中的快捷键标签改为“开启/关闭放大”；关于页和诊断版号从程序集读取。
- 便携包输出为 `artifacts/DailyToolkit-<版本>-<架构>/` 及对应 ZIP，GitHub Actions 同步收集带版号的包。

性能模式保留原算法；清晰加入基于 [AMD CAS](https://github.com/GPUOpen-Effects/FidelityFX-CAS) 的锐化；质量使用 Lanczos-3 重建，并结合基于 [AMD FSR 1 EASU/RCAS](https://github.com/GPUOpen-Effects/FidelityFX-FSR) 的边缘处理和锐化。质量绘制两次，GPU 开销更高。像素继续使用最近邻。固定、可移动及跨屏接力规则保留，技术细节见 [框架说明](FRAMEWORK.md)。

## 验证范围

发布源码前的本地构建为 0 警告、0 错误，核心检查 **110/110**，桌面检查 **31/31**。另完成实际捕获、双屏三种比例的 4×/16× 双向接力、输入合并、固定取消、滚轮上下限反向、提示过期及模式资源复用检查。

0.7.0 发布提交的 [GitHub 构建](https://github.com/YMXKNebula/DailyToolkit/actions/runs/37754948461) 已通过。后续源码提交的结果见 [Actions](https://github.com/YMXKNebula/DailyToolkit/actions/workflows/build.yml)。本机报告、个人设置、画面和日志不上传。

## 限制和实验工具

AMD 算法已按放大框需求调整，版权见第三方声明。细密字形可比较清晰与质量后选择；像素模式在非整数倍率或子像素移动时可能跳变。

实测捕获链未稳定达到 120/144/240 FPS；GPU 绘制时间不能用于替代实际输出 FPS 或端到端延迟。

仓库里的 [AI 放大实验工具](../tools/DailyToolkit.LensAiPrototype/README.md) 用于测试 FSRCNN-small 2× 模型。已做 NVIDIA、AMD 的 GPU 执行和 CPU 数值对照，Intel 与实时产品集成尚未验证。正式软件不包含模型或推理运行时。

本次便携包及对应源码可从 [0.7.0 正式发布](https://github.com/YMXKNebula/DailyToolkit/releases/tag/v0.7.0) 获取。历史便携包仍可从 [0.6.1–0.6.8 二进制归档](https://github.com/YMXKNebula/DailyToolkit/releases/tag/archive-0.6.1-0.6.8) 获取；该历史标签保留原来的源代码基线。

## 项目文案修订

项目介绍、使用说明和开发文档已简化。这次更新源码和文案，现有 0.7.0 下载包及版本标签保留，程序文案随下次打包更新。
