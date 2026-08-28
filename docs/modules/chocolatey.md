> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# Chocolatey

Installs packages using Chocolatey. Chocolatey is one of the most popular Windows package managers
with a massive library of over 9,000 packages. It is designed for system-wide software installation
and works well for both GUI and CLI applications.

**YAML Key:** `choco`

**Properties:**

- `id`: The package identifier (e.g., `git`).

---

## Basic Usage

You can search for Chocolatey packages at
[community.chocolatey.org](https://community.chocolatey.org/packages) or by running:

```powershell
choco search <app-name>
```

To install a single package using WDEM:

```yaml
choco:
  - id: git
```

To install multiple packages at once:

```yaml
choco:
  - id: git
  - id: neovim
  - id: vlc
```

---

## Advanced Configuration

### Running with Elevated Privileges

Chocolatey typically requires admin rights to install software system-wide. Make sure WDEM is run
from an **Administrator** PowerShell or terminal session.

### Real-World config.yaml Examples

**Example 1 — Developer Environment Setup**

```yaml
choco:
  - id: git
  - id: nodejs
  - id: python
  - id: vscode
```

**Example 2 — Media & Productivity Setup**

```yaml
choco:
  - id: vlc
  - id: 7zip
  - id: notion
  - id: zoom
```

**Example 3 — System Administration Setup**

```yaml
choco:
  - id: sysinternals
  - id: putty
  - id: winscp
  - id: curl
```

---

## Troubleshooting

**Issue: `choco` is not recognized as a command**

- Chocolatey is not pre-installed on Windows. Install it by running this in an Administrator
  PowerShell:

```powershell
  Set-ExecutionPolicy Bypass -Scope Process -Force
  [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072
  iex ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))
```

**Issue: Package not found**

- Double check the package ID at
  [community.chocolatey.org](https://community.chocolatey.org/packages).
- Package names in Chocolatey are case-insensitive but must be exact (e.g., `googlechrome` not
  `google-chrome`).

**Issue: Access denied / requires admin**

- Chocolatey needs Administrator privileges. Right-click your terminal and select **Run as
  Administrator**, then re-run WDEM.

**Issue: Package installs but is outdated**

- Chocolatey may have cached an older version. Run:

```powershell
  choco upgrade <package-id> -y
```
