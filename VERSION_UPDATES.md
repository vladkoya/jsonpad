# Version Updates

This file tracks notable feature additions and release-level changes.

## v1.2.0

### Added

- Ultra-large mode for files >= 300 MB with paged read-only navigation
  - Previous/Next page controls
  - 8 MB page loading window
- Background streaming JSON validation during load
  - Status bar reports: idle, running, valid, invalid, error
- Live operation metrics in status bar
  - Progress percentage
  - Throughput (bytes/KB/MB/GB per second)
  - ETA and completion timing for load/save/page operations
- Installer and release polish
  - App icon (`JsonPad/Assets/JsonPad.ico`)
  - Assembly metadata/version info in `JsonPad.csproj`
  - One-click Inno Setup installer script (`installer/JsonPad.iss`)
  - Installer build/signing notes (`installer/README.md`)

### Improved

- Large-file loading responsiveness by streaming content in chunks.
- Reduced memory pressure in large-file mode by disabling undo.

### Fixed

- Package restore compatibility by removing AvalonEdit dependency.
- Build error in file IO service by adding missing `System.IO` import.
