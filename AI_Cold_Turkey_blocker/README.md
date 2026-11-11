# AI Cold Turkey Blocker

**内置浏览器人工观看：独立验证中。** `dist/AICT-manual-view-lab-v1/内置浏览器验证.exe` 只读识别前台网页，提供60秒屏幕遮挡与本地AI点击测试页。请求截图排除和点击穿透，不关闭网页、不修改ChatGPT设置、不接正式规则/额度；真实客户端识别与AI截图/点击仍待用户验证。见 [验证说明](docs/MANUAL_VIEWING_LAB_V1.md)。

**开始菜单图标补丁：** `dist/AICT-start-menu-icon-v1/添加开始菜单图标.exe` 为当前用户开始菜单入口设置与托盘一致的时钟图标，不创建桌面入口，不修改后台。后续构建内嵌多尺寸图标，新安装默认开始菜单入口。见 [开始菜单图标](docs/START_MENU_ICON.md)。

**最新更新 v13：修复正常网站偶发网络拒绝。** 已有v12安装使用 `dist/AICT-network-v13/更新网络保护.exe`。网络就绪与前台统计分离，短暂不同步有有界缓冲，插件桥接卡住可重连；增加分浏览器实际网络状态、只在自有网络限制解除后重试正常错误页一次。更新后重载浏览器组件到0.5.0，保留规则、额度与托盘功能。详见 [网络保护 v13](docs/NETWORK_V13.md)。

**最新更新 v12：托盘与诊断修正。** 已有v11安装使用 `dist/AICT-tray-v12/更新托盘与诊断.exe`。叉号隐藏到托盘，双击图标/桌面入口恢复，托盘右键退出界面；后台限制继续运行。新增单实例唤回，修正空应用列表导致的网站误扣时，将诊断序列化也移到后台。浏览器0.4.0不变，现有规则与用量保留。说明见 [托盘与诊断 v12](docs/TRAY_V12.md)。

**最新更新 v11：采集失联按时间扣额度。** 已有v10安装使用 `dist/AICT-telemetry-v11/更新采集与额度.exe`，保持持续运行。短暂失联不再锁死整个时段，界面/导出区分实测用量与失联扣时；旧缺口保留用量并标注估算。应用身份校验移出心跳循环，增加采集、IPC、服务/存储与进程状态诊断，继续凭新日志解决实际失联。浏览器0.4.0不变。说明见 [采集与额度 v11](docs/TELEMETRY_V11.md)。

**最新更新 v10：持续使用。** 已有v8/v9安装使用 `dist/AICT-continuous-use-v10/更新为持续使用.exe`，取消统一30分钟到期，按你自己的日程、额度及延迟修改长期执行。规则、用量和待生效内容保留；已停用的规则不自动开启。桌面入口与主窗口改为“专注计划”，独立管理员恢复保留。新安装默认持续使用，短测仅为可选项。说明见 [持续使用 v10](docs/CONTINUOUS_USE_V10.md)。下方短时测试描述是历史记录。

**最新更新 v9：减少网站加载闪屏。** 已有v8安装使用 `dist/AICT-smooth-navigation-v9/更新网页加载优化.exe`，再重新加载浏览器组件至0.4.0。无关网址和通过所有规则共同确认的例外直接加载，复杂组合保留确认页；快速放行隐藏卡片，支持深浅色。规则与数据保留。22项组件测试、Edge及Chrome各20项实测通过，其中各5项记录确认无插件中转。详见 [网页加载优化 v9](docs/SMOOTH_NAVIGATION_V9.md)。

**最新更新 v8：封锁原因。** 现有v7安装使用 `dist/AICT-block-reasons-v8/更新封锁原因.exe`，随后重新加载浏览器组件到0.3.0。网站封锁页显示真正命中的规则名称、时间段/循环/倒计时/额度等原因和相关截止时间；软件主窗口可查看当前触发条件。多个条件分别列出，解除后清理旧原因，失联显示未确认。说明见 [封锁原因 v8](docs/BLOCK_REASONS_V8.md)，本地回执 `reports/block-reasons-v8-local-checks-20261103.json`。源码已同步用户指定私有仓库，日志、数据库、安装包与其他项目不上传。

