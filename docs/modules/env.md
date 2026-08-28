> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# Environment Variables

Configures Windows environment variables for your system or user profile.

**YAML Key:** `env`

**Properties:**

- `name` : Name of the environment variable.
- `value` : Value to set.
- `scope` : `user` or `system`.

---

## Basic Usage

```yaml
env:
  - name: 'MY_VAR'
    value: 'my_value'
    scope: 'user'
```

---

## Real-World Examples

### Example 1 — Developer Setup

```yaml
env:
  - name: 'JAVA_HOME'
    value: "C:\\Program Files\\Java\\jdk-17"
    scope: 'system'
  - name: 'NODE_ENV'
    value: 'development'
    scope: 'user'
```

### Example 2 — Python Setup

```yaml
env:
  - name: 'PYTHONPATH'
    value: "C:\\Python311"
    scope: 'system'
  - name: 'PIP_DEFAULT_TIMEOUT'
    value: '100'
    scope: 'user'
```

### Example 3 — Work Setup

```yaml
env:
  - name: 'COMPANY_API_KEY'
    value: 'your_api_key'
    scope: 'user'
  - name: 'PROXY_URL'
    value: 'http://proxy.company.com'
    scope: 'system'
```

### Example 4 — Minimal Setup

```yaml
env:
  - name: 'MY_PROJECT'
    value: "C:\\Projects"
    scope: 'user'
```

---

## Troubleshooting

**Issue: Variable not found after setting**

- Restart terminal after setting variables
- Log out and log back in for system variables
- Run `echo %MY_VAR%` to verify

**Issue: Wrong scope**

- Use `system` for all users
- Use `user` for current user only
- Run WDEM as Administrator for system scope

**Issue: Value not applying**

- Check for typos in variable name
- Make sure WDEM ran successfully
- Verify in System Properties > Environment Variables
