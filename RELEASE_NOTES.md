# WDEM release status

WDEM currently ships no product executable. The repository contains
`Wdem.Core`, `Wdem.LegacySource`, and their test suites while the product hosts
are developed.

Release validation is manual and verifies `Wdem.sln` with restore, build, and
test. Binary publication, checksums, and release assets will be enabled only
when `Wdem.Cli` and `Wdem.Desktop` exist.
