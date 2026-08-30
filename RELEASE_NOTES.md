# WDEM release notes

WDEM now ships as a self-contained Windows x64 product distribution. A release
contains only `Wdem-win-x64.zip`, `SHA256SUMS.txt`, and
`THIRD-PARTY-NOTICES.md`.

The ZIP preserves three product directories:

- `Cli` contains the single-file `Wdem.Cli.exe` automation host.
- `Desktop` contains the unpackaged WinUI 3 `Wdem.Desktop.exe` and all required
  Windows App SDK/runtime companion files.
- `ElevatedHost` contains the single-file `Wdem.ElevatedHost.exe` UAC broker.

The release includes the complete C# developer MVP profile, dependency-aware
Detect/Plan/Apply flows, restart recovery, redacted run reporting, and
one-time migration metadata that never substitutes for current detection.

Verify `Wdem-win-x64.zip` against `SHA256SUMS.txt` before extraction and retain
every extracted file. Source-derived transition code is a library boundary and
is not published as an executable or separate release asset.
