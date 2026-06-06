# TheOrc — Assets

Place the following files in this folder before building:

| File | Description | Source |
|---|---|---|
| `icon.png` | App icon (1024×1024 PNG, square) | Provided in project |
| `banner.png` | GitHub README banner (wide format) | Provided in project |
| `icon.ico` | Multi-resolution Windows icon | **Generated** — run `.\make-icon.ps1` |

## Generating icon.ico

```powershell
# From this folder:
.\make-icon.ps1

# Or with ImageMagick directly:
magick icon.png -define icon:auto-resize=256,128,64,48,32,16 icon.ico
```

The `.ico` file is referenced by both `OrchestratorIDE.csproj` and `OrchestratorSetup.csproj`
as the application window icon and taskbar icon.
