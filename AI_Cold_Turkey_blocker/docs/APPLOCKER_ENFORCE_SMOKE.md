# 家庭版真实应用启动阻断：一轮合并实验

## 当前结果

`applocker-enforce-20261102T093118262Z` 已通过：36 次正式观察、策略与三个任务清理均核验，源二进制与冻结 v1 包相符。
首次观察到阻断 0.2420571 秒，仅一次，未授予 20 次时延验收通过；目录外副本按本包设计仍允许。
日志导出遇到某些事件无 `EventData` 的解析错误，未影响直接启动证据和清理；后续源代码已修复并检查八个 XML 案例。
**无需重跑本包。** 下一批见 `APPLOCKER_IDENTITY_LATENCY.md`。下文保留原实验说明及原始范围。

这是阶段 0 的目录范围强制执行实验，不是可用产品。仅在已有恢复快照的 VMware Windows 来宾运行。
主机只做编译和离线检查；启动脚本及原生 Enforce、Cleanup、Probe 三种模式均拒绝物理主机。

## 一次运行检查什么

同一无害 EXE 被复制成四份：受限目录中的原名、改名副本、例外程序，以及目录外副本。
三个阶段各重复三次，每次启动四份样本，共 36 次正式启动观察，另外记录传播等待时的尝试。

| 样本 | 基线 | 强制规则期间 | 撤销后 |
|---|---|---|---|
| `Targets\Target.exe` | 退出 0 并返回固定标记 | Windows 启动拒绝码 1260 | 恢复退出 0 与标记 |
| `Targets\Renamed.exe` | 同上 | 同上 | 同上 |
| `Targets\AllowedControl.exe` | 同上 | 通过拒绝规则的显式例外继续运行 | 同上 |
| `MovedTarget.exe` | 同上 | 继续运行，用于显示目录路径规则的边界 | 同上 |

还在基线阶段启动一个最长存活 100 秒的样本，检查规则生效后其是否仍在运行，再由探针结束自己启动的该进程。
这观察 AppLocker 对已运行程序的行为，不是产品的自动关闭功能，也不结束任何真实工作应用。

所有启动由当前账户的**非提权、交互桌面令牌**完成。不是 SYSTEM 启动验证，也没有新建用户或修改账户。
这能验证非提权调用，不能替代以后独立标准账户的服务权限和防绕过验收。

不能把普通“拒绝访问”、超时或一个不明进程的退出 0 当作封锁/允许成功。
拒绝必须未启动且取得 1260；允许必须样本完成、退出 0、返回固定标记且无 stderr。

## 策略和清理

- 启动前拒绝已有 EXE/StoreApps CSP 实例或本地 SrpV2 策略键；不合并、覆盖或删除别人的策略。
- 使用唯一 GUID 分组。先创建并读回允许所有已签名打包应用的 Appx 配套规则，再创建 EXE 规则。
- EXE 规则固定为允许所有、仅拒绝本轮受保护 `Targets\*`，在拒绝规则中明确排除 `AllowedControl.exe`。
- 配套 Appx 规则是必要的安全处理：微软指出，启用 EXE 规则而没有打包应用规则会导致打包应用被阻止。
- 所有策略 XML 均先按固定结构验证，再 HTML 编码一次；读回接受原始 XML 或单层编码，拒绝 DTD、多层编码和额外规则/属性。
- 临时 SYSTEM 工作任务不运行 PowerShell。探针任务使用当前账户的交互 Limited 令牌，无密码输入。
- ProgramData 根目录、样本、配置、工作程序和命令文件由管理员组拥有；普通令牌只能读取。仅 `probe` 子目录允许该账户写观察结果。
- 创建策略前先注册独立清理任务：三分钟后、或下次系统启动时运行，不依赖桌面窗口存活。
- 正常工作程序在 `finally` 中撤销；清理按精确分组、实例和固定策略正文验证，先删 EXE，确认不存在后再删配套 Appx。
- 原生 Cleanup 不依赖样本或配置完好，避免它们损坏时阻止恢复。保留实验目录、日志与旧发布包，不自动删除。
- 若清理失败，保留独立任务；不把“任务已退出”当作清理成功。

不改执行策略、服务配置、浏览器或网络策略；不写 CodeIntegrity/WDAC，不触发该机制的系统重启。
策略应用本身、日志和系统内部缓存会产生来宾状态变化；不能宣称字节级不变。

