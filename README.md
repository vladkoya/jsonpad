# JsonPad

Windows notepad-style editor for very large JSON files, built with C# and WPF.

## What it supports

- Open and save large JSON files asynchronously
- Stream large-file loading in chunks to keep UI responsive
- Cancel long read/write operations
- Large-file mode (disables word wrap for better responsiveness)
- JSON validate / format / minify
- Find text and go-to-line
- Status bar for file path, caret position, and content length

## Project structure

- `JsonPad/JsonPad.csproj` - WPF project (`net8.0-windows`)
- `JsonPad/MainWindow.xaml` - UI layout
- `JsonPad/MainWindow.xaml.cs` - editor and file operations logic
- `JsonPad/Services/LargeFileService.cs` - buffered async file IO
- `JsonPad/Services/JsonTools.cs` - JSON validation/format/minify helpers

## Build & run (on Windows)

1. Install .NET 8 SDK
2. From repository root:

   ```bash
   dotnet restore JsonPad/JsonPad.csproj
   dotnet run --project JsonPad/JsonPad.csproj
   ```

## Notes

- The app enters **large-file mode** automatically for files >= 25 MB.
- Format/minify for very large files can require significant memory; the app warns before running those operations in large-file mode.
