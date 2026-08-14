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
.\scripts\Publish-AgenTallyStablePackage.ps1 -Version 0.1.0
```

构建首先执行公开仓库边界、锁定依赖、NuGet 漏洞、敏感内容和联网边界检查，
然后发布 Stable UI/Core、自包含运行时和当前安装器维护文件。输出位于忽略的
`artifacts/stable-package/`：

```text
AgenTally-0.1.0-win-x64-setup.exe
AgenTally-0.1.0-win-x64-setup.sha256
AgenTally-0.1.0-win-x64-setup.json
```

构建安装包本身不会安装、启动或注册 AgenTally。发布者未签名时，SHA-256
只能验证传输完整性，不能替代代码签名或发布者身份验证。

## 安装形态

- 默认程序目录：`%LocalAppData%\Programs\AgenTally`；
- Stable 数据：`%LocalAppData%\AgenTally\Stable`；
- 一个开始菜单入口，可选桌面快捷方式；
- 不创建 Windows Service 或计划任务；
- 不默认启用开机自启；
- 不需要管理员权限。

升级复用同一固定应用身份和已记录的安装目录。网络盘、UNC、卷根、重解析点
路径、冲突的安装记录或无法证明所有权的非空目录都会失败关闭。

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