## 在来宾运行

1. 保留当前关机基线快照。本包记录你提供的名字，不会自动证明快照可恢复，也不会操作 VMware 快照。
2. 将 `dist/AICT-applocker-enforce-smoke-v1.zip` 复制到来宾桌面，**解压到一个新目录**。
3. 在新目录打开 **管理员、64 位 Windows PowerShell 5.1**，执行：

```powershell
.\scripts\Invoke-AppLockerEnforceSmoke.ps1 -SnapshotReference 'AICT-phase0-baseline'
```

通常几十秒，父脚本最多等待约两分钟。不需要手动点击样本，窗口中会给出结果和证据目录。
将整份 `applocker-enforce-时间` 文件夹复制回主机项目 `reports`，把路径发回来。
**只运行这一个新包，不再运行旧的审计、启动诊断或收尾包。**

如报 `Experiment incomplete`，保留报告，先不要重复运行或删除 ProgramData 目录；至少等待三分钟，让独立清理任务执行。
保留的清理任务名称、受保护目录及 RunId 均写在 `runner-result.json` 中。
需要手动触发时，在来宾管理员 PowerShell 中使用报告中的**完整清理任务名**执行：

```powershell
# 将下方名称替换为本轮报告中的真实 Cleanup 任务名，不输入尖括号占位符。
Start-ScheduledTask -TaskName 'AICT-Enforce-Cleanup-本轮32位RunId'
```

该任务只撤销其所属实验分组。仍失败时从 VMware 控制台恢复基线快照；不要删除系统策略根节点或全局恢复安全设置。
独立任务稍后产生的 `cleanup-result-时间.json` 留在本轮受保护目录，可另行取回；首次报告不会冒称已收集未来结果。

## 证据与尚未证明的部分

报告保存逐阶段启动结果、非提权身份断言、首次观察阻断时间、策略读回、清理结果、任务撤销状态、版本与文件哈希。
只导出命中本轮样本路径的有限 AppLocker 日志，不导出其他应用记录；日志不可用不会替代启动证据。
配置中的用户 SID 不复制到交付报告；报告目录只在本地保存，不上传。

主机可用以下只读检查器复核返回证据，不修改原始报告：

```powershell
python .\scripts\verify_enforce_smoke.py .\reports\applocker-enforce-实际时间
```

- `smoke_pass` 仅指本次目录实验的 36 次正式观察与清理；不得自动授予完整阶段 0 通过。
- **目录外相同文件仍允许执行是本实验的预期边界。** 发布者/Authenticode 哈希识别、防复制绕过仍未实现，不授予 identity 验收通过。
- 文件的分发 SHA-256 用于包完整性，不等于 AppLocker 使用的 Authenticode 哈希，不能混用。
- 记录一次启用的观察时间，用于尽早发现明显延迟；尚未完成至少 20 次切换，不授予两秒时延验收通过。
- 既有策略共存、独立标准用户、服务权限、系统应用兼容、重启持久化和长期故障恢复仍需后续验收。
- 此处临时任务不是正式后台服务，也没有实施卸载保护或强制锁定。

## 已完成的开发机检查

已编译两个 x64 .NET Framework 实验程序，17 项原生离线策略/判定断言通过，无害样本标记通过；
三种原生模式及启动脚本均在物理主机拒绝执行；Windows PowerShell 5.1 语法通过。
新增 11 项证据检查回归覆盖错误拒绝码、缺失标记、重复样本、清理残留、配套规则缺失、哈希篡改和时钟不一致；
加上既有检查，目前 31 项 Python 测试通过。合成证据仅用于检查器测试，不作为来宾实测结果。
**来宾实际 Enforce 尚未执行。**

参考：[规则集合与打包应用要求](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/applocker/working-with-applocker-rules)、
[拒绝优先和例外](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/applocker/understanding-applocker-rule-behavior)、
[AppLocker CSP XML 与作用域](https://learn.microsoft.com/en-us/windows/client-management/mdm/applocker-csp)、
[StoreApps WMI Bridge 类](https://learn.microsoft.com/en-us/windows/win32/dmwmibridgeprov/mdm-applocker-applicationlaunchrestrictions01-storeapps03)、
[Authenticode 哈希条件](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/applocker/understanding-the-file-hash-rule-condition-in-applocker)。
