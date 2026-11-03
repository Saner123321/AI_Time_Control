# 服务与AppLocker连接：VM联调 v1

## 当前结果（2026-11-03）

`service-policy-20261103T014001416Z` 已复核通过：60次正式启动观察、18项普通令牌权限检查、实际服务中断期间9次拒绝、
恢复后规则/冻结修改持久化一致及最终清理均通过；单次调度阻断观察上界1.0064008秒。回执见 `reports/service-policy-v1-audit-20261103T014001416Z.json`。
**此批无需重跑。** 已有操作者成功恢复并登录桌面的确认，后续复用这份基线无需为重复确认而再次恢复。
没有整台Windows重启或真实额度采集证明；home/service-restart通过，不授予完整阶段0通过。下文保留原包操作与边界。

此包把.NET10规则内核、SQLite、真实LocalSystem服务及AppLocker执行接口连接起来。
**这是只允许在VMware来宾运行的联调版本，不是主机安装器。** 当前仅允许封锁本包生成的一个无害测试EXE及其同内容副本。
不接受用户指定的任意应用或网址，不连接桌面开发预览，不配置浏览器、WFP、驱动或已有工作软件。
只管理运行脚本的来宾账户；EXE拒绝规则指定该账户SID，不针对SYSTEM和其他账户。

## 运行前的恢复基线

阶段0中的恢复基线仍未独立核验。项目 `ARCHITECTURE.md` 要求“第一次系统写入前必须建立并验证恢复快照”。
先在VMware中确认**关机状态、4GB内存**的恢复快照可以正常恢复并进入来宾桌面。
若不确定旧快照，先保留来宾重要文件到主机，再在当前干净状态建立新的关机快照并恢复一次，记录实际名称。
不要恢复此前带16GB运行内存、已经失败的旧快照；恢复旧快照会回退其中后续改动。

`-SnapshotRestoreConfirmed` 是操作者对成功恢复并进入桌面的明确确认，不是脚本自动验证快照。
报告仍分别记录 `snapshotRestoreOperatorConfirmed=true`、`snapshotRestoreIndependentlyVerified=false`，不会把这个开关作为完整阶段0通过证据。
尚未成功恢复时不要添加该开关。本包也不会自动恢复快照或重启电脑。

## 一次完成的实验

把 `AICT-service-policy-lab-v1.zip` 复制到虚拟机桌面，完整解压成 **AICT-service-policy-lab-v1** 文件夹。
在桌面打开管理员、64位 **Windows PowerShell 5.1**；下列命令包含解压文件夹名称，不要先进入文件夹。
快照名称填写刚刚实际恢复成功的那个名称：

```powershell
.\AICT-service-policy-lab-v1\scripts\Invoke-ServicePolicyLab.ps1 -SnapshotReference 'AICT-phase0-baseline' -SnapshotRestoreConfirmed
```

本次不需要手动重启Windows。程序自动执行基线、规则到点切换、一次服务自身崩溃、SCM恢复、持久化核对和清理。
会在ProgramData创建唯一GUID的受保护目录、一个手动启动LocalSystem测试服务，以及临时探针/独立清理任务。
独立清理约在开始后8分钟，或意外重启后2分钟执行；不要停用它。正常结束会提前清理并撤销任务。
服务是手动启动，**本轮不验证开机启动**；SCM第一轮恢复故意延迟5秒，给探针观察服务实际中断的窗口。
若AppIDSvc未运行，前置检查拒绝实验并保留现有服务配置，不自行改变它。

正常显示 `batch passed and cleaned` 后，把生成的整个 **service-policy-时间戳** 目录复制回主机项目 `reports`，发送路径即可。
失败也发送完整报告，不反复重跑、不删ProgramData目录、不覆盖定位文件。会保留失败项，不把部分成功当成全部通过。

## 立即清理

若清理没有确认完成，在同一桌面管理员PowerShell运行：

```powershell
.\AICT-service-policy-lab-v1\scripts\Recover-ServicePolicyLab.ps1
```

清理经独立SYSTEM任务执行MDM设备接口；先禁用并停止本轮精确服务，防止恢复动作重启它，再删除本轮EXE策略，最后删除伴随Appx策略。
恢复不依赖数据库或配置文件完好；可从完整匹配的固定策略推导拒绝SID。若伴随Appx策略已经缺失，仍可撤销完整匹配的本轮EXE策略。
策略分组、规则GUID、固定PE哈希、服务完整路径/账户及任务定义都会核对。不同内容或已有策略冲突会拒绝覆盖/删除。
清理失败保留独立任务；从VMware控制台使用已验证快照恢复是最后的来宾恢复手段。
不会删除数据库、实验目录或原始证据。当前恢复脚本不把损坏文件重新初始化为空规则。

