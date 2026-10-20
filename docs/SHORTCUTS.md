# 快捷键检查

设置中的“快捷键”列出放大镜、浮笺（显示和聚焦）、剪贴回溯、文件集装箱的五项全局快捷键，也列出软件主动定义的窗口内操作。普通文本框自带的复制、撤销等编辑键不重复列入。

`ShortcutCatalog` 监听现有配置与开关的变更，按修饰键和虚拟键比较内部重复；已停用工具的组合也参与比较，并标明停用状态。各工具页与总览使用同一份提示。它不添加定时器、键盘钩子或试注册。

输入被拒绝的重复组合会在对应快捷键旁显示“未更改”提示，已保存的组合保持原值；再次成功设置或更改相关配置后清除提示。

Windows 检查来自微软的已知系统组合：例如 Win+L、Win+R、Alt+Tab、Ctrl+Shift+Esc。F12 提示调试器保留，其他 Win 组合提示 Windows 保留。系统功能可能因 Windows 版本、语言、设置而不同，所以一般提示使用“可能触发系统操作”。

其他程序是否占用，沿用工具实际启用或修改快捷键时的 `RegisterHotKey` 结果；错误 1409 显示“已被占用”，其他失败显示“注册失败”，不会把权限或桌面限制误报成已查明的占用。编辑期间暂停注册有独立状态。关闭工具不会为了检测而重新注册。

检查不能枚举 Windows 或其他软件的全部快捷键，注册成功也不能证明没有任何系统或窗口内行为重叠。停用工具尚未执行注册，不能确认外部占用。

参考：

- [Microsoft Learn：RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)：Win 组合保留、F12 保留、注册失败和错误信息。
- [Microsoft Support：Windows keyboard shortcuts](https://support.microsoft.com/en-us/windows/keyboard-shortcuts-in-windows-dcc61a57-8ff0-cffe-9796-cb9706c75eec)：已知系统组合与动作。
- [Microsoft Learn：System Error Codes (1300–1699)](https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--1300-1699-)：`ERROR_HOTKEY_ALREADY_REGISTERED`（1409）。
