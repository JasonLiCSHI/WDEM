# WDEM security guide

WDEM applies least privilege: inspection and planning run as the current user,
and only confirmed administrator resources cross the UAC broker boundary.
Trusted configuration artifacts are pinned by SHA-256 and, where applicable,
publisher signature. Runs and reports are redacted before persistence or export.

For the complete product model—including cancellation finalization, restart
recovery, mandatory fresh Detect/Plan, one-time state import, and fetch-only
source provenance—read [Recovery and security](wdem/recovery-and-security.md).

Report suspected vulnerabilities privately according to the repository
[security policy](../SECURITY.md).