**最新更新 v7：每个时段各自限额。** 已有v6主机安装使用 `dist/AICT-multi-quota-v7/更新分时段额度.exe`。同一规则组可逐行设置多个每天重复的时段，各行额度、用量、缺口与重置独立；上午20分钟不占用晚上40分钟。旧每日和单时段行为保留，跨午夜不在零点重置，修改或合并时段保留已有用量。浏览器组件不变。说明见 [每段独立额度 v7](docs/MULTI_QUOTA_V7.md)，回执 `reports/multi-quota-v7-local-checks-20261103.json`：151项核心测试及界面自测通过，本轮未应用到用户安装。

**最新更新 v6：指定时间段的共享额度。** 已有 v5 主机安装可使用 `dist/AICT-window-quota-v6/更新时段额度.exe`；保留原每日额度行为，新增每天指定时段内的共享分钟数，支持跨午夜、周期开始日期记账、时段外不扣这份额度。编辑时段不重置已有用量；硬封锁仍独立生效。浏览器组件不变。说明见 [时段额度 v6](docs/WINDOW_QUOTA_V6.md)，验证回执 `reports/window-quota-v6-local-checks-20261103.json`：137项核心测试与界面自测通过。本轮未应用到用户安装。

**最新更新 v5：网站例外备注与分组。** 已有 v4 主机安装使用 `dist/AICT-exception-organizer-v5/更新备注与分组.exe`；旧例外保留，支持用途备注、分组、搜索、批量添加和批量移动。纯备注修改立即保存并保留待生效申请的原截止时间；网址范围变化仍遵循原延迟规则。浏览器组件不变。新规则不再默认启用测试循环。说明见 [例外整理 v5](docs/EXCEPTION_ORGANIZER_V5.md)，验证回执 `reports/exception-organizer-v5-local-checks-20261103.json`；本轮没有在用户安装中应用更新。

**最新功能：网址路径、`*` 通配符及页面例外。** 已有主机安装使用 `dist/AICT-host-url-update-v4/更新网址功能.exe` 原地更新，再重新加载浏览器组件至 0.2.0。保留数据库；网站草稿在新版恢复后需重新核对时间条件并提交。说明见 [网址规则 v4](docs/URL_PATTERNS_V4.md)，验证回执为 `reports/url-patterns-v4-local-checks-20261103.json`：121项核心测试、11项组件测试、Edge及Chrome各10项实际浏览器检查通过；主机更新尚待用户操作。下方版本记录按历史保留。

**当前：主机短时测试版（用户2026-11-03明确允许主机迭代）。** 使用 `dist/AICT-host-trial-v1/专注计划.exe`，也提供同名ZIP。
初始为空规则，不迁移VM配置；首轮30分钟，到期保留并停用规则，另有独立恢复工具与安全任务，暂不开放应用白名单。安装后桌面入口是“专注计划（主机测试）”。
已修正空网站黑名单仍要求保护、DNR空域名规则、WFP用户条件类型和原封锁页状态；114项核心测试、9项浏览器规则/worker测试，以及描述符/策略结构/任务绑定检查通过。
主机实际安装和执行由用户启动验证，不再要求回虚拟机验证这些功能。使用说明 `docs/HOST_TRIAL.md`，本地回执 `reports/host-trial-v1-local-checks-20261103.json`。
下文旧VM发布记录和“主机尚未开放”的历史描述仅保留追溯，以本段新范围为准。

**当前更新器：v2.1。** 回传确认旧 v2 更新器在 Windows PowerShell 5.1 中发生空备份路径异常，恢复又被未改动的采集 DLL 占用阻断；旧 v2 更新包暂停推荐。
使用 `dist/AICT-beta-rules-update-v2.1.zip`，解压到新文件夹，双击“更新专注计划.exe”，保留现有规则/用量并继续更新。
规则程序与冻结 v2 完全相同，仅修正更新器。实际 WinPS5.1 下的8项创建/替换/锁占用/恢复/数据保持测试通过；服务任务使用测试替代对象，实际VM更新/恢复待回传。
详情 `docs/RULES_UPDATE_V2_1.md`，实测与失败回执分别为 `reports/rules-update-v2.1-local-checks-20261103.json`、`reports/rules-update-v2-failure-audit-20261103.json`。

