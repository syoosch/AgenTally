# AgenTally

AgenTally 是一款面向 Windows 的本地 Agent Token 用量统计工具。它只读分析
本机已有的 Agent 记录，在本地展示 Token、模型、项目、会话、趋势、热力图
以及按公开 API 规则估算的等效价格。

## 主要功能

- 按 Agent、模型、项目、根会话和时间范围统计 Token；
- 区分输入、缓存读取、缓存写入、输出与 reasoning Token；
- 提供概览、分析、项目、会话、Prompt 时间线和数据来源页面；
- 使用本地 SQLite 保存统计结果；
- 支持手动备份、恢复、重新扫描和清除本地统计；
- 可由用户主动选择是否随 Windows 登录启动；
- Core 在后台和系统托盘持续统计，WPF 界面按需打开。

当前支持 Codex、Claude Code、Kimi Code、Kimi Work、ZCode、WorkBuddy、
Qwen Code、Qoder/Qoder CN Desktop、Gemini CLI 和 OpenCode 的已验证本地
记录格式。不同来源能够可靠提供的 Token、Prompt、工具和父子 Agent 字段
并不完全相同；AgenTally 会明确显示不可取得的字段，不从正文或时间猜测。

## 隐私与安全

- Agent 文件、日志和数据库始终作为只读输入；
- 不安装 Hook，不注入或代理 Agent，不包装 Agent 命令；
- 不修改 Agent 配置、环境变量、进程或网络链路；
- 不读取账号凭据、Cookie、登录 Token 或订阅额度；
- 不提供匿名遥测，不自动上传统计、崩溃日志或工作文件；
- 不保存完整 Prompt、回复、工具参数/输出、附件内容或附件路径；
- 正常采集、统计和价格估算不联网。

版本检查是唯一允许的联网边界，并且只在正式发布渠道配置完成后启用。

## 安装与使用

正式版本发布后，请从本仓库的 GitHub Releases 下载 Windows x64 安装程序。
安装器面向当前 Windows 用户，不需要管理员权限，也不会默认启用开机自启。

启动 AgenTally 后：

1. Core 自动发现并增量读取受支持的本地记录；
2. 从左侧页面查看概览、分析、项目、会话和来源状态；
3. 在设置中维护本地数据、备份以及开机自启选择；
4. 关闭窗口后 Core 和托盘继续运行；从托盘可以重新打开或完全退出。

等效 API 价格只是一致规则下的估算值，不等同于供应商账单、订阅额度或
实际付款金额。

## 从源码构建

要求 Windows 和 `global.json` 指定的 .NET SDK：

```powershell
dotnet restore AgenTally.sln --locked-mode
.\scripts\Test-AgenTallyPublicBoundary.ps1
.\scripts\Test-AgenTallyPrepackageSecurity.ps1
dotnet test --project tests/AgenTally.Tests/AgenTally.Tests.csproj --configuration Release --no-restore
dotnet build AgenTally.sln --configuration Release --no-restore --no-incremental -warnaserror
```

贡献方式和完整验证要求见 [CONTRIBUTING.md](CONTRIBUTING.md)。Stable 安装包
的构建与所有权边界见 [docs/PACKAGING.md](docs/PACKAGING.md)。

## 许可证与第三方声明

AgenTally 使用 [MIT License](LICENSE)。项目使用或参考的第三方项目、目录数据
和许可证声明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
