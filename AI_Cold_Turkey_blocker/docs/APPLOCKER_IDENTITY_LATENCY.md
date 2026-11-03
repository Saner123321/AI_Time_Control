# 文件身份与 20 次切换：阶段 0 合并实验

## 当前结果

回传 `identity-latency-20261102T112355726Z` 已验证：172次正式观察及20轮启用/撤销全部通过，
最慢首次拒绝0.2417321秒、最慢恢复0.3123469秒；源二进制匹配冻结包，策略和三个任务均清理。
日志导出正常，限定路径事件100条，其中拒绝80条、允许20条；日志不保证逐次记录每个允许调用，主要证据仍为完整启动记录。
Home 的文件身份和底层切换时延用例已记 PASS；不等于产品日程、发布者识别、重启或完整阶段通过。
无需重跑本包，后续命令包含解压文件夹名称。以下保留原实验范围。

前一批 `applocker-enforce-20261102T093118262Z` 已通过：36 次目录范围启动观察、例外、撤销恢复与三个任务清理均符合预期。
这批针对剩下的两个问题：相同程序复制出目录是否仍被拒绝，以及反复启用/撤销时的实际切换时间。
**尚未在来宾执行本批；不能把本机编译或只读哈希计算当作 AppLocker 匹配通过。**

## 运行方式

仅在已有恢复快照、已登录的 VMware Windows 来宾内运行。保留当前 `AICT-phase0-baseline` 快照。
将 `dist/AICT-applocker-identity-latency-v1.zip` 复制到来宾，完整解压到一个**新目录**。
在该目录打开**管理员、64 位 Windows PowerShell 5.1**，只执行一次：

```powershell
.\scripts\Invoke-AppLockerIdentityLatency.ps1 -SnapshotReference 'AICT-phase0-baseline'
```

会自动执行所有切换和样本启动，无需重复运行 20 次命令。预计一至数分钟，父脚本最多等待约八分钟。
结束后，将完整 `identity-latency-时间` 目录复制回主机项目的 `reports`，发送路径。
不要重新运行已经通过的旧审计包或目录封锁包。

## 具体检查

- 固定目标是本包单独编译的无害 EXE；其原名、目录内改名和目录外副本都应在规则期间收到 Windows 启动拒绝码 1260。
- 一个另行编译、内容不同的无害程序作为对照，整个实验期间都应退出 0 并返回专属标记。
- 基线为四个样本各三次，共 12 次正式观察。
- 随后 20 轮，每轮启用后检查四份样本、撤销后再检查四份，共 160 次正式观察；总计 **172 次**，另记录传播等待尝试。
- 启动均由当前账户的交互 **Limited 非提权令牌**执行；不建立新账户，不获取密码，不把 SYSTEM 启动当作普通权限证明。
- 用同一台来宾的单调计数器，从 EXE 策略创建调用前计至首次实际拒绝；从删除调用前计至首次实际恢复。
- 分别报告 20 个启用和 20 个撤销样本的最大值，不用平均值掩盖超时。初始目标为两者均不超过 2 秒。
- 功能成功但某次时延超过目标时，保留身份阻断的证据，将时延记录为未达标；不会把已运行的事实全部丢弃。

## 文件身份和约束

使用 Windows 文档中的 PE SIP 接口 `CryptSIPRetrieveSubjectGuid`、`CryptSIPCreateIndirectData` 计算 SHA-256 Authenticode 身份，
生成 `FileHashRule`，使用 `SHA256` 类型。**分发包的普通 SHA-256 仅用于文件完整性，不混作 AppLocker Authenticode 身份。**

构建时将目标身份、目标/对照分发哈希和长度嵌入工作程序；运行前核对全部样本及本轮工作程序，确保工作程序和对照未命中目标身份。
清理模式不依赖可损坏的样本或配置文件，只接受精确分组与嵌入的固定策略正文。
目标文件的哈希规则按内容生效，因此本轮相同目标的其他副本也会暂时受限；不会选择任何系统或真实工作程序。