**规则交互更新 v2（2026-11-03用户修订）：** 只收紧或中性修改立即生效；包含放宽才等待，期间旧规则继续执行，到期自动换新规则，不再额外等待旧封锁窗口。
界面分“正在生效 / 待生效”，提交后显示新规则；同内容重交保留时间，不同内容重交重新等待。
已安装会话使用 `dist/AICT-beta-rules-update-v2.zip`，关闭原主窗口后双击“更新专注计划.exe”，保留规则/额度/待生效内容，无需重装或重载扩展。
规则语义及更新说明见 `docs/RULES_UPDATE_V2.md`。108项规则回归与界面检查通过，实际VM更新与新时序效果待确认。
回传 `aict-beta-20261103T100452724Z` 的10个文件哈希通过，记录4次应用拒绝与12次网站拒绝；不从这些日志授予所有防绕过用例通过。回执 `reports/integrated-v1-audit-20261103T100452724Z.json`。

**当前优先级（2026-11-03）：** 先完成抵抗退出、结束进程、移除插件和软件内解锁的自用版。
整机重启实验包的准备已停止，尚未发布；完整故障/多版本矩阵后移。范围见 `docs/SELF_USE_MVP_SCOPE.md`。

**最新统一入口：** `dist/AICT-self-use-beta-v1.zip` 完整复制到 VMware 来宾解压，双击根目录“专注计划.exe”安装，之后从桌面“专注计划”打开。
集成持久后台、应用/自定义网站、黑白名单、日程/循环/倒计时、额度、冻结修改、使用统计和图形导出/正常卸载。
浏览器首次通过图形引导加载本地组件一次；不要求命令行、重复旧实验或恢复快照。
使用说明 `docs/INTEGRATED_BETA.md`，集中测试表 `docs/BETA_TEST_CHECKLIST.md`。
本机自动检查通过，新版 Windows 执行路径仍待来宾实测；正式商店部署和物理主机安装未开放。

**v1 安装缺陷（已回传确认）：** 安装遗漏创建原生桥检查的 `probe` 运行目录，导致迁移时失败，并非一直等待。此版新安装暂勿继续推荐。
针对已回传的会话 `111e0fe3cfa348b4a055eafb99e25f06`，使用 `dist/AICT-beta-setup-repair-v1.zip`，双击“修复并继续安装.exe”即可补齐目录并续接原安装；不修改原包、不清空规则、不需快照恢复。
安装源码已修正，在迁移/服务操作前创建并验证运行目录；构建加入原生目录复现/修复检查。修复在实际来宾的完成结果仍待回传，记录见 `reports/beta-setup-repair-v1-local-checks-20261103.json`。

旧 `AICT-desktop-browser-v1` 固定域名实验已停止推荐：回传显示原应用服务已过实验清理期限，网站策略写入未成功。
原包与证据保留，不把网络错误当作网站封锁通过。新版改由特权后台处理受管理用户的浏览器配置，并以组件与 WFP 联动执行网站规则。

Windows 11 强制封锁软件。已有集成自用测试版与既有家庭版隔离实测，完整平台验收尚未完成。

**尚未交付可安装在主机的自用产品。** 旧桌面预览不会封锁；新 `AICT-interactive-lab-v1` 仅在 VMware 来宾启用真实应用限制，来宾行为待验证。详见 `docs/INTERACTIVE_LAB.md`。

批准的阶段顺序：隔离验证 → 规则引擎 → 应用封锁 → 网站封锁 → 加固试运行。
不用轮询杀进程代替 AppLocker 启动阻断，不在主机上试验封锁。首版范围按用户调整，不再等待完整旧阶段0矩阵。

## 旧版：虚拟机应用联调（保留追溯）

`dist/AICT-interactive-lab-v1.zip`：选择EXE、时段/循环、锁定和延迟修改已连接Windows服务与真实AppLocker读回；另处理已经运行的选中程序。
安装先用自带无害样本自动检查，再打开普通权限界面。本轮无需重启或重复恢复快照，测试结束可独立恢复。
网站/插件兜底、真实额度、白名单和主机安装未交付。运行、收集和恢复命令见 `docs/INTERACTIVE_LAB.md`。

## 打开桌面开发预览

已构建 `dist/AICT-desktop-preview-v1`（自带运行时），也提供同名ZIP与SHA256。
在 `D:\Projects\AI_Time_Control` 运行，或直接双击包内“打开开发预览.cmd”：

```powershell
.\AI_Cold_Turkey_blocker\dist\AICT-desktop-preview-v1\App\Aict.Desktop.exe
# 完全退出普通后台预览（配置保留）：
.\AI_Cold_Turkey_blocker\dist\AICT-desktop-preview-v1\Stop-Preview.ps1
```

