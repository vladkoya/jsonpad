# JsonPad installer

## Build app binaries

```powershell
dotnet publish JsonPad\JsonPad.csproj -c Release -r win-x64 --self-contained true
```

## Build one-click installer

```powershell
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\JsonPad.iss
```

The installer will be generated at `installer\output\JsonPad-Setup.exe`.

## Code-signing preparation

`JsonPad.iss` includes an `EnableCodeSigning` preprocessor constant:

- `0` (default): build unsigned installer
- `1`: sign installer and uninstaller with `signtool`

Before switching to `1`, ensure:

- `signtool.exe` is installed and on PATH
- a code-signing certificate is available in the Windows certificate store
- timestamp endpoint is reachable (`http://timestamp.digicert.com`)
