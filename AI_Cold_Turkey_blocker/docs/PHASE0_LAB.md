# 阶段 0 隔离验证手册

当前状态：等待用户手动登录 Windows 来宾，并建立恢复快照。尚未执行任何封锁实验。

## 来宾准备

用户已明确允许直接使用 `D:\program\Windows11_VMware\Windows 11 x64.vmx`。
保留加密和登录密码，不要求用户在聊天中提供密码；登录与 UAC 由用户手动处理。
本机发现加密配置，不代表启动一定需要再次解锁；此前 VMware 页面未显示解锁提示。

1. 用户手动开启并登录虚拟机，确认允许实验的桌面与管理员身份。
2. 第一次系统写入前创建 `AICT-phase0-baseline` 快照，确认可从 VMware 控制台恢复。
3. 来宾运行只读检测，确认实际 EditionID、构建和浏览器版本。VM 名称不是版本证据。
4. 不共享主机个人目录，不将主机物理磁盘挂入来宾；实验文件放在来宾的独立目录。
5. 使用临时标准用户和实验管理员区分权限；不改变主机账户、主机浏览器或主机网络。
6. 使用 .NET 10 SDK。当前主机未装 SDK，不为了阶段 0 更改全局开发环境。
7. 先在现有来宾验证；家庭版与专业版证据必须分别补齐。不能用企业版结果代替家庭版结果。

家庭版主机不支持完整 Hyper-V 角色或 Windows Sandbox；HypervisorPresent 为真不代表拥有该功能。
不通过改系统版本检查或安装非官方系统补丁解决此问题。

## 测试用例与证据

以下均需在家庭版和专业版来宾中执行。全部用例只使用实验样本，不选择真实工作程序。
每项记录 UTC 时间、来宾版本、实际操作、实际结果与本地证据文件 SHA-256。

| ID | 实验与通过条件 |
|---|---|
| baseline-recovery | 记录 AppLocker、服务配置、浏览器策略、WFP 基线；确认快照和管理员恢复通道有效 |
| applocker-deploy | 从受支持入口部署最小规则，只阻断专用测试 EXE；直接启动被拒绝，其他实验程序可运行；管理入口缺失记 BLOCKED |
| applocker-identity | 复制/改名样本仍被发布者或哈希规则命中；不同内容样本不误封 |
| applocker-exceptions | 黑名单例外和白名单的编译结果符合 AppLocker 拒绝优先语义；Windows shell 与实验恢复工具可运行 |
| applocker-existing-policy | 添加无关基线规则后应用/撤回实验规则；无关规则保持原样；并发变更时不覆盖他人更新 |
| applocker-latency | 至少 20 次启用/撤回测量，记录最大值；规则应用至启动阻断最长不超过 2 秒，不以平均值掩盖超时 |
| service-permissions | 普通用户不能停止/重配实验服务或改其目录；界面关闭不解除实验策略；管理员边界明确记录 |
| service-restart | 实验服务意外退出后恢复机制启动；持久规则仍在；故障期间不能称未来日程仍正确切换 |
| boot-persistence | 分别记录正常重启、关机开机、睡眠恢复、来宾强制断电后的规则及启动窗口；快速启动不支持时不能假称通过 |
| browser-policy | Edge/Chrome 黑白名单实际生效；已有浏览器策略未被覆盖；记录动态刷新时延与已打开页面行为 |
| browser-profiles | 分别尝试新配置文件、访客、无痕、参数启动；证明限制生效，不能只看注册表写入成功 |
| browser-extension-loss | 测试扩展失联后兜底阻断不超过 5 秒，恢复需重新验证；开发者扩展结果不冒充商店正式防停用 |
| wfp-dual-stack | 独立 provider/sublayer 的过滤实际阻断目标 EXE 的 IPv4/IPv6、TCP/UDP；其他应用仍可联网 |
| wfp-existing-flow | 已建立 TCP/QUIC 流在规则改变后的行为可重复；boot-time 与 persistent 过滤分别验证，不能用动态 session 代替持久化 |
| cleanup-recovery | 清理仅删除实验拥有的对象；基线策略保持一致，系统应用与网络恢复；注入更新中断后仍可恢复 |

阶段 0 的时延实验针对底层策略应用与失联兜底；完整周计划切换、额度超用、锁定语义属于后续产品阶段验收。

## 证据记录格式

`reports/phase0-results.json` 初始全部为 `NOT_RUN`。状态仅允许：

- `NOT_RUN`：未执行；
- `BLOCKED`：环境、权限或部署入口阻塞；不是失败，也不是不支持；
- `FAIL`：确实执行且未达到通过条件；
- `PASS`：执行达到条件，并具备时间、观察结果与证据。

环境记录需有实际 `edition_id`、`build`、`isolated_vm: true` 和 `snapshot_reference`。
每个 PASS 用例增加 `observed_at_utc`、`observation`、`artifacts` 数组；每个 artifact 含相对报告目录的
`path` 和文件内容的 `sha256`。禁止引用报告目录以外的文件。
`applocker-latency` 和 `browser-extension-loss` 额外填写 `max_observed_seconds`。

检查工具只检查证据的完整性、哈希和门槛，不能验证人工记录的真实性；仍需人工复核实验记录。
不能以手动修改状态或单元测试通过代替真实 Windows 实验。

## 官方参考（2026-11-02 查阅）

- [AppLocker 版本检查变更](https://support.microsoft.com/en-us/servicing/os/windows/2023/02/kb5024351-removal-of-windows-edition-checks-for-applocker)
- [App Control / AppLocker 可用性](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/feature-availability)
- [AppLocker PowerShell 管理入口](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/applocker/use-the-applocker-windows-powershell-cmdlets)
- [Edge 强制扩展限制](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-policies/extensioninstallforcelist)
- [WFP 启动机制](https://learn.microsoft.com/en-us/windows/win32/fwp/basic-operation)
- [Hyper-V 支持版本](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/get-started/install-hyper-v?pivots=windows)
- [Windows Sandbox 限制](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox)
