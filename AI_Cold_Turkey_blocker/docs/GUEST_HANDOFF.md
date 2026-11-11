# 在 VMware 来宾里取得只读检测结果

本文是早期 VMware 可行性验证的历史记录。首次安装专注计划请使用[完整安装说明](INSTALLATION.md)，无需安装 Codex 或查找开发者电脑上的文件。

## 为什么需要这一步

虚拟机已经进入 Windows，但桌面登录不会自动授权 VMware 命令行的 guest operations。
已实际测试的 `vmrun listSnapshots` 返回“此操作需要输入密码”；不尝试提取 VMware 已保存的密码。
窗口自动化未成功打开 VMware 快照菜单；因此目前没有快照成功证据。

后面的命令由使用者在自己的来宾 PowerShell 中手动运行，不依赖开发者本机路径或开发工具缓存。不要向聊天、仓库或问题反馈中提交 Windows 或 VMware 密码。

## 用户操作

1. 已确认 `AICT-phase0-baseline` 快照登记。只读重测不要求新快照；首次实际封锁前，需确认可恢复的基线。
   原快照保存的是 16 GB 内存状态，曾恢复失败。建议先正常关闭来宾，再以当前 4 GB 配置创建一个关机快照；不要覆盖或删除原快照。
2. 将项目下 `dist/AICT-guest-preflight-v2.zip` 拖入来宾的一个**新文件夹**并解压，不与旧包混用。
3. 在**来宾**的 64 位 Windows PowerShell 中运行（解压目录内）：

   ```powershell
   powershell.exe -NoProfile -File .\scripts\Collect-GuestEvidence.ps1
   ```

   不需要管理员权限。若本地执行策略禁止脚本，不绕过策略；告知实际错误后调整交付方式。
4. 把新生成的 `guest-evidence-时间` 文件夹拖回主机项目的 `reports` 文件夹。
   告知已经复制，以及快照的实际名称。

检测包只记录系统版本、管理入口和只读 WFP 打开/关闭结果；不修改 AppLocker、服务或网络。
v2 修复了 PowerShell 5.1 把 `$null` 字符串参数转为空字符串、导致 WFP 错误 50 的问题，并增加 AppLocker WMI 管理类元数据查询。
旧报告保持原样，错误 50 不能被用作 WFP 不支持的证据。类元数据存在也不能证明家庭版支持 CSP 部署。
它会拒绝在非 VMware 机器上运行，但此检查只是防误操作，不是安全边界。
快照是否有效仍需在 VMware 中确认，检测脚本不会伪造快照通过状态。

## 输出及解释

- `environment.json`：来宾系统和组件清点。
- `preflight.json`：AppLocker 管理入口、有效策略内容的哈希与规则数量、WFP 只读会话结果。
- `SHA256SUMS.txt`：两份 JSON 的哈希，用于检查传回后内容一致性。

WFP 返回 0 仅表示会话打开成功；不代表过滤有效。AppLocker 管理入口缺失不等于系统不支持执行策略。
这些文件不含 Windows 密码、VMware 密码、账户 SID、网页历史或已有策略正文。
# 命令路径约定（2026-11-02）

用户要求后续运行命令包含解压文件夹名称。后续交付默认在解压目录的父目录（例如来宾桌面）打开 PowerShell，
给出标准调用形式：`.\AICT-包名-vN\scripts\入口.ps1 ...`，不再只给 `.\scripts\...`。
若脚本分为准备/重启后收尾，两条命令均带文件夹名；说明运行位置，不假定已经进入包内部。