## 具体验证及尚未覆盖的内容

1. 非提权令牌下：原名、改名、目录外复制的测试EXE和不同内容对照均能正常启动。
2. 真正规则由服务自身首次启动创建并事务保存：固定循环起点为初始时间后15秒，封锁10分钟、休息1分钟；同时冻结一份24小时后才可申请生效的无害修改。
3. 服务根据同一Core规则引擎计算，目标出现AppLocker拒绝码1260；三种身份副本拒绝，对照正常。记录共享单调时钟上的边界及首次拒绝，观察两秒目标的上界；不能把目标当成通过。
4. 指定用户可通过有界管道查询状态；受保护DACL只允许SYSTEM和该SID，拒绝网络登录；服务再次核对实际调用SID，客户端核对管道端PID与SCM真实服务PID。
   查询接口不接受停止、规则写入、时间、额度或路径字段。测试拒绝停止请求及注入可信时间。
5. 非提权探针请求停止、重配、删除、修改服务权限、自定义控制、启动及进程结束的句柄权，另测配置/权限样本写入拒绝；不实际停止/删除/结束服务或写文件字节。
6. 管理员仅发固定控制码128，使服务记录一次性标记后退出自己的进程；实际中断期间继续启动测试程序，检查拒绝仍在。
7. SCM恢复后，读取同一SQLite：规则摘要、待生效修改ID/内容摘要/等待截止保持一致，修订号前进；重新核验权限和实际拒绝。
8. 原始状态重开后，时钟主动为未核验：不以系统墙钟恢复可信，也不提前解除当前目标；这是一种保守恢复，**尚未接入正式可信时间恢复源**。
9. 精确清理后，目标和对照重新允许启动；服务、拥有的EXE/Appx节点和临时任务消失。共60次正式启动观察，另有过渡探测。

AppLocker持久策略只保证本轮已经启用的限制在服务中断时仍存在；服务中断期间不能计算新的未来时段，不能因此声称任何故障下无缝调度。
本轮没有真实前台采集、额度消耗、网站控制、Windows重启、断电、MSIX、发布者规则或全系统白名单测试。
不承诺管理员无法改服务/策略；不把本批通过等同完整阶段0/阶段1通过。
服务状态区分期望值、最后一次策略读回及故障；策略保存和读回仍需本轮实际启动探针确认。

## 结构与所有权

- `Aict.Enforcement`：仅支持PE SIP SHA256黑名单计划的纯编译层；不支持的维度明确拒绝。
- `Aict.Service`：.NET10 ServiceBase宿主、SQLite提交、单调时间推进及有身份验证的只读管道。
- `AictPolicyBridge.exe`：复用家庭版已经验证的系统自带.NET Framework／Windows MI路径，通过固定命令处理固定样本，不接受任意XML/路径/命令。
  独立桥接是当前兼容性选择；服务调用开销计入实际观察，未声称已经满足两秒目标。不是SYSTEM PowerShell绕过方案。
- CSP只使用新的随机分组；创建前检查EXE/Appx及本地SRPv2冲突，读回精确XML，清理按完全相同的所有权形状删除。
  不合并其他工具的策略；其他管理源和版本的完整兼容矩阵仍待隔离验证。

服务和桥接源码、生成样本契约、运行时和SHA256清单随包冻结。所有系统性入口在物理主机守卫处拒绝，离线编译/契约检查除外。
日志只含本轮测试程序、状态和错误，不导出来宾账户SID配置、其他应用信息或完整浏览记录。
主机只读复核命令（在项目父目录；Python3需与本机实际安装相符）：

```powershell
py -3 .\AI_Cold_Turkey_blocker\scripts\verify_service_policy_lab.py .\AI_Cold_Turkey_blocker\reports\service-policy-实际时间
```

参考：[Windows服务.NET支持](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service)、
[命名管道ACL接口](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeserverstreamacl.create?view=net-10.0)、
[AppLocker CSP分组与策略](https://learn.microsoft.com/en-us/windows/client-management/mdm/applocker-csp)。
