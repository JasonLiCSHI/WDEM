# Get started with WDEM

WDEM is distributed as a self-contained Windows x64 ZIP. Download these three
assets from the same WDEM release:

- `Wdem-win-x64.zip`
- `SHA256SUMS.txt`
- `THIRD-PARTY-NOTICES.md`

## Verify and extract

In PowerShell, from the download directory:

```powershell
$expected = (Get-Content .\SHA256SUMS.txt).Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)[0]
$actual = (Get-FileHash .\Wdem-win-x64.zip -Algorithm SHA256).Hash
if ($actual -ne $expected) { throw 'WDEM archive checksum mismatch.' }

Expand-Archive .\Wdem-win-x64.zip -DestinationPath .\WDEM -Force
```

Do not copy out a single executable. Retain every file and the exact `Cli`,
`Desktop`, and `ElevatedHost` directories from the archive. In particular,
the unpackaged WinUI 3 desktop requires its Windows App SDK and runtime files
beside `Wdem.Desktop.exe`.

## Start the product

From the extracted WDEM directory, launch the desktop experience with
`Desktop\Wdem.Desktop.exe`:

```powershell
.\WDEM\Desktop\Wdem.Desktop.exe
```

Or use `Cli\Wdem.Cli.exe` to inspect the shipped C# profile from the command
line:

```powershell
.\WDEM\Cli\Wdem.Cli.exe inspect --profile .\WDEM\Desktop\profiles\csharp-developer.yaml
```

Inspect and review the complete plan before Apply. Apply can install software,
change developer configuration, and request one UAC consent through the
elevated host.

## Product data and inputs

WDEM stores run snapshots, recovery state, and redacted logs under
`%LOCALAPPDATA%\WDEM`; run records are under `%LOCALAPPDATA%\WDEM\runs`.
Do not edit an active run snapshot.

Profiles are explicit inputs. The shipped optional company extension reads only:

- `WDEM_COMPANY_VSIX_PATH`: an absolute local path or approved HTTPS URI for
  the VSIX package;
- `WDEM_COMPANY_VSIX_SHA256`: exactly 64 hexadecimal SHA-256 characters.

Leave both variables unset when the optional company extension is not selected.
WDEM does not define a public environment-variable override for its profile or
state paths.

On first start, WDEM can record a one-time import from the retired source
product's state directory. The exact source path and marker contract are
documented in [source provenance](source-provenance.md#one-time-state-import).
Imported entries are historical hints, never authoritative compliance. Every
new run performs a fresh Detect and builds a new Plan.

Continue with [profile authoring](profile-authoring.md) or
[recovery and security](recovery-and-security.md).
