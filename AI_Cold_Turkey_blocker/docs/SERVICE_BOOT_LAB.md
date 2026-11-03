# 后台服务权限、崩溃恢复和重启：合并实验

## 当前实测结果

`service-lab-finish-20261102T123617226Z` 已验证通过：三个阶段共30项权限观察、SCM受控崩溃恢复、正常重启自动启动与测试状态持久化。
观察到恢复耗时1.7971629秒，启动次数1→2→3，三个快照持久化失败次数均为0；服务、独立清理任务已移除，源二进制匹配冻结包。
无需重跑。此包没有应用封锁策略，因此规则在服务故障/重启期间仍生效的整体验收继续保持未通过。
下文保留原实验操作和边界。

本包仅验证服务基础设施。**不创建 AppLocker/WFP/浏览器策略，不封锁应用或网站，不修改已有服务，不改账户或执行策略。**
它会在 VMware 来宾中暂时安装一个唯一 GUID 的 LocalSystem 自动启动测试服务，每秒更新本轮目录中的小型状态文件。
不是正式产品；不授予封锁跨重启、额度跨重启、安装卸载锁定或完整阶段 0 通过。

## 操作：在解压文件夹的父目录运行

将 `AICT-service-boot-lab-v1.zip` 复制到 Windows 虚拟机桌面，解压成 `AICT-service-boot-lab-v1` 文件夹。
在**桌面**打开管理员、64 位 Windows PowerShell 5.1。以下命令按用户偏好均包含解压文件夹名字，不要先进入该文件夹内部。
保留现有 `AICT-phase0-baseline` 快照；本包记录名字，不会自动证明快照恢复有效。

第一步：一次完成服务安装、权限观察与受控崩溃恢复。

```powershell
.\AICT-service-boot-lab-v1\scripts\Prepare-ServiceLab.ps1 -SnapshotReference 'AICT-phase0-baseline'
```

只有出现 `Prepare passed` 后，才手动**重启这台虚拟机**，登录同一来宾账户。
重新在桌面打开管理员、64 位 Windows PowerShell，执行第二步：

```powershell
.\AICT-service-boot-lab-v1\scripts\Finish-ServiceLab.ps1
```

收尾会再次检查非提权权限，然后禁用、停止并删除本轮测试服务，核对服务已不存在，再撤销独立清理任务。
将生成的完整 `AICT-service-boot-lab-v1\service-lab-finish-时间` 文件夹复制回主机项目 `reports`，发路径即可；
其中会包含准备阶段的原生记录，不必分别搬运多次报告。

**及时重启和收尾：**独立清理任务会在准备后约 30 分钟，或下次系统启动约 10 分钟后执行，以先发生者为准。
错过时限通常使本次服务重启验收无法完成，不会封锁工作应用。不要为追求通过而停用清理任务。

## 准备失败、暂不重启或要提前撤销

不要反复运行 Prepare：同一解压目录的定位文件不会覆盖。失败报告和受保护目录均保留。
要立即退出实验，在同一位置运行：

```powershell
.\AICT-service-boot-lab-v1\scripts\Recover-ServiceLab.ps1
```

恢复程序只接受原发布二进制、指定 GUID 服务及匹配的可执行路径/账户/服务类型；不按前缀批量删除。
它不依赖测试状态/config 完好，即使服务进程已经损坏退出也可清理。
若收尾/恢复不能确认服务已不存在，会保留独立清理任务和日志。不要删除 ProgramData 实验目录；仍失败可从 VMware 控制台恢复快照。
准备成功后若误提前运行 Finish，它会报告未观察到重启，保留服务待重启，仍受清理时限约束。

## 检查内容

### 非提权探针

使用现有账户的交互 Limited 令牌，分别在崩溃前、恢复后、重启后观察：

1. 可以查询真实运行的服务；请求停止、重配、删除、改权限、发送自定义控制以及启动的访问权均应返回拒绝访问 5。
2. 请求服务 LocalSystem 进程的结束权应被拒绝，目标 PID 必须来自正在运行的真实服务。
3. 配置、状态和同权限测试文件应拒绝写句柄；同时检查真实可执行文件、目录、配置的所有者与权限。

