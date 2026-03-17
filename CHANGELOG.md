# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Ultra-large mode search now scans the entire file (with wrap-around) instead of only the currently loaded page.
- Multi-file tabbed document view for opening and switching between multiple JSON files.
- Search status feedback showing where the search scanned/found, with found text highlighted in view.
- Ability to close open tabs (tab-header X, menu, and Ctrl+W) with per-tab unsaved-change prompt.
- Explicit ultra-large file open choice: **Open paged (recommended)** or **Open fully (experimental)**.
- Optional line numbers with a View menu toggle.

## [1.2.0] - 2026-03-17

### Added

- Ultra-large mode for files >= 300 MB with paged read-only navigation.
  - Previous/Next page controls
  - 8 MB page loading window
- Background streaming JSON validation during load.
  - Status bar reports: idle, running, valid, invalid, and error states
- Live operation metrics in status bar.
  - Progress percentage
  - Throughput (bytes/KB/MB/GB per second)
  - ETA and completion timing for load/save/page operations
- Installer and release polish.
  - App icon (`JsonPad/Assets/JsonPad.ico`)
  - Assembly metadata/version info in `JsonPad.csproj`
  - One-click Inno Setup installer script (`installer/JsonPad.iss`)
  - Installer build/signing notes (`installer/README.md`)
- Dedicated changelog tracking file (this file).

### Changed

- Large-file loading responsiveness improved by streaming content in chunks.
- Reduced memory pressure in large-file mode by disabling undo.

## [1.1.0] - 2026-03-17

### Added

- Repository-level `NuGet.config` pinned to `https://api.nuget.org/v3/index.json` for consistent restore behavior.

### Changed

- Replaced AvalonEdit dependency with built-in WPF `TextBox` editor to remove external package constraints.
- Optimized very large file loading flow to avoid prolonged UI freeze states.

### Fixed

- Build error in file IO service by adding missing `using System.IO;`.

## [1.0.0] - 2026-03-17

### Added

- Initial Windows WPF JsonPad application.
- Notepad-style JSON editor with:
  - Open / Save / Save As
  - Find and Go To Line
  - JSON Validate / Format / Minify actions
- Async buffered file read/write with cancellable operations.
- Large-file mode (>= 25 MB) for improved responsiveness.
- Status bar with file path, content length, and caret position.
- Initial documentation and repository ignore rules.

[Unreleased]: https://github.com/vladkoya/jsonpad/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/vladkoya/jsonpad/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/vladkoya/jsonpad/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/vladkoya/jsonpad/releases/tag/v1.0.0