本机已验证 x64 结构布局、读取不修改源文件、中文/空格/`&` 路径、改名与复制身份稳定、不同内容身份不同，
以及 PE 校验和字段变化不改变 Authenticode 身份、却会改变分发 SHA-256。
这些是计算/接口契约检查；**只有本批来宾启动结果才能确认生成的身份被 AppLocker 正确识别。**

这不覆盖已签名应用的发布者识别、应用更新后的哈希维护、脚本解释器、DLL 或 Store 应用的目标阻断。
也不承诺抵抗管理员、修改二进制后的所有变种或未知程序；白名单与系统必需项仍有独立验收。

## 隔离、清理和故障

- 原生三模式和父脚本在物理主机拒绝运行；主机只编译、执行无害样本及只读 SIP 计算。
- 检测到既有 EXE/StoreApps CSP 实例或本地 SrpV2 策略键时拒绝实验，不合并、覆盖别人的规则。
- 唯一 GUID 分组，先创建并验证允许全部已签名打包应用的 Appx 配套规则，再逐次创建 EXE 哈希拒绝规则。
- 每轮删除本轮 EXE 实例，最后核对并删除自己的 EXE 和 Appx；从不删除策略根节点。
- 预先注册独立 SYSTEM 清理任务：**实验开始十分钟后或下次启动时**运行。工作/探针任务各限时八分钟。
- 正常结束且确认本轮策略不存在，才撤销独立清理任务；失败时保留任务和受保护目录。
- 根目录、样本、配置、命令文件均归管理员组；普通探针只可写自己的观察子目录。
- 不设置执行策略、不改服务配置、不写网络/浏览器策略，不执行真实应用的强制结束或卸载保护。

如出现失败，不要重复运行、删除受保护目录或修改全局安全设置。先保留报告，等待独立清理任务执行。
任务名与目录写在 `runner-result.json`。若需立即触发，在来宾管理员 PowerShell 使用该报告中的真实完整任务名：

```powershell
Start-ScheduledTask -TaskName 'AICT-Identity-Cleanup-本轮32位RunId'
```

这是占位示例，实际名称应复制报告值。稍后产生的清理 JSON 留在相同受保护目录，可另行取回；首次报告不会包含未来结果。
若仍无法恢复，使用 VMware 控制台恢复基线快照。本包记录快照名字，仍不冒称自动验证过快照恢复。

## 证据

报告包含版本、源二进制哈希、所有正式启动记录、每轮传播尝试、单调时钟、策略读回、独立清理和任务撤销状态。
AppLocker 日志只导出命中本轮生成路径的允许字段；新版同时识别 `EventData` 与 `UserData`，跳过无文件事件。
前一批的事件导出错误已在源代码修复，并用八个离线 XML 案例检查；不为补日志要求重跑成功批次。
用户 SID 配置不复制到报告；不导出别的应用日志或浏览数据。

在主机只读复核：

```powershell
python .\scripts\verify_identity_latency.py .\reports\identity-latency-实际时间
```

检查器分别输出 `identity_functional_pass` 和 `latency_target_pass`，核对 172 次正式观察、20 轮时钟和源包身份。
最终仍需审查真实来宾证据后更新阶段记录，不自动授予完整阶段 0 通过；服务、重启、浏览器与恢复验收尚未完成。

本机当前检查：16 项原生契约断言、三模式/启动脚本物理主机拒绝、WinPS 5.1 语法、只读 SIP 差分验证、
8 个事件 XML 案例与 39 项 Python 回归通过。合成协议数据只测试检查器，不算 Windows 封锁实测。

参考：[AppLocker 文件哈希](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/applocker/understanding-the-file-hash-rule-condition-in-applocker)、
[CSP 文件哈希 XML 类型](https://learn.microsoft.com/en-us/windows/client-management/mdm/applocker-csp)、
[SIP 创建摘要](https://learn.microsoft.com/en-us/windows/win32/api/mssip/nf-mssip-cryptsipcreateindirectdata)、
[SIP_SUBJECTINFO](https://learn.microsoft.com/en-us/windows/win32/api/mssip/ns-mssip-sip_subjectinfo)。
