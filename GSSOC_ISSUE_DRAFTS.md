# GSSOC 2026 Issue Drafts for WDEM

This file contains the drafts for the issues we identified. You can copy-paste these into GitHub Issues or I can run the commands when you're ready.

---

## Issue 1: [Docs] Expand Module Documentation
**Labels**: `type:docs`, `GSSOC`, `level:beginner`

---

## Issue 2: [Docs] Create a "Configuration Cookbook"
**Labels**: `type:docs`, `GSSOC`, `level:beginner`

---

## Issue 3: [Settings] Add Validation for Volume and Brightness
**Labels**: `type:testing`, `GSSOC`, `level:beginner`

---

## Issue 4: [Settings] Add a "Privacy" Preset
**Labels**: `type:feature`, `GSSOC`, `level:beginner`

---

## Issue 5: [Tests] Add Unit Tests for Configuration Models
**Labels**: `type:testing`, `GSSOC`, `level:beginner`

---

## Issue 6: [Settings] Expand the Registry Settings Catalog
**Labels**: `type:feature`, `GSSOC`, `level:intermediate`

---

## Issue 7: [Plugin] Oh My Posh Theme Configurator (Python/JS)
**Labels**: `type:feature`, `GSSOC`, `level:intermediate`

**Description**:
Oh My Posh is a popular prompt theme engine for any shell. Its configuration is stored in a complex JSON/YAML file. We want a plugin to manage these themes declaratively.

**Tasks**:
- Create a new plugin in `plugins/oh-my-posh/`.
- Allow users to specify a theme name or a local theme file path in `config.yaml`.
- The plugin should download/copy the theme and update the user's PowerShell `$PROFILE` to initialize it.
- Ensure it handles path refreshes so the prompt works immediately.

---

## Issue 8: [Plugin] Microsoft PowerToys Configurator (Python/JS)
**Labels**: `type:feature`, `GSSOC`, `level:intermediate`

**Description**:
Microsoft PowerToys is a suite of utilities for power users (FancyZones, Awake, PowerRename, etc.). All settings are stored in JSON files within `%LOCALAPPDATA%\Microsoft\PowerToys\`.

**Tasks**:
- Create a new plugin in `plugins/powertoys/`.
- Implement logic to manage general PowerToys settings and at least 3 specific modules (e.g., FancyZones, Awake, and PowerToys Run).
- The plugin should handle merging the desired state into the existing JSON files and notify PowerToys to reload configurations if necessary.

---

## Issue 9: [Plugin] Community Choice: Config Manager for any Windows App (Python/JS)
**Labels**: `type:feature`, `GSSOC`, `level:intermediate`

**Description**:
Windows is full of applications that store configuration in various places (AppData, Registry, JSON files, etc.). We want you to build a plugin for an application of your choice that makes its configuration declarative.

**Tasks**:
- Choose a common Windows application (e.g., Discord, Steam, Spotify, Obsidian, etc.) that has a complex or hidden configuration.
- Identify where it stores its settings (e.g., `%APPDATA%`, `%LOCALAPPDATA%`, or Registry).
- Create a new plugin in `plugins/<app-name>/`.
- Implement logic to manage these settings via WDEM's `config.yaml`.
- Ensure the plugin is idempotent (doesn't overwrite changes if the state is already correct).

---

## Issue 10: [Security] Native Windows Credential Provider
**Labels**: `type:security`, `GSSOC`, `level:advanced`

---

## Issue 11: [Plugins] PowerShell Plugin Support
**Labels**: `type:feature`, `GSSOC`, `level:advanced`

---

## Issue 12: [Logic] Enhanced "Dry Run" Diff View
**Labels**: `type:feature`, `GSSOC`, `level:advanced`
