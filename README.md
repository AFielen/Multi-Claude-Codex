> Based on Multi Claude, created by Richard KPL ([author](https://github.com/RichardKPL)). [Original Multi Claude repository](https://github.com/RichardKPL/multi-claude)

# MultiCloud Codex

<img src="recursos/icone.png" width="96" align="right" alt="MultiCloud Codex">

Run work and personal accounts side by side on Windows. MultiCloud Codex gives each Codex or Claude Desktop profile its own sign-in and window. For a Codex profile, the desktop app and the native Windows CLI share the same account.

Axel (AFielen) maintains this edition. [GitHub repository](https://github.com/AFielen/Multi-Claude-Codex) · [Windows guide (Deutsch)](windows/README.md)

## Build and start on Windows

You need 64-bit Windows 10 or 11 and the .NET 10 SDK to build the manager. Install the desktop app you want to launch. For a profile terminal, its Windows CLI must also be available.

Clone this repository and build the portable Windows edition in PowerShell.

```powershell
git clone https://github.com/AFielen/Multi-Claude-Codex.git
cd Multi-Claude-Codex
dotnet publish windows/MultiClaude/MultiClaude.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o dist/wpf-win-x64
```

Copy the **contents of `dist\wpf-win-x64`** together to a writable folder such as `Documents\MultiCloud Codex`, outside your Codex data folders. Run **`Multi Claude.exe`** from there. The package includes the .NET runtime, the picture gallery and the license files.

## Open two Codex accounts

1. Choose **New profile**, select **Codex** under **Application**, name the profile (for example, `Work`) and choose **Save**.
2. Select **Open** and sign in to the desktop app with your work account. To use that account in a terminal, select **Codex CLI → Open terminal**. You can also sign in from the **Codex CLI** tab.
3. Create a second Codex profile named `Personal`. Open it and sign in with your other account. Press **Ctrl+K** to find either profile by name.

Each new Codex profile starts with fresh settings. Its desktop data lives in `codex-desktop\<uuid>` and its native Windows `CODEX_HOME` lives in `codex-home\<uuid>`, beside the manager. Removing the profile retains its `codex-home` folder. WSL and cloud sessions use their own storage.

## Use a Claude account

Choose **Claude** under **Application** when creating the profile. Select **Sign in**. If the browser offers a return link, copy it, choose **Paste link** in the manager and confirm the profile name.

The **Claude Code** tab opens a terminal for the selected profile. For profiles with a separate Claude Code configuration, it also shows token totals from local history. **Enable limit readings** adds the five-hour and weekly readings to **Overview** once Claude Code reports them.

## Switch profiles and move your setup

- Use **Ctrl+K**, a global shortcut or the manager’s notification-area menu to open a profile or bring its window forward. Closing the manager window leaves it in the notification area. Choose **Exit manager** there to quit.
- To move your setup, copy the whole manager folder, including the `perfis.db` catalog and the profile directories. Imported profiles keep their original data paths. Recreate saved Windows shortcuts and copy fresh terminal commands after the move.
- When opening an existing Claude catalog, the manager recognizes its profiles as Claude. Once you add a Codex profile, use MultiCloud Codex to open that catalog. [Windows catalog and migration details](windows/README.md#daten-bei-einem-späteren-wechsel)

## License and credits

MultiCloud Codex is distributed under GPL-3.0 with the attribution terms in `NOTICE`. Include the license and attribution files when redistributing the portable package. [License](LICENSE) · [Notice](NOTICE) · [Authors and project credits](AUTHORS)
