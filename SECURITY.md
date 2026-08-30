# WDEM security policy

## Supported versions

Only the latest WDEM release is supported. Verify the distribution ZIP against
its release `SHA256SUMS.txt` and retain all extracted companion files.

## Report a vulnerability

Do not open a public issue for a suspected vulnerability. Use
[GitHub private vulnerability reporting](https://github.com/JasonLiCSHI/WDEM/security/advisories/new)
or email [security@wdem.dev](mailto:security@wdem.dev). Include the affected
version, impact, reproducible steps, and a minimal proof of concept. Remove
credentials, tokens, personal paths, and other secrets from attachments.

We aim to acknowledge reports within 48 hours, provide an initial assessment
within five business days, and coordinate disclosure after a fix is available.

## Product security boundary

WDEM uses a narrowly scoped UAC broker, content hashes and signature checks,
log/report redaction, atomic configuration writes, cancellation finalization,
and mandatory fresh detection/planning before Apply. See
[recovery and security](docs/wdem/recovery-and-security.md) for the user-facing
model and [source provenance](docs/wdem/source-provenance.md) for the
MIT-licensed transition boundary.