界面关闭后普通后台仍运行；退出脚本只停止路径和启动时间均匹配的本包预览进程。
当前61项.NET自动测试、47项既有Python回归、11项真实管道观察与本程序界面渲染检查通过。
验证记录见 `reports/desktop-preview-validation-20261102.json`。产物不包含测试数据库，首次打开仍为空白配置。
预览未接入真实采集、AppLocker/WFP或正式服务，不能用于实际自控；阶段0/1整体仍未通过。

## 当前交付

- `scripts/Inspect-Environment.ps1`：普通权限运行的只读检测；只有指定 `-OutputPath` 才保存报告。
- `scripts/Collect-GuestEvidence.ps1`：仅在 VMware 来宾运行的只读检测和本地证据收集。
- `dist/AICT-guest-preflight-v2.zip` 与 `docs/GUEST_HANDOFF.md`：修正版来宾只读检测包与说明；不接收登录密码。旧 v1 包保留追溯，不再使用。
- `scripts/verify_guest_evidence.py`：验证传回来宾报告的来源哈希，原始文件不变，不授予封锁用例通过状态。
- `dist/AICT-wfp-smoke-v2.zip` 与 `docs/WFP_SMOKE.md`：补充 UDP 接收端证据和重复对照的短时阻断实验；旧 v1 结果保留，不重写成通过。仅在 VMware 来宾手动运行。
- `dist/AICT-applocker-csp-audit-v1.zip` 与 `docs/APPLOCKER_CSP_AUDIT.md`：下一轮 AppLocker 管理接口的 AuditOnly 读写/删除实验；使用临时 SYSTEM 任务，不请求应用封锁。
- `dist/AICT-csp-startup-diagnostic-v1.zip` 与 `docs/CSP_STARTUP_DIAGNOSTICS.md`：审计任务退出 1 后的启动诊断；只运行离线 Validate，不重跑 Audit，也不改执行策略。原审计包暂勿重跑。
- `dist/AICT-applocker-native-audit-v1.zip` 与 `docs/APPLOCKER_NATIVE_AUDIT.md`：SYSTEM 脚本加载被拒绝后的 C# / Windows MI 实验版本；无 SYSTEM PowerShell，不修改执行策略，先核对旧分组再做 AuditOnly 读写。
- `dist/AICT-csp-native-closeout-v1.zip` 与 `docs/CSP_NATIVE_CLOSEOUT.md`：原生创建接口返回 Failed 后的收尾；修复 UTF-8 解析，只撤销已知实验任务并读取限定日志，不重复写入策略。旧审计包暂停使用。
- `dist/AICT-applocker-native-audit-v2.zip`：家庭版 AuditOnly 创建、读回和删除已通过，相关任务已清理；仍不代表真实启动阻断。
- `dist/AICT-applocker-enforce-smoke-v1.zip` 与 `docs/APPLOCKER_ENFORCE_SMOKE.md`：家庭版真实目录范围启动阻断、改名、例外、运行中样本和恢复已通过，36 次正式观察及策略/任务清理均核验。日志导出解析错误已在后续源代码修复，原包与报告保留；不为补日志重跑成功批次。
- `scripts/verify_enforce_smoke.py`：只读复核该批来宾启动证据与哈希，不修改源报告。
- `dist/AICT-applocker-identity-latency-v1.zip` 与 `docs/APPLOCKER_IDENTITY_LATENCY.md`：家庭版 172 次正式启动及20次切换通过；同内容原名/改名/目录外复制均拒绝，独立内容对照允许。最慢启用0.2417321秒、恢复0.3123469秒；策略/任务清理完成。不外推为完整日程/启动/负载指标。
- `scripts/verify_identity_latency.py`：分别复核身份阻断和两秒切换目标，时延未达标不会抹去成功的身份观察。
- `dist/AICT-service-boot-lab-v1.zip` 与 `docs/SERVICE_BOOT_LAB.md`：服务自身权限、受控崩溃恢复、正常重启自动启动和测试状态持久化已通过，服务与清理任务移除。未应用封锁策略，实际规则/额度的故障及重启验收仍未通过。无需重跑。
- `scripts/verify_service_lab.py`：只读复核服务实验，不将测试计数持久化当作真实额度或封锁跨重启证明。
- `src/Aict.Core`、`src/Aict.Storage` 与 `docs/CORE_ENGINE.md`：首版纯规则/共享额度/冻结修改与SQLite快照，49项.NET自动测试通过。只生成模拟和存储数据，尚未接入实际执行层。
- `scripts/Test-Core.ps1`：使用项目内的.NET SDK运行测试和组合锁预览；不安装服务或封锁主机。开发工具位于忽略的`.tools`，没有全局SDK安装或PATH修改。
- `src/Aict.Backend`、`src/Aict.Contracts`、`src/Aict.Desktop`：普通进程后台、仅当前账户的有界命名管道、中文WPF规则编辑、冻结修改预览和导出。持久展示“未执行系统封锁”，真实额度未知。不是正式服务或防绕过实现。
- `scripts/Build-Preview.ps1 -SelfTest` 与 `docs/DESKTOP_PREVIEW.md`：构建自带运行时的桌面预览，验证真实管道、后台进程重开和本程序窗口渲染；测试数据与日常预览数据分开。阶段0门槛不变。
- `src/Aict.Enforcement`、`src/Aict.Service` 与 `docs/SERVICE_POLICY_LAB.md`：规则计划、真实LocalSystem服务候选及家庭版MI桥，VM内固定测试程序联调已通过；尚未连接桌面预览或开放任意目标。普通用户管道仅查询，不接收时间/额度/路径/停止请求。
- `dist/AICT-service-policy-lab-v1.zip`：家庭版服务/AppLocker/SQLite合并联调已通过，60次正式启动、18项权限观察与清理核验完成；服务中断仍拒绝，恢复保留冻结修改。单次调度阻断上界1.0064008秒；无需重跑，尚无真实Windows重启/额度证明。回执 `reports/service-policy-v1-audit-20261103T014001416Z.json`。
- `scripts/verify_service_policy_lab.py`：只读检查新包证据，分别记录功能和两秒观察目标；没有实际重启/真实额度数据时不授予相关通过。当前总计69项.NET测试、56项Python回归通过；这些不是来宾Windows行为实测。
- `scripts/check_phase0.py`：检查家庭版、专业版隔离验收证据；缺失项不会被算作通过。
- `tests/test_phase0_gate.py`：验收门槛检查器的离线回归测试，不代表 Windows 封锁测试。
- `docs/PHASE0_LAB.md`：隔离环境准备、实测步骤、证据要求与恢复要求。
- `docs/ARCHITECTURE.md`：实现边界和阶段衔接。
- `reports/phase0-results.json`：实际验收状态。未执行项目保持 `NOT_RUN`。
- `reports/PHASE0_REPORT.md`：本机检测结论及下一步。

