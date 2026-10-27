# Security and privacy

Please do not include API keys, cookies, SSH keys, Codex authentication files,
session rollouts, private provider configurations or account screenshots in issues.
Report a vulnerability through GitHub private vulnerability reporting when available.

The default package reads local Codex usage and uses a loopback-only CDP connection.
It does not modify WindowsApps or disable workspace trust. Provider configuration
uses the original DPAPI flow. The optional SSH quota adapter is disabled by default;
configure it only for a host you control, with the minimum existing permissions.
It does not install sudoers entries or refresh/sign into upstream accounts.

Do not expose the CDP port on a network interface. The monitor never intentionally
terminates Codex. Its update checker accepts only this repository's GitHub release
assets with a matching SHA-256 digest; automatic updates are opt-in.

The community reset forecast uses a third-party HTTPS service and is informational.
Review `docs/model-quota.md` before enabling supplier quota reads.
