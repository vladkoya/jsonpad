# JsonPad

Windows notepad-style editor for very large JSON files, built with C# and WPF.

See `VERSION_UPDATES.md` for release-by-release changes.

## What it supports

- Open and save large JSON files asynchronously
- Stream large-file loading in chunks to keep UI responsive
- Cancel long read/write operations
- Large-file mode (disables word wrap for better responsiveness)
- Ultra-large mode (>= 300 MB) with read-only byte paging
- Background streaming JSON validation while files are loading
- Live load progress, throughput, and ETA in the status bar
- JSON validate / format / minify
- Find text and go-to-line
- Status bar for file path, caret position, and content length

## Project structure

- `JsonPad/JsonPad.csproj` - WPF project (`net8.0-windows`)
- `JsonPad/MainWindow.xaml` - UI layout
- `JsonPad/MainWindow.xaml.cs` - editor and file operations logic
- `JsonPad/Services/LargeFileService.cs` - buffered async file IO
- `JsonPad/Services/JsonTools.cs` - JSON validation/format/minify helpers
- `installer/JsonPad.iss` - Inno Setup one-click installer script

## Build & run (on Windows)

1. Install .NET 8 SDK
2. From repository root:

   ```bash
   dotnet restore JsonPad/JsonPad.csproj
   dotnet run --project JsonPad/JsonPad.csproj
   ```

## Notes

- The app enters **large-file mode** automatically for files >= 25 MB.
- The app enters **ultra-large mode** automatically for files >= 300 MB.
- Format/minify for very large files can require significant memory; the app warns before running those operations in large-file mode.

## One-click installer (Inno Setup)

1. Publish app binaries:

   ```bash
   dotnet publish JsonPad/JsonPad.csproj -c Release -r win-x64 --self-contained true
   ```

2. Build installer:

   ```bash
   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\JsonPad.iss
   ```

3. Use generated installer:

   - `installer\output\JsonPad-Setup.exe`

`installer/README.md` includes code-signing preparation details.
