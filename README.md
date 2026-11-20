<div align="center">

<img src="recursos/icone.png" width="112" alt="Multi Claude">

# Multi Claude Codex

**Run several Claude Desktop accounts side by side — each one in its own window.**

Based on Multi Claude, created by Richard KPL. Axel (AFielen) develops and maintains the Codex edition. [Original project](https://github.com/RichardKPL/multi-claude) · [Codex edition](https://github.com/AFielen/Multi-Claude-Codex)

[![Original release](https://img.shields.io/github/v/release/RichardKPL/multi-claude?label=original%20release&color=d97757&style=for-the-badge)](https://github.com/RichardKPL/multi-claude/releases/latest/download/Multi-Claude-Portable.exe)
[![License GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-1f1e1d?style=for-the-badge)](LICENSE)
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078d4?style=for-the-badge&logo=windows)](#requirements)
[![Portable](https://img.shields.io/badge/portable-no%20install-5fa85e?style=for-the-badge)](#download)

[**Original website**](https://richardkpl.github.io/multi-claude/) ·
[**Original changelog**](https://richardkpl.github.io/multi-claude/changelog.html) ·
[**Build the Codex edition**](#windows-wpf-edition) ·
[**How it works**](#how-it-works) ·
[**License**](#license--credits)

<img src="docs/img/en/principal-claro.png" alt="Multi Claude 2.1 main window" width="860">

</div>

---

## Windows WPF edition

The native C#/.NET port of version 2.3.0 is in the Windows project. [Build, test and catalog compatibility](windows/README.md)

## ✨ Features

| | |
|---|---|
| 👥 **Unlimited profiles** | Every profile is a separate Claude Desktop with its own sign-in, sessions and settings. |
| 🔐 **Profile sign-in** | Select a profile and explicitly paste its browser return link in the profile's sign-in dialog. |
| 🟢 **Profile switching** | Select a profile in the sidebar, drag to reorder and open or focus its window. Profiles can have a global shortcut, a notification-area initial and a saved Windows shortcut. |
| **Claude Code** | New profiles have their own Code configuration and terminal command. Existing profiles keep their previous shared configuration until you enable isolation in the editor. |
| **Usage** | View recorded input, output and cache tokens. The optional Claude Code status line supplies five-hour and weekly plan readings, reset times and a warning above 80%. |
| 🖼️ **Profile pictures** | Built-in gallery plus your own images. Any image dropped into `profile_pictures` becomes an option. |
| 🎨 **Light, Dark or System** | Follows the Windows theme live — title bar included. |
| 🌍 **10 languages** | Português, English, Español, Français, Deutsch, Italiano, Русский, 日本語, 中文, 한국어. |
| 📦 **Portable** | Newly created profiles live next to the executable. Imported profiles keep using their original directories. |
| ⇩ **Profile import** | Finds Claude profiles that already exist on the machine (or another `perfis.db`) and imports them. |
| 🧨 **Danger zone** | Remove manager settings and eligible Desktop data with typed confirmation. Separate Claude Code directories are retained. |
| 📐 **Responsive layout** | The manager opens at 480 × 360 logical pixels. Pick a profile directly or search by name with Ctrl+K. Widen the window for a sidebar. The manager remembers your window size, selection and profile order. |

<div align="center">
<img src="docs/img/en/principal-escuro.png" width="420" alt="Multi Claude 2.1 dark theme">
<img src="docs/img/en/fotos.png" width="380" alt="Profile pictures">
</div>

## ⬇️ Download

1. For the modified edition, use **Run and build from source** below. The original author's published build is available from the [upstream releases page](https://github.com/RichardKPL/multi-claude/releases/latest).
2. Put it in a folder of its own (e.g. `Documents\Multi Claude`) — that's where your data will be stored.
3. Run it. On first launch it asks for your language and offers to import any profiles it finds.

> The local build produces an unsigned Windows executable.

### Requirements

- Windows 10 or 11
- [Claude Desktop](https://claude.ai/download) installed
- A writable local folder for the portable app and its profiles (a local USB drive also works).

## 🚀 Usage

1. Choose **New profile** and set its name, picture, color and optional shortcut. **Edit profile** opens the same settings later. Windows 11 supports a profile-colored native window border.
2. Select **Sign in** on the profile you want and complete sign-in in Claude. If the browser shows a return link for Claude Desktop, copy it, choose **Paste link** in Multi Claude, check the profile name in the dialog, paste and confirm. The manual flow requires a browser that exposes the return link.
3. Pick a profile and use **Open** or **Switch to window**. Ctrl+K opens profile search. Use the arrow keys and Enter to select a match. A global shortcut or notification-area letter works while the manager is running. **Edit profile** is below the profile name. In **Profile details** you can reorder the profile and save a Windows shortcut that also opens it when the manager is closed.
4. In **Claude Code**, choose **Open terminal**, **Sign in** or **Copy command**. The terminal requires the official Windows CLI. Switching Code configuration takes effect on a fresh launch. Optional sharing connects existing skills, agents, commands and user instructions with the standard setup. Personal files already present in the isolated configuration take precedence. [Claude Code setup](https://code.claude.com/docs/en/setup)
5. **Overview** shows the five-hour and weekly plan readings with their reset times. **Claude Code** shows token totals for the selected isolated profile’s retained local history. Refresh keeps your tab, position and previous readings visible while the new data loads. **Enable limit readings** installs a status line in that profile. A supported account receives readings after an API response. Existing custom status lines are preserved. [Claude Code status line](https://code.claude.com/docs/en/statusline#available-data)

<div align="center">
<img src="docs/img/en/boas-vindas.png" width="300" alt="Welcome screen">
<img src="docs/img/en/configuracoes.png" width="260" alt="Settings">
</div>

## ⚙️ How it works

- Each profile runs Claude Desktop with `--user-data-dir=<profile folder>`, so sign-ins and sessions stay isolated.
- **Sign-in** passes the link you explicitly confirm to the selected Claude Desktop instance. Canceling or timing out discards the pending action. If an older Multi Claude installation registered itself as a browser or protocol handler, open Windows Settings → Apps → Default apps and choose your browser for web links and Claude Desktop for `claude://`.
- **Status** uses each profile's Claude log and configuration for sign-in status, and the running Claude processes for open status.
- **Sign out** requests a normal shutdown of the profile's Claude instance, then clears its local cookies and account information. A cleanup error is shown in the app.
- **Delete and reset** preserve external profile folders. Erasing a registered profile's data requires an exclusively used profile directory directly inside `dados`. Unregistered directories are also preserved.

### Where data lives (portable mode)

```
Multi Claude.exe
perfis.db            ← profiles, language, theme (SQLite)
log.txt              ← diagnostic log
dados\<profile>\     ← each profile's Claude data
claude-code\<uuid>\  ← isolated local Claude Code data
profile_shortcuts\  ← profile icons for saved Windows launchers
profile_pictures\    ← default pictures + the ones you add (usuario_*)
```

After moving the executable or the profile folder, recreate exported Windows shortcuts and copy fresh terminal commands. Both contain absolute paths. Local Code settings use `CLAUDE_CONFIG_DIR` when starting the Windows CLI and Claude Desktop. Desktop authentication is separate, and remote, cloud and WSL environments have their own storage. [Desktop environment configuration](https://code.claude.com/docs/en/desktop#environment-variables-in-local-sessions)

## 🛠️ Run and build from source

```powershell
py -3.14 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements-build.txt
.\.venv\Scripts\python.exe -m unittest discover -s recursos -p "test_*.py"
.\.venv\Scripts\python.exe recursos/build_exe.py
.\.venv\Scripts\python.exe recursos/smoke_exe.py
```

On Windows, the build creates `dist/Multi Claude.exe`. Distribute it together with `LICENSE`, `NOTICE`, `AUTHORS` and `THIRD-PARTY-NOTICES.txt` from the same directory. The build verifies both icons, ten default pictures, the license and the attribution files against their recorded SHA-256 hashes. To run the source directly, use `.\.venv\Scripts\pythonw.exe "Multi Claude.pyw"`.

```
Multi Claude.pyw        application source
recursos/               icon and build script
profile_pictures/       default profile pictures
docs/                   website (GitHub Pages)
LICENSE · NOTICE        GPL-3.0 license + attribution terms
AUTHORS                 original project and Codex edition credits
```

## 🌐 Website

The `docs/` folder contains the source for the original Multi Claude website. The site detects the visitor's browser language on first visit, falls back to English when it is not supported, and also provides a manual language selector. [Richard KPL’s project website](https://richardkpl.github.io/multi-claude/)

Screenshots in `docs/img/<language>/` show fictional profiles. They follow the selected website language and fall back to English if an image is unavailable. To regenerate them on Windows with Pillow installed, run `python recursos/capture_screenshots.py` (or append language codes). The capture tool uses a temporary database and disables Claude discovery and status polling.

## 📄 License & credits

Multi Claude Codex uses the original GNU General Public License, version 3, with the additional attribution terms from Multi Claude. [License](LICENSE) · [Original notice](NOTICE)

- **Original project.** Based on Multi Claude, created by Richard KPL. © 2026 Richard KPL. [Author](https://github.com/RichardKPL) · [Multi Claude](https://github.com/RichardKPL/multi-claude)
- **Codex edition.** © 2026 Axel (AFielen) for changes in this edition. [Maintainer](https://github.com/AFielen) · [Multi Claude Codex](https://github.com/AFielen/Multi-Claude-Codex)

If you distribute a modified version, include the original `LICENSE` and `NOTICE` and keep Richard's credit visible in the About section, footer and README as described in `NOTICE`. For a version based on Multi Claude Codex, please carry both credits and project links forward and identify your own changes. [Credits to include](AUTHORS)

> **Disclaimer:** "Claude" and "Claude Desktop" are trademarks of Anthropic, PBC. Multi Claude is an independent project and is **not affiliated with, endorsed by or sponsored by Anthropic**. Please use it in accordance with [Claude's terms of service](https://www.anthropic.com/legal/consumer-terms).
