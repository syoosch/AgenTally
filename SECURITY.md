# Security Policy

## Supported versions

Security fixes are provided for the latest published release line.

| Version | Supported |
| --- | --- |
| Latest published release | Yes |
| Older releases | No |

## Reporting a vulnerability

Use GitHub's
[private vulnerability reporting](https://github.com/syoosch/AgenTally/security/advisories/new).
Include affected versions, impact, reproduction conditions, and a minimal proof
of concept that contains no real user data.

Do not open a public issue containing exploit details, secrets, real Agent
records, databases, prompts, responses, file paths, or screenshots containing
private information.

Reports are handled on a best-effort basis; no fixed response-time commitment
is currently offered. Please allow time to reproduce, correct, and validate a
fix before public disclosure.

## Security-sensitive areas

Reports are especially useful when they involve:

- Modification or deletion outside AgenTally-owned paths.
- Credential, cookie, login-token, or subscription-quota access.
- Network activity outside the explicitly approved version-check surface.
- Exposure of complete prompts, responses, tool payloads, attachments, or
  local project paths beyond the documented local-only behavior.
- Installer, upgrade, uninstall, path traversal, reparse-point, ACL, registry,
  or process-identity boundaries.
- SQLite integrity, backup/restore validation, IPC authorization, or source
  parsing of malicious local input.

## Safe testing

Use synthetic data and environments you own or are authorized to test. Do not
access another person's records, disrupt services, retain private data, or
publish an exploit before a fix is available.
