> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# WSL (Windows Subsystem for Linux)

Manages WSL distributions.

**YAML Key:** `wsl`

**Properties:**

- `update`: `true` to run `wsl --update`.
- `defaultVersion`: Set the default WSL version (e.g., `2`).
- `defaultDistro`: Set the default WSL distribution by name.
- `distros`: A list of distributions to install and configure.
  - `name`: The distro name (e.g., `Ubuntu-22.04`).
  - `setupScript`: Path to a shell script to run inside the distro after installation.

**Example:**

```yaml
wsl:
  update: true
  defaultVersion: 2
  defaultDistro: 'Ubuntu-22.04'
  distros:
    - name: 'Ubuntu-22.04'
      setupScript: 'scripts/ubuntu_setup.sh'
```
