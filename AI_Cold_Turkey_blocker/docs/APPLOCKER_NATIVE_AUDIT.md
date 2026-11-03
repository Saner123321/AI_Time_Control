# 使用 C# 工作程序继续 AuditOnly 管理入口实验

## 当前结果

2026-11-02 回传 `csp-native-audit-20261102T083356189Z` 已验证：v2 创建、读回、删除成功，任务已清理。
以下是实验设计与此前故障记录；无需重跑本包。下一批实际启动实验见 `APPLOCKER_ENFORCE_SMOKE.md`。

## v2：针对有来源依据的传输编码差异

上一轮原生程序能读取 CSP，但创建时返回通用 Failed。现已确认收尾报告：两轮实验任务均已撤销或原本不存在，分组无残留。
本轮保留 C# / MI 和 AuditOnly 范围，**只修正 Policy 字符串的 XML 传输编码**，不更改系统版本或进行设备管理注册。

- v1 直接传原始 RuleCollection XML；公开 AppLocker WMI Bridge 实现会先调用 HtmlEncode。
- v2 先构造并验证原始 AuditOnly XML，再 HTML 编码一次，作为 MI 的 Policy 字符串。
- 读回时识别原始 XML 或单层 HTML 编码，分别处理；不递归解码，不接受双层编码或 DTD。
- 本地契约测试覆盖中文和 `&` 路径、XML 消息中字符串保留、原始 XML 与文本值差异、拒绝双层编码和非 AuditOnly 内容。
- 本地模型并不证明 Windows 内部实现或家庭版现场成功；目前这是有来源支持的故障假设，必须以新来宾结果验证。
- 已修复 Windows PowerShell 5.1 的 UTF-8 JSON 解码，并加入实际中文数据回归；保留之前的失败发布包和报告。
- 新版记录请求编码、读回编码、失败发生阶段及可用的 CIM 扩展错误码，不导出已有策略正文。

参考：[AppLocker 工具作者发布的 WMI Bridge 实现](https://www.powershellgallery.com/packages/ManageApplocker/24.5.13.1/Content/ManageApplocker.ps1)
明确在设置 Policy 前进行 HTML 编码；[微软 WMI Bridge XML 示例](https://learn.microsoft.com/en-us/windows/configuration/assigned-access/configure-single-app-kiosk)
也展示该编码步骤，但其 Assigned Access 功能适用版本不用于推断本项目 AppLocker 的版本支持。
本项目没有运行这些外部脚本，也没有采纳它们的批量删除、远程下载或脚本策略覆盖操作。

---

用户回传的启动 stderr 明确显示：SYSTEM 环境拒绝加载原 `.ps1`。这是脚本加载层错误，不是 AppLocker CSP 的接口返回值。
本轮不改执行策略，不间接执行原脚本内容；用单独编译的 C# 程序，通过公开 Windows MI 客户端操作相同 CSP 类。
这与产品计划中的原生 .NET 后台方向一致；本包仍只是 .NET Framework 的隔离实验，不是 .NET 10 产品实现。

## 具体行为

- 父启动脚本仍由用户在来宾管理员 Windows PowerShell 中运行；SYSTEM 任务只运行固定 C# EXE，不启动 PowerShell。
- 不复制 Windows MI DLL：编译引用主机已有的 Windows MI 程序集，来宾运行使用自身 Windows 组件。
- 新的独立 ProgramData 实验目录仅管理员组和 SYSTEM 可访问；EXE 与捕获输出的固定命令文件均由管理员组拥有。
- 两项临时 SYSTEM 任务分别执行 Audit 与三分钟后的独立 Cleanup；stdout、stderr、退出码和逐步日志一并保存。
- C# 客户端首先核对旧分组 `375860026bf2434088478e4ac27f57fe`。若有旧实例，仅在父节点、EXE 键、AuditOnly 内容、允许全部规则和原实验路径均吻合时删除；否则保留并报错。
- 只有确认旧分组不存在时，父脚本才考虑移除旧清理任务；旧任务定义有变化就不动它。
- 新实验仍仅创建 AuditOnly 的独立 GUID 分组，读回后删除。检测到任何既有其他 CSP 实例或 SrpV2 策略键时停止，不覆盖。
- 不执行被拒绝程序，不写 Enforce/WDAC/CodeIntegrity，不修改服务启动方式。
- 新分组清理仍使用精确父节点和 EXE 键，不删除其他规则或用户文件。清理任务未撤销时继续保留受保护目录。

AuditOnly 读写通过也不等于应用启动阻断通过。Windows 自身日志或空键可能保留，实验不承诺字节级无变化。

## 在来宾运行

保留现有新的关机快照。将 `dist/AICT-applocker-native-audit-v2.zip` 解压到来宾新目录，在**管理员 Windows PowerShell 5.1** 中运行：

```powershell
.\scripts\Invoke-AppLockerNativeAudit.ps1 -SnapshotReference 'AICT-phase0-baseline'
```

通常几十秒内完成，最长等待约 110 秒。后台程序采用 Windows MI API，未提供或设置任何 PowerShell ExecutionPolicy 覆盖参数。
将新生成的 `csp-native-audit-时间` 文件夹复制回主机项目 `reports`，告知路径。

如果没有 native result，本包也会收集启动 stderr 和已写入的逐步日志，能区分加载失败、MI 会话、旧分组核对、创建、读回和清理。
如果提示保留清理任务，至少等待三分钟，不删除该目录；保留所有日志。
旧的脚本审计包不再使用，启动诊断文件也继续保留，不追改旧失败结果。

## 实测边界

本机已完成编译、九项离线 XML/传输断言、中文 UTF-8 读取回归、Audit/Cleanup 两种模式的主机拒绝检查及启动脚本语法检查。
未在主机创建 SYSTEM 任务或访问 AppLocker 策略；在来宾的实际 MI 创建/读回/删除现已成功，仍未验证 Enforce。
策略执行版本限制与管理入口是分开的验证问题；本包没有绕过 Windows 版本检查或使用未公开接口。

参考：[公开 Windows MI 客户端 API](https://learn.microsoft.com/en-us/windows/win32/wmisdk/creating-wmi-clients)、
[WMI Bridge 设备级 LocalSystem 要求](https://learn.microsoft.com/en-us/windows/client-management/using-powershell-scripting-with-the-wmi-bridge-provider)、
[AppLocker EXE 类](https://learn.microsoft.com/en-us/windows/win32/dmwmibridgeprov/mdm-applocker-applicationlaunchrestrictions01-exe03)、
[PowerShell 执行策略](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_execution_policies)。
