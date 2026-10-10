# 构建和开发检查

本页面向参与源码开发的贡献者。程序使用说明见 [项目主页](../README.md)。

## 从源码构建

需要 Windows 和 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```powershell
.\scripts\build.ps1
.\src\DailyToolkit.Desktop\bin\Release\net10.0-windows10.0.26100.0\DailyToolkit.exe
```

## 打包

先生成便携版：

```powershell
.\scripts\publish.ps1
```

输出为 `artifacts/DailyToolkit-<版本>-win-x64/` 和 `DailyToolkit-<版本>-win-x64-portable.zip`。首次打包会下载 .NET 运行时。

安装版使用 [Inno Setup](https://jrsoftware.org/isdl.php) 6.5 或更新版本：

```powershell
.\scripts\package-installer.ps1
```

可以用 `-Compiler` 指定本地 `ISCC.exe`。安装包输出为 `DailyToolkit-<版本>-win-x64-setup.exe`，与便携包共用程序文件，两个包的摘要写入 `SHA256SUMS.txt`。GitHub Actions 使用运行环境已有的编译器执行构建、检查和打包。

发布文件包括安装包、便携 ZIP 和校验文件。编辑公开发布说明时，写明可使用的功能、可调整的选项、用户可见的修复和必要的操作步骤。测试结果与实现细节记录在开发文档中。

## 屏幕局部放大检查

`scripts/build.ps1` 检查设置保存、旧版兼容、导航排序、收藏、快捷键、拖动、照片预览和真实着色器输出。颜色检查要求边框为选定的 RGB 值，框内像素保持不变。

核心检查还覆盖四边立即解耦、重新耦合及剩余位移、采样边界、镜像轴接力、固定冻结、负坐标与不同尺寸屏幕。交互桌面可在构建后运行 `tests/DailyToolkit.Desktop.Tests/bin/Release/net10.0-windows10.0.26100.0/DailyToolkit.Desktop.Tests.exe --live-lens-only`，检查真实 Windows 捕获在固定偏移下继续刷新，以及待切换位置取消、接力时连续位移和资源复用。该检查只显示自有纯色窗口，不生成桌面图片或修改用户设置；单屏时实机跨屏检查明确跳过。

同一检查还验证高频输入不在鼠标回调内移动窗口或触发渲染，最新位置不丢失，固定与停止会取消待更新位置。拖动处理耗时可用同一测试程序的 `--drag-benchmark <本地JSON路径>` 对比，测试场景为自有纯色窗口；数据只衡量回调处理时间，不等同于从鼠标到显示器的完整延迟，渲染次数包含参数重绘，不能当作源画面的帧率。

本机交互桌面可运行以下检查，输出目录需先存在：

```powershell
DailyToolkit.exe --check-lens <PNG路径>
DailyToolkit.exe --check-lens-shortcuts <JSON路径>
DailyToolkit.exe --benchmark-lens <JSON路径> <旧版HLSL路径>
```

前两项短暂显示自有测试窗口，验证捕获、真实鼠标点击和拖动、开启时反复切换位置模式、辅助线、资源释放和跨屏往返，只保存测试区域。固定模式的点击应传到测试窗口，可移动模式的点击应被浮窗接住。快捷键检查临时使用 F24，结束后恢复鼠标位置，不修改用户设置。单屏时跨屏结果为 `null`。

基准使用生成的纹理，通过 GPU 时间戳比较处理耗时和像素输出，不包含读回、系统捕获或呈现等待，不能代替游戏中的帧率测试。默认颜色沿用原着色器输出，改色只改变两像素宽的边框。

底层 Direct3D 使用 Vortice，工具逻辑和算法由本项目实现。依赖许可证见 `THIRD_PARTY_NOTICES.txt`。

## 浮笺检查

自动检查使用隔离的笔记文件和测试窗口，覆盖保存、原生快捷键、鼠标不聚焦、选词、透明度、边框、整数缩放及跨屏移动。实际使用时还应检查：

1. 在浏览器或编辑器中呼出浮笺，连续按聚焦快捷键应在编辑与失焦之间切换；选区保留，隐藏中的笔记不应被该键显示。
2. 关闭点击聚焦时，失焦的所有鼠标操作应到达下方程序；开启后，只有左键聚焦浮笺，右键、中键和滚轮仍到达下方程序。取消聚焦后返回此前程序。聚焦后双击正文应选词，失焦不能拖动或缩放浮笺。
3. 使用中文输入法保持候选组合，第一次 Esc 取消组合；无组合时 Esc 取消聚焦。检查右键菜单、复制剪切粘贴与撤销。
4. 在混合 DPI 或上下排列屏幕间拖动，拖动点应跟随指针；改变显示器布局或拔掉副屏后应找回窗口。人工拔插不由自动检查代替。
5. 切换配色与独立颜色，检查预览和实际笔记的正文边框、滚动条透明度及失焦后的隐藏状态。聚焦时，固定和可移动模式都能缩放；关闭手动缩放后隐藏缩放角，保存后重开应保留整数尺寸和设置。长正文的上下遮挡边缘应渐隐，短正文和失焦正文不应渐隐。
6. 同时检查屏幕放大的快捷键、固定/移动、滚轮、画质，以及主题、收藏和工具启停。

## 其他开发文档

- [贡献要求](../CONTRIBUTING.md)
- [项目结构与工具接入](FRAMEWORK.md)
- [开发计划](PROJECT_PLAN.md)
- [0.7.9 稳定性审查](STABILITY_REVIEW_079_2026-10-09.md)
- [安装包验证记录](INSTALLER_CHECKS_079.md)
