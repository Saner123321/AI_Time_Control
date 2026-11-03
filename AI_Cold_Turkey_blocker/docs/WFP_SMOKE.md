# 下一步：短时 WFP 实际阻断实验

这不是产品安装包。仅验证用户态 WFP 管理接口能否在家庭版来宾中阻断指定实验 EXE。
包中使用 .NET Framework 编译的独立实验程序，避免在来宾安装 SDK；产品技术选型仍是 .NET 10。

## 前置条件与运行

1. 用户已经说明将先创建新快照。请在来宾**正常关机、内存为 4 GB** 时创建关机快照。
   不删除原快照，不勾选保存运行内存；记录实际名称。快照存在和恢复已验证仍是不同状态。
2. 重新开机并手动登录来宾，将 `dist/AICT-wfp-smoke-v2.zip` 解压到来宾的新目录，不与 v1 混用。
3. 在来宾打开管理员 Windows PowerShell，进入解压目录，运行：

   ```powershell
   .\scripts\Invoke-WfpSmoke.ps1 -SnapshotReference '你的新关机快照名'
   ```

   需要管理员权限，因为本轮确实添加临时 WFP 过滤；用户自行处理登录和权限提示。
   如果脚本被执行策略拒绝，不改变策略或绕过；保留错误后告知。
4. 把生成的 `lab/wfp-smoke-时间` 文件夹整体复制到主机项目的 `reports` 文件夹，告知目录。

## 精确实验范围

- 程序在物理主机上拒绝进入 `--smoke` 模式。VMware 判断用于防误操作，不是不可绕过的安全边界。
- 为每轮生成独立目录、provider、sublayer 和两条过滤；只匹配自己复制的 `AictProbeTarget.exe` 的完整 WFP 应用身份。
- 使用动态 WFP 会话，不设置 persistent 或 boot-time 标志；会话结束时系统自动清理对象。
- 单个事务提交 IPv4 和 IPv6 ALE_AUTH_CONNECT 过滤。不会关闭防火墙，不改 hosts/DNS，不装服务或驱动。
- 无外部网络依赖：回环地址上绑定随机端口，以一个字节的请求/响应验证 TCP 和 UDP。
- 4 种组合分别检查，每种重复 3 次：目标基线成功 → 目标被阻断 → 对照程序成功 → 清理后目标成功，共 48 次通信。
- 每次记录连接/发送/接收失败位置、接收端实际字节数和响应数、接收线程错误与是否正常退出。
- TCP 预期在连接/发送阶段返回 10013；UDP 允许接收超时，但必须同时满足接收端收到 0 字节、发送 0 个响应、无服务端错误且接收线程正常退出。
- 单独的超时、连接阶段超时、接收端故障、收到请求却未回复、缺失计数都不能计作通过。
- 封锁前读回两条自己的实际规则，核验目标应用身份、提供程序、子层、IPv4/IPv6 层、阻断动作与非持久标志；不核验通过就停止实验并清理。
- 最后用只读会话检查自己的 filter/provider/sublayer 均返回相应 not-found 状态。
- 原始窗口、浏览器和其他用户程序均不在匹配范围内，不关闭任何已有程序。

## 结果与限制

`result.json` 只有在 48 次通信、完整接收端证据、规则读回及清理查询全部满足条件时，才把 `transient_smoke_passed` 设为 true。
UDP 的判断是有重复对照的端到端实验结果，不是 Windows 丢包事件归因；不启用全局网络事件采集，也不修改审核/诊断设置。
v1 的原始 FAIL 结果保持原样：TCP 拒绝与清理均成功，但 UDP 超时缺少服务端证据，不能事后重写成通过。
异常路径也会关闭动态会话；程序进程被结束时，动态会话由 Windows 释放。

以下仍**未测试**：已有连接、QUIC 协议本身、非回环网络、进程故障注入、持久过滤、boot-time 过滤、重启窗口、浏览器集成、AppLocker。
因此即使本轮通过，`phase0_pass_awarded` 也保持 false，不自动授予整个阶段或所有恢复用例通过。

快照名由操作者提供，程序不假装验证了 VMware 恢复操作。本轮不得在恢复基线准备好之前执行。

## 已完成的本机验证

- x64 结构大小和关键偏移检查，与本机 Windows SDK 10.0.19041.0 头文件声明核对。
- 8 次目标/对照的 IPv4、IPv6、TCP、UDP 本地通信全部成功，未添加 WFP 过滤。
- 14 项合成判定回归通过，包括拒绝“只有 UDP 超时”“接收端已收到数据”“接收线程失败”和缺少证据的记录；不是网络封锁通过证据。
- `--smoke` 模式在物理主机上拒绝执行，在调用任何 WFP 写接口前退出。
- 未在主机执行实际封锁；来宾实际封锁结果待用户返回证据。

依据：[FWPM_FILTER0](https://learn.microsoft.com/en-us/windows/win32/api/fwpmtypes/ns-fwpmtypes-fwpm_filter0)、
[FwpmFilterAdd0](https://learn.microsoft.com/en-us/windows/win32/api/fwpmu/nf-fwpmu-fwpmfilteradd0)、
[WFP 会话和对象管理](https://learn.microsoft.com/en-us/windows/win32/fwp/object-management)。
