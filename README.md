# AgenTally

[![Release](https://img.shields.io/github/v/release/syoosch/AgenTally?display_name=tag&sort=semver&style=flat-square)](https://github.com/syoosch/AgenTally/releases/latest)
[![CI](https://github.com/syoosch/AgenTally/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/syoosch/AgenTally/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg?style=flat-square)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4?style=flat-square)

**English** | [简体中文](README.zh-CN.md)

AgenTally is a local-first Windows desktop app for understanding AI Agent token
usage. It reads existing local records without modifying them and helps you
explore usage by Agent, model, project, session, and time range, including
trends and estimated API-equivalent cost.

> **Interface language:** the application UI is currently available in
> Simplified Chinese only. The English [user guide](docs/USER_GUIDE.md) includes
> the exact Chinese labels used by the app.

[Download the latest release](https://github.com/syoosch/AgenTally/releases/latest) ·
[Read the user guide](docs/USER_GUIDE.md) ·
[Get help](SUPPORT.md) ·
[Read the privacy notice](PRIVACY.md)

## Download

Download the Windows x64 installer and its matching `.sha256` file from
[GitHub Releases](https://github.com/syoosch/AgenTally/releases/latest).

> The current installer is not code-signed, so Windows SmartScreen may show an
> “Unknown publisher” warning. Download it only from this repository's Release
> page. SHA-256 verifies download integrity; it does not replace code signing or
> prove publisher identity.

| Requirement | Details |
| --- | --- |
| Operating system | Windows 10 or Windows 11 |
| Architecture | x64 |
| Installation scope | Current Windows user |
| Administrator rights | Not required |
| .NET | Self-contained installer; no separate .NET installation required |
| Application UI | Simplified Chinese |

Put exactly one downloaded installer and its matching `.sha256` file in the
same folder, open PowerShell in that folder, and run:

```powershell
$setup = @(Get-ChildItem -File .\AgenTally-*-win-x64-setup.exe)
if ($setup.Count -ne 1) { throw "Keep exactly one AgenTally installer in this folder." }
$sidecar = [System.IO.Path]::ChangeExtension($setup[0].FullName, '.sha256')
$expected = ((Get-Content -LiteralPath $sidecar -Raw) -split '\s+')[0]
$actual = (Get-FileHash -LiteralPath $setup[0].FullName -Algorithm SHA256).Hash
if ($actual -ne $expected) { throw "SHA-256 mismatch. Do not run this installer." }
"SHA-256 verified: $actual"
```

## What you can do

- Break down token usage by Agent, model, logical project, root session, and
  time range.
- Inspect source-provided input, cache read, cache write, output, and other
  reliable token categories.
- Explore trends, a usage heatmap, projects, sessions, and Prompt timelines.
- Estimate API-equivalent cost from explicit public API pricing rules and set
  local prices and effective dates for models you have used, retaining earlier
  rates and already priced history.
- Back up, restore, rescan, or clear AgenTally's local statistics.
- Close the window while the Core and system tray continue collecting, then
  reopen the UI when needed.

## Get started in three steps

1. Download and run the Windows x64 installer from the
   [latest Release](https://github.com/syoosch/AgenTally/releases/latest).
2. Start AgenTally. It automatically discovers supported local Agent records
   and reads them without modifying the source files.
3. Use Overview（概览）, Analysis（分析）, Projects（项目）, Sessions（会话）,
   and Data Sources（数据来源）from the left navigation. Settings（设置）
   contains local data, backup, pricing, privacy, update, and startup controls.

Closing the main window exits the UI while the Core and tray continue
collecting. To stop AgenTally completely, choose Exit AgenTally（退出
AgenTally）from the system tray menu.

## Agent compatibility

“Supported” means the core statistics flow is integrated. “Partial” means
reliable fields continue to work while fields that the source does not provide
or prove are shown as unavailable. Compatibility follows the local record
format; AgenTally does not guess missing values from message text, timestamps,
or account quotas.

| Level | Agent | Current boundary |
| --- | --- | --- |
| Supported | Codex (Desktop and CLI) | Combined as Codex, with token, project, session, and source-proven Prompt/call attribution |
| Supported | Claude Code (CLI and Desktop Code) | Combined statistics from verified local transcript formats; missing source facts remain unavailable |
| Supported | DeepSeek Harness (Desktop and CLI) | Local session token, Prompt, tool, and child-task facts; independent reasoning counters are unavailable |
| Supported | Kimi Code CLI | Source-proven per-turn token, project, session, and Prompt attribution |
| Supported | Kimi Work (Desktop Work mode) | Counted separately from Kimi Code; ordinary chat is outside the scope |
| Partial | ZCode | Local usage records; tool and child-task relationships are not reliably available |
| Partial | WorkBuddy | Reliable token, project, session, Prompt, and tool data; parent/child task relationships are unavailable |
| Partial | Qwen Code | Reliable token, project, session, Prompt, and tool data; some categories and parent/child relationships are unavailable |
| Partial | Qoder / Qoder CN Desktop | Per-message token data from local databases; some categories, tools, and reasoning are unavailable |
| Partial | Gemini CLI | Integrated from pinned source contracts; no privacy-safe local real-world sample has been verified yet |
| Partial | OpenCode | Known SQLite/JSON formats are supported; no privacy-safe local real-world sample has been verified yet |
| Structure only | Qoder CLI | Source, session, and Prompt structure can be discovered; there is no reliable per-call token contract yet |

See Data Sources（数据来源）in the app for the detailed status of your local
sources.

## Privacy at a glance

- Agent files, logs, and databases are always treated as read-only inputs.
- AgenTally does not read credentials, cookies, login tokens, or subscription
  quotas and does not upload telemetry, statistics, or crash logs.
- It does not store complete Prompts, responses, tool arguments/results, or
  attachment paths/content. A bounded local Prompt preview may be stored when
  the source reliably identifies it.
- Normal collection, statistics, and pricing do not use the network. The only
  permitted network surface is version checking, and the production channel is
  currently not configured.

Read [PRIVACY.md](PRIVACY.md) for the complete data, backup, networking, and
deletion boundaries.

## Current limitations

- The application UI is Simplified Chinese only.
- The installer is not code-signed.
- Updates must currently be checked and installed from GitHub Releases; there
  is no in-app download or installation flow.
- Some Agents or fields are partial or unavailable when their local records do
  not provide a reliable contract.
- Price history follows verified official evidence; gaps are not filled with
  guessed dates or fees. DeepSeek peak/off-peak holidays currently cover 2026.
- New installers prevent downgrades and allow same-version repair; previously
  released installers cannot gain that protection retroactively.

## Documentation and support

- [User guide](docs/USER_GUIDE.md)
- [Support and troubleshooting](SUPPORT.md)
- [Privacy notice](PRIVACY.md)
- [Security policy](SECURITY.md)
- [Changelog](CHANGELOG.md)
- [Contributing](CONTRIBUTING.md)
- [Stable packaging](docs/PACKAGING.md)

## License and third-party notices

AgenTally is licensed under the [MIT License](LICENSE). Third-party projects,
catalog data, and license notices are listed in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
