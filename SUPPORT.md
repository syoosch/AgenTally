# AgenTally Support

**English** | [简体中文](SUPPORT.zh-CN.md)

Start with the [user guide](docs/USER_GUIDE.md). This page covers common
questions, where to report a problem, and what private information must not be
included in a report. The application UI is currently Simplified Chinese only;
English instructions include the matching Chinese labels.

## Choose the right channel

- Usage question: check the frequently asked questions below first.
- Reproducible problem: search
  [existing issues](https://github.com/syoosch/AgenTally/issues) and then open a
  [Bug report](https://github.com/syoosch/AgenTally/issues/new?template=bug_report.yml).
- Feature or experience suggestion: search existing issues and then open a
  [Feature request](https://github.com/syoosch/AgenTally/issues/new?template=feature_request.yml).
- Security vulnerability: use
  [GitHub private vulnerability reporting](https://github.com/syoosch/AgenTally/security/advisories/new)
  instead of a public issue.

Support is provided as maintainer time permits; no fixed response time is
currently promised. Reports written in English or Chinese are both welcome.

## Frequently asked questions

### Why is there no data after startup?

1. Confirm that a supported Agent has created local records on this computer.
2. Open Data Sources（数据来源）and check whether the source was found and which
   compatibility status is shown.
3. Keep the Core running and wait for the next incremental collection. Normal
   changes should usually appear within 30–60 seconds.
4. If the source still has no data, open Settings（设置）→ Data & Backup（数据与
   备份）and choose Rescan（重新扫描）.

Do not upload Agent source logs, Agent databases, or the AgenTally database to
a public issue while troubleshooting.

### Why are some token, Prompt, tool, or child-task fields unavailable?

AgenTally uses only fields that the local source can reliably prove. If a field
is absent, ambiguous, or cannot be mapped uniquely, it is shown as unavailable.
It is not treated as zero or inferred from message text, timestamps, or account
quotas. Data Sources（数据来源）shows the current compatibility status.

### Why does a model have token usage but no price?

Token collection and price matching use separate rules. Token data remains
available when no exact pricing rule matches, while the price is shown as
unpriced. Open Settings（设置）→ Models & Pricing（模型与计价）to set a local
price for a model that already has local usage.

### Why does the tray remain after I close the window?

Closing the main window exits the WPF UI while the Core and system tray continue
collecting. Double-click the tray icon or choose Open AgenTally（打开 AgenTally）
to open a new UI. Choose Exit AgenTally（退出 AgenTally）to stop the current
Profile's UI and Core.

### Does uninstall delete my statistics?

Not by default. The uninstaller keeps the database and settings so that they
are available after a later reinstall. Stable-owned data, logs, and runtime
state are removed only if you explicitly choose to delete all local application
data during uninstall. External backups and Agent source records are never part
of the uninstall scope.

### Does the current version check for updates automatically?

The production version channel is not configured, so automatic and manual
checks make no version request. Check
[GitHub Releases](https://github.com/syoosch/AgenTally/releases/latest)
manually for updates. The app does not download or install updates for you.

### What is the difference between rescan and clear?

Rescan（重新扫描）re-reads supported sources without modifying them and safely
adds facts that can still be proved. Clear All Statistics（清除全部统计）first
records a baseline at the end of current source data, then removes the local
statistics and existing event price bindings. New collection starts after that
baseline. A later rescan can recover history that still exists in source logs,
but it cannot recover history deleted from the sources and no longer present in
AgenTally. Custom model prices are retained.

## What can I include in a Bug report?

Provide only the minimum information needed to reproduce the problem, after
removing personal data:

- AgenTally version.
- Windows version and x64 environment.
- Installation method.
- Affected Agent and its version.
- Short, reproducible steps.
- Expected and actual behavior.
- Compatibility status shown by Data Sources（数据来源）.
- A short error message with personal information removed.

## What must not be uploaded publicly?

Do not put any of the following in an issue, comment, or attachment:

- Complete or truncated Prompts, responses, tool arguments, or tool output.
- Agent source logs, transcripts, databases, or configuration contents.
- The AgenTally database, backups, runtime files, or complete diagnostic logs.
- Credentials, cookies, login tokens, API keys, subscription, or account data.
- Complete local paths, project names, session names, attachment paths, or work
  files.
- Screenshots or recordings containing any of the information above.

If you are unsure whether something is safe, describe the symptom without
uploading the file. If more evidence is needed, continue with the smallest
sanitized or synthetic sample possible. See the [privacy notice](PRIVACY.md)
for the complete boundary.