## 安全运行检测

在本目录打开 **64 位 Windows PowerShell**，不需要管理员权限：

```powershell
.\scripts\Inspect-Environment.ps1
# 可选：只向指定文件写入报告；已有文件不会覆盖。
.\scripts\Inspect-Environment.ps1 -OutputPath .\reports\my-environment.json
```

检测脚本不启动/停止服务、不安装组件、不修改注册表、不添加网络过滤、不上传信息。
检测到虚拟化组件或 DLL 只表示组件存在，不表示封锁可用，也不证明存在隔离虚拟机。
报告不包含账户名、SID、浏览历史、已有策略内容或设备序列号。

检查阶段门槛与运行离线测试（使用现有 Python，无第三方依赖）：

```powershell
python .\scripts\check_phase0.py .\reports\phase0-results.json
python -m unittest discover -s tests -v
```

门槛检查退出码：`0` 所需证据齐全且全部通过；`1` 数据无效或有失败；`2` 尚未完成。
门槛检查器只核对本地证据完整性，不会自动运行实验，也不能独立证明人工记录真实。

## 尚未完成

新版服务、系统执行、扩展与图形安装/卸载已实现并打包；完整 Windows VM 集成验收、正式扩展商店发布、升级与故障矩阵尚未完成。
不提供主机一键封锁或强制安装入口。主机安装和首次强制锁定另行确认。
# 当前网址能力更新

网址路径、`*` 通配符和页面例外已实现，使用说明见 [网址规则 v4](docs/URL_PATTERNS_V4.md)。主机原地更新入口为 `dist/AICT-host-url-update-v4/更新网址功能.exe`，保留原数据库；需要用户完成更新及浏览器组件重新加载。当前已完成独立 Edge/Chrome 测试，真实站点验收尚待更新后进行。