不会真正以普通身份发出停止/删除/结束操作，也不写入文件字节。若错误授予权限，会记录失败后清理，
不为证明权限错误再破坏服务。运行中可执行文件的共享冲突不能冒充权限拒绝，因此用未映射的测试文件验证实际写入拒绝。

这验证**当前账户的非提权令牌**，不是另建标准账户的完整多用户测试。管理员仍有最终控制权。
它也不是受保护进程、杀毒软件受保护服务或抵抗管理员卸载的实现。

### 崩溃与重启

- 用 Service Control Manager 注册独立进程、LocalSystem、自动启动服务，读回配置与精确服务权限。
- 服务对象由管理员组拥有；SYSTEM/管理员拥有完整控制，指定账户只允许查询/读取权限，不授予自定义控制。
- 管理员发送唯一的实验控制码 128。服务记录标记后仅结束**自己的进程**一次，不执行任意命令或结束其他进程。
- Windows 恢复动作配置为：第一次一秒后重启，第二次两秒后重启，再次失败不执行动作；无命令、无系统重启，60 秒无失败后计数重置。
- 检查崩溃后 PID/实例改变、开机时间相同、持久启动次数精确加一，以及权限未放宽。
- 用户重启后，检查开机时间和服务实例改变、自动启动类型、LocalSystem 身份、启动次数再加一、心跳测试计数未回退，再测权限。
- 服务启动时尽早连接 SCM，虚拟机守卫直接读取 SMBIOS 注册表标识，避免在连接 SCM 前等待 WMI；开机时间在 OnStart 中读取。
- 清理先禁用本轮服务，防止已排队的 SCM 重启将其拉起，再停止和删除；不更改系统的全局服务超时设置。

测试状态是一个受保护、原子替换的 JSON 文件，不是 SQLite、使用时长或每日额度。它只能证明本批测试状态没有丢失。
真实规则调度、额度、异常时钟和断电数据库恢复仍待产品引擎及后续验收。

## 范围、隐私与证据

服务只读机器虚拟化/开机信息、读写自身 ProgramData 实验目录；没有网络请求、驱动、注入或自我复制。
记录账户 SID 的配置留在本轮受保护目录，不复制到报告；报告不含其他服务配置、其他应用日志或用户文档内容。
程序和任务均按发布包哈希核验；独立 SYSTEM 清理任务只执行受保护目录里的固定原生清理模式，不执行 PowerShell。
不会删除源文件或实验目录；策略层完全未参与，故没有“封锁跨重启通过”的结论。

主机复核返回报告：

```powershell
python D:\Projects\AI_Time_Control\AI_Cold_Turkey_blocker\scripts\verify_service_lab.py D:\Projects\AI_Time_Control\AI_Cold_Turkey_blocker\reports\service-lab-finish-实际时间
```

检查器区分准备、权限、崩溃恢复、服务自动启动与测试状态持久化、最终清理；不自动授予完整阶段通过。
本机已完成编译、5 项原生 ABI/权限契约检查、6 种原生模式与3个入口的物理主机拒绝、四个 WinPS 5.1 脚本语法检查；
当前 Python 回归 47 项通过。**没有在物理主机安装服务；来宾实际服务行为尚待验证。**
另有三个本地状态 IO 案例验证并发旧/新快照、UTF-8、禁止覆盖证据及临时文件收尾；读取时允许原子替换，避免采集者干扰保存。

参考：[服务权限](https://learn.microsoft.com/en-us/windows/win32/services/service-security-and-access-rights)、
[服务恢复动作](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_failure_actionsw)、
[更改服务恢复配置和排队重启边界](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-changeserviceconfig2w)、
[按句柄设置安全信息](https://learn.microsoft.com/en-us/windows/win32/api/aclapi/nf-aclapi-setsecurityinfo)。
