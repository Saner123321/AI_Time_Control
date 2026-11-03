# 原生 CSP 创建失败后的任务收尾与现有日志读取

本轮 `csp-native-audit-20261102T073848494Z` 已提供真实 SYSTEM 结果：MI 读取成功、基线实例数 0，
`CreateInstance` 返回通用 `Failed`；新旧两个分组均确认不存在，前后 SrpV2 键均不存在，未启用 Enforce。
尚不能凭通用错误判断是版本、CSP 分组创建方式还是实现问题。

启动器另有编码问题：Windows PowerShell 5.1 默认读取无 BOM UTF-8 中文 JSON 会解析失败。
已在同一份原始报告复现，改为显式 UTF-8 后正确读取。原始报告、错误与失败结果均保持不变。

## 本包只做收尾，不重复写策略

- 绑定本次 native RunId、旧 RunId、已收到的结果 SHA-256 和冻结的 v1 EXE/脚本哈希。
- 在来宾读取原受保护目录内的结果，明确按 UTF-8 解码，确认 SYSTEM、两个分组不存在、创建未成功且未启用强制。
- 检查两轮审计任务没有正在运行，再核对所有者、任务动作、参数和 native 命令文件内容。
- 仅撤销这两轮的四项已知实验任务；已不存在则记录，不碰其他任务。任务定义变化就保留并报告。
- 不新建 SYSTEM 任务，不运行任何 worker，不调用 CSP 读写，不修改执行策略，不删除实验目录或 VM 文件。
- 只读取原失败时间前两分钟至后三分钟的现有 DeviceManagement Admin 日志，最多扫描 256 条，仅保留含本次随机 RunId 的事件数据。
- 不开启任何事件日志或审计设置。零条匹配/日志不可读不等于“没有错误”，会记录扫描范围及读错误。

结果中的“分组不存在”来源于之前已核验的 SYSTEM 结果，并非本脚本重新查询 CSP。
如果报告内容、哈希、权限或任务定义有变化，脚本停止收尾，不猜测或覆盖。

## 在虚拟机运行

将 `dist/AICT-csp-native-closeout-v1.zip` 解压到来宾新目录，在管理员 Windows PowerShell 5.1 中运行：

```powershell
.\scripts\Close-CspNativeExperiment.ps1
```

把生成的 `csp-closeout-时间` 文件夹复制回主机项目 `reports`，告知路径。
无需重跑原审计包，无需重建或恢复快照。原实验文件继续保留。
正常收尾结果为 `OWN_TASK_CLOSEOUT_COMPLETE`；它不表示 AppLocker 管理入口或整个阶段通过。

本机已通过：中文 UTF-8 JSON 回归、六项模拟任务保护测试、脚本语法与主机拒绝检查。
模拟测试不操作真实计划任务；本机没有修改任务、CSP 或 Windows 安全设置。
