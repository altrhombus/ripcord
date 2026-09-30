# Security policy

## Reporting a vulnerability

**Please report privately, not in a public issue.**

Use GitHub's private vulnerability reporting: go to the **Security** tab of this repository and choose
**Report a vulnerability**. That channel is private to the maintainers and is the preferred route — it needs
no email address to be published and keeps the report attached to the repository.

Please include, as far as you can:

- what the issue is and which component is affected;
- how to reproduce it, or a proof of concept;
- what an attacker gains, and what access they need to start with;
- the commit or release you tested.

You will get an acknowledgement. Ripcord is a personal project maintained in spare time, so please do not
expect a same-day response; if a report is urgent, say so explicitly in the title.

Please give a reasonable window to fix an issue before disclosing it publicly. There is no bug bounty.

## Supported versions

| Version | Supported |
|---|---|
| `main` | Yes |
| [`ps3-v1.0`](https://github.com/altrhombus/ripcord/releases/tag/ps3-v1.0), the PS3 port | Yes, fixes land in a later `ps3-v*` release |

The dotnet client has not had a release yet. When `v1.0` ships, the latest `v*` release is supported, and a
fix lands in the next one. The console ports version separately, under their own tag names, as `ps3-v1.0`
does.
