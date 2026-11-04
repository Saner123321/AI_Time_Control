# AI_Time_Control

Windows 11 自用强制封锁软件，源码位于 [AI_Cold_Turkey_blocker](AI_Cold_Turkey_blocker)。当前实现到 **v12：托盘入口与诊断修正**，关闭窗口保留托盘，重复启动唤回窗口；后台持续执行，修正空应用目标误计费并把诊断序列化移到后台。

支持应用与网站封锁、每周计划、倒计时和循环、每日或分段额度、网址通配符、例外备注与分组、使用记录及导出。规则收紧可立即生效，放宽按已有等待时间生效；等待期间原规则继续运行。

后台使用 Windows 服务、AppLocker 和用户态 WFP；桌面使用 C# / WPF，浏览器保护采用 Edge / Chrome Manifest V3 扩展。当前是个人测试版，正式扩展商店部署及完整系统故障矩阵仍未完成。主机版默认持续使用，30分钟短测仅在新安装时可选，独立管理员恢复入口保留，无法彻底抵抗已提权管理员。

## 从源码构建

环境：Windows 11 x64、Windows 自带的64位 Windows PowerShell 5.1 / .NET Framework 编译器、Python 3.11及以上；浏览器组件测试另需 Node.js。SDK由项目脚本下载到本项目 `.tools`，按微软公布的SHA512校验，不修改全局开发环境。

在仓库根目录执行：

```powershell
python .\AI_Cold_Turkey_blocker\scripts\initialize_local_dotnet.py
powershell.exe -NoProfile -File .\AI_Cold_Turkey_blocker\scripts\Test-Core.ps1
powershell.exe -NoProfile -File .\AI_Cold_Turkey_blocker\scripts\Build-Beta.ps1 -Host -SelfTest
node --test .\AI_Cold_Turkey_blocker\tests\extension-*.test.mjs
powershell.exe -NoProfile -File .\AI_Cold_Turkey_blocker\tests\Test-ContinuousUse.ps1
```

构建结果在 `AI_Cold_Turkey_blocker/lab/build/host-trial`。上述构建和自测不安装服务、不应用封锁策略；真实安装、浏览器加载与首次封锁由用户另行操作。构建所需的小型无害应用夹具已包含在 `build-fixtures`。需要新安装包时，再运行 `python .\AI_Cold_Turkey_blocker\scripts\package_beta.py --host`，输出独立的v12完整包；已有安装使用对应更新包。

## 内容与验证

- [托盘与诊断修正 v12](AI_Cold_Turkey_blocker/docs/TRAY_V12.md)
- [采集与失联扣时 v11](AI_Cold_Turkey_blocker/docs/TELEMETRY_V11.md)
- [持续使用 v10](AI_Cold_Turkey_blocker/docs/CONTINUOUS_USE_V10.md)
- [每段独立额度](AI_Cold_Turkey_blocker/docs/MULTI_QUOTA_V7.md)
- [封锁原因显示](AI_Cold_Turkey_blocker/docs/BLOCK_REASONS_V8.md)
- [架构与边界](AI_Cold_Turkey_blocker/docs/ARCHITECTURE.md)
- [例外备注和分组](AI_Cold_Turkey_blocker/docs/EXCEPTION_ORGANIZER_V5.md)
- `src`：规则、存储、服务、会话代理和桌面界面。
- `browser-extension`：浏览器保护组件；`tests`：规则、持久化、升级及组件测试。
- `lab`、`scripts`：历史实验源码、构建、只读诊断和按指定安装范围执行的工具。历史更新脚本依赖原本机报告或冻结包，不作为通用安装入口。

当前本地记录为177项核心测试、22项组件测试通过，另有界面自测、14项模式/到期任务检查和8项更新器文件恢复检查。网站原因页显示命中的规则、触发条件和相关时段截止时间；这些记录不代表所有系统版本与故障场景都已验收。详细历史说明保留在项目文档中。

仓库不包含个人规则数据库、使用记录、诊断报告、虚拟机文件、SDK缓存、完整安装包或构建输出。`taskflow` 与网页资料目录不在本次上传范围内。项目文档中提到的本机日志、历史ZIP和差分更新包不会随源码上传。
