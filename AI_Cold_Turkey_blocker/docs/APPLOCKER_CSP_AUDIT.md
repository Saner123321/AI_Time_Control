# 下一步：AppLocker 管理入口的只审计读写实验

WFP 已完成一轮 48 次有接收端证据的短时双栈实验。下一阻塞项是家庭版缺少 AppLocker PowerShell 管理模块。
本包仅测试另一条公开管理接口，**不实施应用封锁，不安装产品服务，不进入产品开发阶段**。

## 本轮做什么

- 只允许在用户已授权的 VMware 来宾中，从 64 位管理员 Windows PowerShell 5.1 手动运行。
- 在来宾 ProgramData 下创建唯一的实验目录，所有者与访问权限限定管理员组和 SYSTEM。
- 暂时创建两项唯一命名的 SYSTEM 计划任务：审计实验和三分钟后执行的独立清理任务。
- 通过微软公开的 WMI Bridge AppLocker EXE 类，测试 LocalSystem 身份下能否枚举、创建、读回、删除独立 GUID 分组中的策略。
- 策略仅为 `AuditOnly`，包含允许全部 EXE 和一条针对从不执行的实验路径的审计规则。
- 不创建 Enforce 模式，不执行任何被拒绝样本，不改变 AppIDSvc 启动方式，不写 WDAC/CodeIntegrity 节点。
- 如果发现已有 CSP 实例或现有 SrpV2 策略键，直接停止，不合并或覆盖现有规则。
- 正常结束删除本轮实例和计划任务；独立清理任务只在删除成功或确认未尝试写策略后撤销。
- 保留有 ACL 保护的实验文件供审计，不清理用户其他文件。Windows 自身的事件日志/空策略键可能保留，报告不宣称系统字节级无变化。

这里的 AuditOnly 策略写入只是管理接口实验。即使成功，也不能证明启动阻断、按用户限制、改名防绕过或更新卸载保护已经可用。

## 如何运行

使用当前新的关机快照 `AICT-phase0-baseline`，不要恢复或删除快照覆盖当前工作。
将 `dist/AICT-applocker-csp-audit-v1.zip` 解压到来宾的新目录；在该目录打开**管理员 Windows PowerShell**，运行：

```powershell
.\scripts\Invoke-AppLockerCspAudit.ps1 -SnapshotReference 'AICT-phase0-baseline'
```

通常会在几十秒内结束；最长等待 75 秒。不要改变执行策略。如果 SYSTEM 任务因脚本策略或提供程序问题无法运行，保留结果即可，不做绕过。

结束后将 `csp-audit-时间` 文件夹复制回主机项目的 `reports` 文件夹，告知路径。
输出包括 `runner-result.json`、可用时的 `audit-result.json`、`cleanup-result.json` 和哈希清单。

## 失败与恢复

如果提示保留清理任务，请至少等待三分钟，不要删除来宾 ProgramData 中的实验目录。
`runner-result.json` 记录清理任务的精确名称；需要重试清理时，可由用户在来宾管理员 PowerShell 中手动启动**该项任务**。
不删除其他计划任务，不直接修改现有策略，不删除虚拟磁盘或快照。

任何错误都应保存，包括 CIM 的 HRESULT/StatusCode。失败可能说明入口不可用、权限/提供程序限制或实验实现尚需调整，不能直接等同于 Home 不支持 AppLocker 执行。
若写入与清理成功，结果为 `AUDIT_ROUNDTRIP_PASS_NOT_BLOCKING`；阶段 0 总门槛和实际启动阻断仍保持未通过。

## 依据与未验证项

微软 KB5024351 说明更新后的 Windows 11 不再按版本或管理方式限制 AppLocker 策略执行；但 AppLocker CSP 页面仍未列 Home。
因此采用明确的隔离实验取得该版本真实结果，不把文档间差异自行解释成“保证可用”。

- [版本检查移除说明](https://support.microsoft.com/en-us/servicing/os/windows/2023/02/kb5024351-removal-of-windows-edition-checks-for-applocker)
- [WMI Bridge 的 LocalSystem 要求和实例操作](https://learn.microsoft.com/en-us/windows/client-management/using-powershell-scripting-with-the-wmi-bridge-provider)
- [AppLocker EXE 管理类](https://learn.microsoft.com/en-us/windows/win32/dmwmibridgeprov/mdm-applocker-applicationlaunchrestrictions01-exe03)
- [AppLocker CSP 分组与策略格式](https://learn.microsoft.com/en-us/windows/client-management/mdm/applocker-csp)

本机已做 PowerShell 5.1 语法检查、离线 XML 规则检查、主机拒绝检查。
未在主机创建任务、获取 SYSTEM、写入策略；来宾的任务创建与 CIM 写入/清理仍需实测。
