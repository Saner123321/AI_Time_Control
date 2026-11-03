# CSP 审计任务未产生结果：只诊断启动，停止新增策略实验

原实验：`375860026bf2434088478e4ac27f57fe`。
原始结果只有 `runner-result.json`：工作任务返回 1、无工作结果；审计任务已移除，清理任务保留。
这不能证明工作程序是否到达 CSP 调用，也不能证明清理成功。不要再次运行审计包。

## 新工具的精确范围

- 绑定这次 RunId 和冻结的 v1 工作脚本哈希；检查原目录、脚本所有者及 ACL，不替换原脚本，不改原 ACL。
- 只读查询该实验原计划任务状态；复制后来可能生成的 audit/cleanup JSON。
- 在新的受保护诊断目录中，用一项临时 SYSTEM 任务运行原脚本的 `-Mode Validate` 离线 XML 校验分支，捕获 stdout、stderr 和退出码。
- 同一 SYSTEM 进程环境下查询执行策略列表，仅记录字符串值；不设置执行策略，不使用 Bypass、Unrestricted、EncodedCommand 或脚本内容间接执行。
- 使用 `-File` 运行原脚本，保留原本的脚本策略检查。独立的固定 `-Command` 仅执行 `Get-ExecutionPolicy` 只读查询，不加载原脚本，不调用任何策略读写。
- 不调用 Audit/Cleanup 分支，不创建 CSP 实例，不删除 CSP 实例；不启用系统日志采集或修改 Windows 安全设置。
- 只删除本工具自己创建的诊断任务。原清理任务和原实验目录保持原样，避免在状态未知时丢掉恢复入口。

## 用户操作

将 `dist/AICT-csp-startup-diagnostic-v1.zip` 解压到来宾新目录，在**管理员 Windows PowerShell 5.1** 中运行：

```powershell
.\scripts\Collect-CspStartupDiagnostics.ps1
```

通常几十秒内完成，最多等待约 70 秒。不要重跑原 Audit 包，不删除原 ProgramData 实验目录。
将生成的 `csp-startup-diagnostic-时间` 文件夹复制回主机项目的 `reports`，告知路径。
脚本也会直接显示捕获的启动错误；若仍阻塞，保存结果，不自行更改执行策略。

输出可能包含 `diagnostic-result.json`、`validation-stdout.txt`、`validation-stderr.txt`、
`validation-exitcode.txt`、`system-execution-policy.json`、原工作程序后来生成的结果及哈希清单。
没有采集其他任务、浏览记录、账户密码或通用系统事件日志。

本机仅验证语法、拒绝主机运行、命令引用/输出重定向和离线 XML；没有在主机创建 SYSTEM 任务或访问 CSP 策略。
当前无法判断原先退出码 1 的根因；本包用于收集证据，不能被算作修复或 AppLocker 通过。
