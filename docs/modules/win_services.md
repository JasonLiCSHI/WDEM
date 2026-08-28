> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# Windows Services

Manages Windows services.

**YAML Key:** `win_services`

**Properties:**

- `name`: The service name.
- `startupType`: `auto`, `demand`, or `disabled`.
- `state`: `running` or `stopped`.

**Example:**

```yaml
win_services:
  - name: 'Spooler'
    startupType: 'disabled'
    state: 'stopped'
```
