# Codex 云端环境配置

本页用于创建 DailyToolkit 的开发环境。先读 [云端交接](CLOUD_HANDOFF.md)；软件使用说明仍以项目主页为准。

## 用户操作

1. 在 Codex 新建任务，选择 **Work in → Cloud → Select environment → Create environment**。
2. 选择 GitHub 仓库 `YMXKNebula/DailyToolkit`，如有提示，完成登录和仓库访问授权。以含本次交接资料的最新 `main` 为起点。
3. 让环境准备对话按下节配置依赖，检查准备结果。保存后选择 **Publish** 发布开发环境。
4. 等待 **Environment published**，再选择 **Start a new task**。
5. 在模型选择中确认 `gpt-6.1-sol` 与 `xhigh`，再发送 [新对话接续指令](CLOUD_START_PROMPT.md)。选项受账号、客户端和工作区限制；缺少指定组合时先停止接续，不自动换模型或改强度。

“发布开发环境”与 DailyToolkit 软件 Release 分开管理。本次只准备开发环境及交接资料，不发布新软件版本。原本地聊天、本地未跟踪文件、进程和登录状态不会自动转入云端；必要上下文已整理到仓库文档中。

## 环境准备要求

可以将下列内容发给创建环境时的 Codex：

```text
准备 YMXKNebula/DailyToolkit 的 Codex 云端开发环境。
先完整读 AGENTS.md、docs/CLOUD_HANDOFF.md、docs/CODEX_CLOUD.md。
此环境准备仅安装开发依赖并验证 Core，不修改应用功能、版本号或正式标签。
在云端环境内准备 Git、选中的 .NET 10 SDK 和 PowerShell 7（pwsh）。
不要为迁移把 WPF/Win32 程序改成其他平台，也不要在 Linux 上假装验证桌面程序。
使用仓库中的 scripts/check-cloud.ps1；不能安装或运行 DailyToolkit 安装版。
记录实际 OS、SDK、检查退出码和显式跳过项；失败时保留日志，不删除断言或假装成功。
只准备当前云端环境，不触碰用户电脑的网络、VPN、安装、自启和实际数据。
模型与推理强度保持 gpt-6.1-sol / xhigh；若实际配置无法满足，请说明并停止。
```

需要的依赖：

| 工具 | 用途 |
| --- | --- |
| Git | 获取源码、检查分支和提交 |
| .NET 10 SDK | 构建 `DailyToolkit.Core` 与核心测试运行器 |
| PowerShell 7 | 执行 `scripts/check-cloud.ps1`，命令名 `pwsh` |

Core 项目没有额外 NuGet 包引用。仍需在实际环境内核对 SDK 和还原结果；不要假设镜像预装完整依赖。准备期间所需的 SDK/PowerShell 官方下载地址及 NuGet 源按实际安装方式配置，仅调整该云端环境的访问，不改用户本机网络。NuGet 可能需要 `api.nuget.org`、`globalcdn.nuget.org`；SDK 和 PowerShell 按选定官方安装源补充所需域名。

不将本机凭据、`.local`、个人数据或整个 Codex 配置目录复制到环境中。正常读取公开源码和 Core 检查不需要发布令牌；之后确实需要远端写入时，按那次任务配置相应权限。

## 安装与启动入口

在仓库根目录执行。Install script 可使用下列还原步骤，但前提是上述工具已经在该云端环境安装并选好：

```powershell
dotnet --version
pwsh -NoProfile -File ./scripts/check-cloud.ps1 -RestoreOnly
```

准备阶段还应执行一次完整核心检查：

```powershell
pwsh -NoProfile -File ./scripts/check-cloud.ps1 -NoRestore
```

依赖已还原时，后续任务也可用 `-NoRestore`；首次检查或依赖改变时不带该参数：

```powershell
pwsh -NoProfile -File ./scripts/check-cloud.ps1
```

没有需要常驻的 Web 服务或后台守护进程。Start skill 写明“先核对仓库状态和依赖，按当前任务选择检查，不启动桌面程序或安装器”即可，不要设置持续采样、轮询或自动发布。

这些命令只构建 Core 和运行核心测试 DLL，不构建或启动 WPF，不调用安装器、自启、真实剪贴板或用户应用。测试内自有临时文件与 worker 会按原测试流程清理。脚本将 CLI/包缓存放入忽略目录，并在返回时恢复它设置的进程环境变量。

## 报告和验收

输出位于忽略目录 `.local/cloud-core-checks/`：`restore.log`（执行还原时）、`build.log`、`core.log`、`summary.json`。

- `scope` 为 `core-only`；只还原成功时 `status` 为 `restored`，不能当作测试通过。
- 构建与核心测试均成功时才为 `passed`。任何原生命令失败或缺少运行器结果都会使脚本失败。
- `hostOS`、`sdk`、`passed`、`total` 和 `skippedChecks` 记录实际现场。现有运行器对某些已跳过夹具仍输出 PASS，必须一起阅读显式 SKIP 记录。
- `windowsDesktopValidated` 始终为 `false`；即使在 Windows 上运行该入口，也没有执行完整 Desktop 或安装检查。
- 迁移准备时的 GitHub Actions 核心矩阵测试，与用户将要创建的 Codex 环境分别验收。CI 通过不能写成 Codex 云端环境已创建或已运行。

## Windows 检查继续使用现有流程

DailyToolkit 桌面项目是 `net10.0-windows10.0.26100.0` / WPF。修改代码可以在云端完成，但完整运行依赖 Windows；不能以 Core 编译通过替代桌面、Direct3D、权限、窗口或安装验证。

- [Cloud core checks](../.github/workflows/cloud-core.yml)：Linux / Windows 核心矩阵，只读权限，不打包或发布。
- [Build Windows app](../.github/workflows/build.yml)：现有 Windows 质量和可丢弃 installer 生命周期检查。PR 到 `main` 或常规 `main` 推送会触发，独立分支推送不能假设已经触发。
- 具体改动使用 `codex/` 前缀的开发分支，提交前核对差异、检查日志和用户数据保护，再按任务要求开 PR。没有发布要求时不创建 Release、不移动正式标签。
- 实机拖放、管理员跨权限启动、原生接收区视觉、混合 DPI、UAC 和真实用户数据验收仍需相应 Windows 环境及单独授权。

本次迁移资料同步采用文档/工具提交并跳过常规打包流水线；只手动运行新核心矩阵作验证，不改既有 Windows 工作流。以后真正修改桌面代码时仍需完整检查，不能沿用这个跳过理由。

## 官方参考

- [创建与发布云端环境](https://learn.chatgpt.com/docs/environments/cloud-environments)
- [Local、Worktree 与 Cloud](https://learn.chatgpt.com/docs/environments/modes)
- [模型与推理强度选择](https://learn.chatgpt.com/docs/models)

界面入口、模型可用性和账号权限可能变化，以实际界面及官方说明为准。现有本地对话不通过 host handoff 直接转入 Codex 云端；新任务通过仓库交接文档接续。
