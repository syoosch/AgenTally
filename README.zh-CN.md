# AgenTally

[![Release](https://img.shields.io/github/v/release/syoosch/AgenTally?display_name=tag&sort=semver&style=flat-square)](https://github.com/syoosch/AgenTally/releases/latest)
[![CI](https://github.com/syoosch/AgenTally/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/syoosch/AgenTally/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg?style=flat-square)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4?style=flat-square)

[English](README.md) | **简体中文**

AgenTally 是一款面向 Windows 的本地 AI Agent Token 用量统计与分析工具。
它只读分析本机已有记录，帮助你按 Agent、模型、项目、会话和时间范围了解
Token 消耗、趋势与等效 API 价格。

> **界面语言：**当前应用界面仅提供简体中文。英文
> [用户手册](docs/USER_GUIDE.md)会同时标出应用中的真实中文标签。

[下载最新版](https://github.com/syoosch/AgenTally/releases/latest) ·
[阅读用户手册](docs/USER_GUIDE.zh-CN.md) ·
[使用帮助](SUPPORT.zh-CN.md) ·
[隐私说明](PRIVACY.zh-CN.md)

## 下载

从 [GitHub Releases](https://github.com/syoosch/AgenTally/releases/latest)
下载 Windows x64 安装程序和与它同名的 `.sha256` 文件。

> 当前安装程序未进行代码签名，Windows SmartScreen 可能显示“未知发布者”。
> 请只从本仓库 Release 下载。SHA-256 可核对下载完整性，但不能替代代码
> 签名或证明发布者身份。

| 要求 | 说明 |
| --- | --- |
| 操作系统 | Windows 10 或 Windows 11 |
| 架构 | x64 |
| 安装范围 | 当前 Windows 用户 |
| 管理员权限 | 不需要 |
| .NET | 安装程序已自包含，不需要预装 .NET |
| 应用界面 | 简体中文 |

把一个安装程序及其同名 `.sha256` 文件放在同一文件夹，在该文件夹打开
PowerShell 并运行：

```powershell
$setup = @(Get-ChildItem -File .\AgenTally-*-win-x64-setup.exe)
if ($setup.Count -ne 1) { throw "此文件夹中必须只有一个 AgenTally 安装程序。" }
$sidecar = [System.IO.Path]::ChangeExtension($setup[0].FullName, '.sha256')
$expected = ((Get-Content -LiteralPath $sidecar -Raw) -split '\s+')[0]
$actual = (Get-FileHash -LiteralPath $setup[0].FullName -Algorithm SHA256).Hash
if ($actual -ne $expected) { throw "SHA-256 不一致，请勿运行该安装程序。" }
"SHA-256 校验通过：$actual"
```

## 你可以用它做什么

- 按 Agent、模型、逻辑项目、根会话和时间范围统计 Token；
- 查看输入、缓存读取、缓存写入、输出等来源可靠提供的分类；
- 查看趋势、热力图、项目、会话和 Prompt 时间线；
- 按公开 API 价格规则估算等效价格，为已使用的模型维护价格及生效日期，
  保留此前费率与已计价历史；
- 在本地备份、恢复、重新扫描或清除统计；
- 关闭窗口后由 Core 和系统托盘继续统计，需要时再打开界面。

## 三步开始

1. 从 [最新 Release](https://github.com/syoosch/AgenTally/releases/latest)
   下载并运行 Windows x64 安装程序；
2. 启动 AgenTally，它会自动发现并只读采集受支持 Agent 已写入的本地记录；
3. 从左侧进入“概览”“分析”“项目”“会话”和“数据来源”，在“设置”中
   管理本地数据、备份、价格、隐私、更新和开机自启。

关闭主窗口只会退出界面，Core 和托盘会继续统计。需要完全退出时，请在
系统托盘中选择“退出 AgenTally”。

## Agent 兼容性

“支持”表示核心统计流程已经接入；“部分兼容”表示可靠字段继续统计，而来源
没有提供或语义不能确认的字段会显示为不可取得。兼容性取决于本地记录格式，
不会因为缺失字段而从正文、时间或账户额度猜测。

| 等级 | Agent | 当前边界 |
| --- | --- | --- |
| 支持 | Codex（Desktop 与 CLI） | 统一归入 Codex，支持 Token、项目、会话及来源可靠提供的 Prompt/调用归属 |
| 支持 | Claude Code（CLI 与 Desktop Code） | 从已验证的本地记录统一统计；来源缺失的事实仍标为不可取得 |
| 支持 | DeepSeek Harness（Desktop 与 CLI） | 支持本地会话 Token、Prompt、工具和父子任务事实；独立 reasoning 计数不可取得 |
| 支持 | Kimi Code CLI | 支持来源证明的逐轮 Token、项目、会话和 Prompt 归属 |
| 支持 | Kimi Work（Desktop Work 模式） | 与 Kimi Code 分开统计；普通聊天不在范围内 |
| 部分兼容 | ZCode | 支持本地用量记录；工具与子任务关系不可可靠取得 |
| 部分兼容 | WorkBuddy | 支持可靠 Token、项目、会话、Prompt 和工具；父子任务关系不可取得 |
| 部分兼容 | Qwen Code | 支持可靠 Token、项目、会话、Prompt 和工具；部分分类与父子关系不可取得 |
| 部分兼容 | Qoder / Qoder CN Desktop | 支持本地数据库中的逐消息 Token；部分分类、工具和 reasoning 不可取得 |
| 部分兼容 | Gemini CLI | 基于固定来源契约接入，尚无隐私安全的本机真实样本验证 |
| 部分兼容 | OpenCode | 支持已知 SQLite/JSON 结构，尚无隐私安全的本机真实样本验证 |
| 仅结构发现 | Qoder CLI | 可发现来源、会话和 Prompt 结构；当前没有可靠的逐调用 Token 契约 |

详细状态可在应用的“数据来源”页面查看。

## 隐私摘要

- Agent 文件、日志和数据库始终作为只读输入；
- 不读取凭据、Cookie、登录 Token 或订阅额度，也不上传遥测、统计或崩溃日志；
- 不保存完整 Prompt、回复、工具参数/输出、附件路径或内容；来源能可靠识别时，
  可以只在本地保存受长度限制的 Prompt 摘要；
- 正常采集、统计和价格估算不联网。版本检查是唯一允许的联网面，但当前正式
  发布渠道尚未配置。

完整的数据、备份、联网和删除边界见 [PRIVACY.zh-CN.md](PRIVACY.zh-CN.md)。

## 当前限制

- 应用界面仅提供简体中文；
- 安装程序尚未进行代码签名；
- 当前需要从 GitHub Releases 手动确认并安装更新，应用内不提供下载安装；
- 本地来源不能可靠提供契约时，部分 Agent 或字段会保持部分兼容或不可取得。
- 价格历史以可核验的官方资料为准，不猜填缺失日期或费率；DeepSeek 峰谷
  节假日表目前只覆盖 2026 年。
- 新版安装器会阻止降级并允许同版本修复；已发布的旧安装包不能追溯加入保护。

## 文档与支持

- [用户手册](docs/USER_GUIDE.zh-CN.md)
- [使用帮助与故障排查](SUPPORT.zh-CN.md)
- [隐私说明](PRIVACY.zh-CN.md)
- [安全策略](SECURITY.md)
- [更新日志](CHANGELOG.md)
- [贡献指南](CONTRIBUTING.md)
- [Stable 打包说明](docs/PACKAGING.md)

## 许可证与第三方声明

AgenTally 使用 [MIT License](LICENSE)。项目使用或参考的第三方项目、目录数据
和许可证声明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
