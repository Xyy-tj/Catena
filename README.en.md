<div align="center">

<img src="src/Catena.App/Assets/catena-64.png" width="64" height="64" alt="Catena blue cat icon" />

# Catena

**Keep your folders close. Find the file you need.**

A multi-pane file workspace for Windows · Everything search · AI assistance

[简体中文](README.md) · **English**

[![Build](https://github.com/Xyy-tj/Catena/actions/workflows/build.yml/badge.svg)](https://github.com/Xyy-tj/Catena/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/Xyy-tj/Catena?color=1266d6)](https://github.com/Xyy-tj/Catena/releases/latest)
![Windows x64](https://img.shields.io/badge/Windows-x64-0078D4)
[![License: MIT](https://img.shields.io/badge/License-MIT-22a06b)](LICENSE)

[Download](https://github.com/Xyy-tj/Catena/releases/latest) · [Get started](#get-started) · [Features](#features) · [Development](#development) · [Report an issue](https://github.com/Xyy-tj/Catena/issues)

</div>

![Catena light workspace with four independent file panes and a content preview sidebar](docs/images/workspace-light.png)

<p align="center"><sub>Actual application UI rendered with fictional folders and demo files. This README describes the development branch; check release notes for features available in a downloaded build.</sub></p>

## Why Catena

Project files, downloads, references, and outputs often live in different folders. Catena keeps those folders visible in one window, searches them through Everything, and brings results back to the pane where you are working. When you need help organizing them, ask your own AI model with the current folder as context.

Catena is under active early development for Windows x64. The application UI is primarily Chinese; this English README does not imply complete English localization.

## Features

| Capability | What you can do |
| :--- | :--- |
| **Multiple panes** | Switch between one pane, horizontal or vertical pairs, and four panes; drag the dividers to resize |
| **Independent navigation** | Use each pane's own path, folder tree, filters, sorting, pinned location, and system folder picker |
| **Unified search** | Search the current pane, workspace, or computer from one entry point; locate results in the originating pane |
| **Saved workspaces** | Automatically restore paths, layout, selections, and zoom; save different workspaces for different tasks |
| **File previews** | View images, text, code, and PDFs; preview PowerPoint and Word through installed Windows preview handlers |
| **Places and history** | Revisit favorites and recent folders, discover OneDrive, and cache folder structure and file-type statistics |
| **Everyday operations** | Select multiple files, copy, paste, rename, delete, copy paths, open a terminal, or reveal files in Explorer |
| **AI assistance** | Find files using natural language and discuss folder organization, with streaming replies and pasted attachments |
| **Appearance and updates** | Light, dark, system theme, and a blue-mist background; automatic or manual stable-release checks |

### Several folders, one workspace

Each pane keeps its own state, including when hidden by a layout change. Windows file-type icons, compact rows, and editable breadcrumbs keep navigation familiar. Use `Ctrl + mouse wheel` to resize a pane's file list. Drag dividers to adjust the layout, or double-click to restore equal proportions.

<details>
<summary><strong>See the dark workspace</strong></summary>

![Catena dark theme with four panes and file preview](docs/images/workspace-dark.png)

</details>

### Everything search, one entry point

Enter a filename, keywords, or a full path in the top bar, or click a pane's search icon. Results open in a separate window; double-click a result to locate it.

Catena queries the index directly through **Everything Unicode IPC**, avoiding a command-line process and temporary result file for every search. The installer bundles the official Everything setup component and reuses existing installations. Initial indexing still takes time.

<p align="center"><img src="docs/images/search.png" width="820" alt="Search window showing six demo results with icons, filenames, locations, and sizes" /></p>

Folder scopes include subfolders. When the index is unavailable, an explicit current-folder scan is available. Ordinary searches match keywords; the full Everything advanced query syntax is not currently accepted.

### Your model, your files

Configure an **OpenAI-compatible Base URL, model ID, and API key** to enable AI features. Compatible cloud endpoints and local services are supported.

- **AI search**: Ask for something like “budget spreadsheets modified last month.” The model turns your description into filename, extension, date, and size filters, then queries the local index.
- **Folder conversations**: Open AI chat from a pane's `⋯` menu to discuss organization, naming, or archiving.
- **Attachments and streaming**: Paste text, files, or clipboard images with `Ctrl + V`. Replies appear progressively and can be stopped or copied.

<p align="center"><img src="docs/images/ai-chat.png" width="720" alt="AI chat discussing a research folder structure and consistent file naming" /></p>

<p align="center"><sub>The conversation uses demo text without calling an external model. Actual responses and image or attachment support depend on your configured service.</sub></p>

AI search currently uses file metadata; document-content semantic search is not implemented. You decide how to act on suggestions. The AI does not automatically move or delete files.

## Get started

### Install and run

1. Visit [Releases](https://github.com/Xyy-tj/Catena/releases/latest) and download `Catena-<version>-win-x64-Setup.exe`.
2. Run the installer. It includes the .NET runtime and search setup component, installs Everything if missing, and may request administrator permission during installation.
3. Open Catena and choose your folders. Fast search becomes available once Everything finishes its initial indexing.

Published packages do not require the .NET SDK. For a portable directory, keep the entire directory together. If needed, install search components under **Settings → Everything → Install component**.

Supported platform: Windows 10 1809 or newer, x64. Windows 11 is recommended. See [installation and packaging](docs/installation.md) (Chinese).

### Configure AI (optional)

Open **Settings → AI**, enter your provider's endpoint, model ID, and key, then test the connection and save. A local service without authentication can use an empty key, for example at `http://localhost:1234/v1`. The endpoint must support Chat Completions; image input also requires a vision-capable model.

### Check for updates

Open **Settings → About** to view the installed version, check results, and visit the release page. Automatic checks are enabled by default and take effect after saving: a background check runs about 10 seconds after startup, then every 24 hours while the app remains open. An available update adds a **New version** badge beside the **About** tab. Download and run the installer from the release page when ready.

### Keyboard shortcuts

| Shortcut | Action |
| :--- | :--- |
| `Ctrl + K` | Focus global search |
| `Ctrl + L` | Edit the active pane's path |
| `F6` | Switch the active pane |
| `F5` | Refresh the current folder |
| `Alt + ← / → / ↑` | Back, forward, parent folder |
| `Alt + P` | Toggle preview |
| `Ctrl + mouse wheel` | Resize the active file list |

## Local data and privacy

Browsing, ordinary search, and preview processing run locally. Workspaces, folder caches, and settings are stored in `%LOCALAPPDATA%\Catena`. OneDrive scans read metadata only; automatic previews do not download online-only file contents.

AI requests are sent to your configured model service when you submit them. Search sends your query description. Chat can include the current path, filenames, and structure statistics, along with attachments you explicitly select. Folder context can be disabled in the chat window. API keys are encrypted using DPAPI for the current Windows user.

Automatic update checks contact GitHub's public release API for version information. They do not include folder listings, file contents, or model credentials. You can disable these checks in Settings.

## Technology

Catena uses C# and Avalonia for its desktop interface, without Electron or an embedded browser. File lists are virtualized, icons are loaded and cached in the background, and search and preview work avoid blocking the UI thread.

| Layer | Implementation |
| :--- | :--- |
| Desktop UI | .NET 10 · Avalonia 12 · Fluent theme · CommunityToolkit.Mvvm |
| Files and state | Local filesystem · SQLite workspaces and folder caches |
| Fast search | Everything Unicode IPC · Structured query filters |
| AI | OpenAI-compatible HTTP API · SSE streaming |
| Windows integration | System icons · COM preview handlers · Windows PDF engine · DPAPI |
| Testing and distribution | xUnit · Avalonia Headless · GitHub Actions · Inno Setup |

```text
src/
├─ Catena.App               Desktop UI, pane interaction, and previews
├─ Catena.Core              Navigation and workspace models
├─ Catena.Contracts         File, search, preview, and AI interfaces
├─ Catena.Storage.Local     Folder browsing, scanning, and basic previews
├─ Catena.Search.Everything Index connection and query compilation
├─ Catena.AI                Search planning, streaming chat, and attachments
├─ Catena.Persistence       SQLite and settings persistence
└─ Catena.Platform.Windows Icons, file operations, and native previews
```

## Development

Use Windows x64 and the [.NET SDK](https://dotnet.microsoft.com/download) specified in [global.json](global.json), currently `10.0.401`. From the repository root:

```powershell
git clone https://github.com/Xyy-tj/Catena.git
cd Catena
./scripts/dev.ps1 restore
./scripts/setup-everything.ps1
./scripts/dev.ps1 build
./scripts/dev.ps1 test
./scripts/dev.ps1 run
```

`setup-everything.ps1` downloads and verifies the official search components, requiring network access on first use. Development scripts prefer a local SDK under `.tools/dotnet` when available.

```powershell
# Publish a self-contained portable directory
./scripts/dev.ps1 publish

# Build the installer; requires Inno Setup 6.7+
./scripts/package.ps1
```

The portable executable is written to `artifacts/win-x64/Catena.App.exe`; installers go to `artifacts/installer/`. See [development notes](docs/development.md) (Chinese) for more context.

[Issues](https://github.com/Xyy-tj/Catena/issues) and pull requests are welcome. Include the application version, Windows version, and reproduction steps in bug reports. For preview bugs, a minimal sample without private information is particularly useful. Discuss larger changes in an issue before implementation.

## Current limitations and direction

- Full PowerPoint and Word previews depend on installed preview handlers. The PPTX fallback cannot fully reproduce complex masters, grouped shapes, or charts. Large Office files may still take time to open initially.
- Folder structure caches do not yet have filesystem watchers; some external changes require a refresh.
- Cross-pane drag and drop, cut/move operations, batch operations, and document-content semantic search are not implemented yet.
- macOS, Linux, a plugin system, and full English localization are not available. Current development prioritizes everyday Windows use.

## License and acknowledgments

Catena source code is available under the [MIT License](LICENSE). Thanks to [Avalonia](https://github.com/AvaloniaUI/Avalonia), [Everything](https://www.voidtools.com/), [SQLite](https://www.sqlite.org/), [CommunityToolkit](https://github.com/CommunityToolkit/dotnet), [Lucide](https://lucide.dev/), and [Octicons](https://primer.style/octicons/).

Everything components retain their own licenses. Third-party icon licenses are in [ThirdParty](src/Catena.App/Assets/ThirdParty/). See [asset provenance](src/Catena.App/Assets/README.md) and [screenshot notes](docs/images/README.md) for the project artwork and UI images.
