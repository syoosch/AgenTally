# Stable Packaging

AgenTally 使用 Inno Setup 生成一个面向 Windows x64、当前用户安装的单 EXE。
安装程序是自包含的，目标电脑不需要预先安装 .NET Desktop Runtime。

## 构建

构建正式安装包需要：

- 干净的 Git 工作树；
- `global.json` 指定的 .NET SDK；
- Inno Setup 6 或更高版本；
- 一个明确的三段式版本号；
- 已退出源码构建或其他无法验证身份的 AgenTally 进程。

```powershell
.\scripts\Publish-AgenTallyStablePackage.ps1 -Version 0.2.1
```

构建首先执行公开仓库边界、锁定依赖、NuGet 漏洞、敏感内容和联网边界检查，
然后发布 Stable UI/Core、自包含运行时和当前安装器维护文件。输出位于忽略的
`artifacts/stable-package/`：

```text
AgenTally-0.2.1-win-x64-setup.exe
AgenTally-0.2.1-win-x64-setup.sha256
AgenTally-0.2.1-win-x64-setup.json
```

构建安装包本身不会安装、启动或注册 AgenTally。发布者未签名时，SHA-256
只能验证传输完整性，不能替代代码签名或发布者身份验证。

## 正式发布产物验证

正式安装包必须从已推送且 CI 通过的公开 `main` 精确提交构建。构建完成后，
在同一个干净工作树固定并验证提交与产物身份：

```powershell
$expectedCommit = (git rev-parse HEAD).Trim()
.\scripts\Publish-AgenTallyStablePackage.ps1 -Version 0.2.1
.\scripts\Test-AgenTallyStableReleaseArtifact.ps1 `
    -Version 0.2.1 `
    -ExpectedCommit $expectedCommit
```

验证器会核对官方公开仓库、`main`、干净工作树、精确 Git 提交，以及 JSON
报告中的版本、提交、`releaseCandidate`、自包含形态、文件大小、SHA-256、
签名策略和 Inno Setup 编译器身份。任一项不一致都会失败。

JSON 报告是本地构建证据，不作为 Release 下载文件。实机验收通过后，Release
只上传已验证的正式 EXE 和对应 SHA-256；不得上传带 `UNCOMMITTED` 的本地
验证包。创建 Release 后还应重新下载这两个文件，并与本地已验收产物核对。

## 安装形态

- 默认程序目录：`%LocalAppData%\Programs\AgenTally`；
- Stable 数据：`%LocalAppData%\AgenTally\Stable`；
- 一个开始菜单入口，可选桌面快捷方式；
- 不创建 Windows Service 或计划任务；
- 不默认启用开机自启；
- 不需要管理员权限。

升级复用同一固定应用身份和已记录的安装目录。网络盘、UNC、卷根、重解析点
路径、冲突的安装记录或无法证明所有权的非空目录都会失败关闭。

新版安装器在关闭程序或替换文件之前比较已有安装登记及实际 UI/Core 文件版本；
拒绝低版本覆盖高版本，允许同版本修复和高版本升级。登记缺失仍核对文件版本，
已有版本无法可靠解析时停止安装，保留原有程序与数据。已发布的 v0.1.0 安装包
本身不能追溯加入该保护；保留的数据本身也不能证明已安装程序的版本。

安装器逻辑发生变化的正式版本仍须完成首次安装、自定义目录与运行中升级、
降级拒绝、同版本修复、保留数据卸载及删除数据卸载的实机验收。源码检查、
脚本编译或自动化用例不替代该项验收。

## 升级与卸载安全边界

安装、升级和卸载只检查精确的 AgenTally Stable UI/Core 进程身份，并通过
受限的优雅退出协议等待原 PID 和启动时间退出；不会按进程名批量结束，也不会
强制终止无法验证的进程。

卸载默认保留数据库和设置；静默卸载同样保留数据库和设置。
只有用户明确选择删除数据时，才删除 Stable 自有数据。允许删除的范围只有：

1. Inno 记录的实际程序文件；
2. AgenTally 自有快捷方式和卸载注册；
3. Stable `runtime`、`logs` 和 `temp`；
4. 用户明确选择后才包含 Stable `data`。

所有目标均由固定应用身份、已验证安装目录和固定 Stable 数据根正向构造。
Agent 来源、外部备份、父目录以及任何不属于上述正向所有权集合的路径永远
不能成为清理目标。路径越界、重解析点、锁定文件或未知残留都会中止操作。
