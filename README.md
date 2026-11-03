# AI_Time_Control

Windows 11 自用强制封锁软件，源码位于 [AI_Cold_Turkey_blocker](AI_Cold_Turkey_blocker)。当前实现到 **v7：每个时段独立额度**。

支持应用与网站封锁、每周计划、倒计时和循环、每日或分段额度、网址通配符、例外备注与分组、使用记录及导出。规则收紧可立即生效，放宽按已有等待时间生效；等待期间原规则继续运行。

后台使用 Windows 服务、AppLocker 和用户态 WFP；桌面使用 C# / WPF，浏览器保护采用 Edge / Chrome Manifest V3 扩展。当前是个人测试版，正式扩展商店部署及完整系统故障矩阵仍未完成。主机测试构建保留30分钟测试期限与独立管理员恢复入口，无法彻底抵抗已提权管理员。

## 从源码构建

环境：Windows 11 x64、Windows 自带的64位 Windows PowerShell 5.1 / .NET Framework 编译器、Python 3.11及以上；浏览器组件测试另需 Node.js。SDK由项目脚本下载到本项目 `.tools`，按微软公布的SHA512校验，不修改全局开发环境。

在仓库根目录执行：

```powershell
python .\AI_Cold_Turkey_blocker\scripts\initialize_local_dotnet.py
powershell.exe -NoProfile -File .\AI_Cold_Turkey_blocker\scripts\Test-Core.ps1
powershell.exe -NoProfile -File .\AI_Cold_Turkey_blocker\scripts\Build-Beta.ps1 -HostTrial -SelfTest
node --test .\AI_Cold_Turkey_blocker\tests\extension-rules.test.mjs .\AI_Cold_Turkey_blocker\tests\extension-worker.test.mjs
```

构建结果在 `AI_Cold_Turkey_blocker/lab/build/host-trial`。上述构建和自测不安装服务、不应用封锁策略；真实安装、浏览器加载与首次封锁由用户另行操作。构建所需的小型无害应用夹具已包含在 `build-fixtures`。

## 内容与验证

- [当前功能说明：每段独立额度](AI_Cold_Turkey_blocker/docs/MULTI_QUOTA_V7.md)
- [架构与边界](AI_Cold_Turkey_blocker/docs/ARCHITECTURE.md)
- [例外备注和分组](AI_Cold_Turkey_blocker/docs/EXCEPTION_ORGANIZER_V5.md)
- `src`：规则、存储、服务、会话代理和桌面界面。
- `browser-extension`：浏览器保护组件；`tests`：规则、持久化、升级及组件测试。
- `lab`、`scripts`：历史实验源码、构建、只读诊断和按指定安装范围执行的工具。历史更新脚本依赖原本机报告或冻结包，不作为通用安装入口。

当前本地记录为151项核心测试通过，另有界面自测和更新器文件恢复检查；这些记录不代表所有系统版本与故障场景都已验收。详细历史说明保留在项目文档中。

仓库不包含个人规则数据库、使用记录、诊断报告、虚拟机文件、SDK缓存、完整安装包或构建输出。`taskflow` 与网页资料目录不在本次上传范围内。项目文档中提到的本机日志、历史ZIP和差分更新包不会随源码上传。
