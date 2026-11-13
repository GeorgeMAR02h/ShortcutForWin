# Shortcut for Win

Middle-click any icon on the Windows taskbar to open a small flyout with your own group of shortcuts for that app.

*[Leia em português](README.pt-BR.md)*

## Features

- **Middle-click** a taskbar icon to open a flyout right above it.
- Each taskbar icon has its **own group** of shortcuts.
- Add programs by **dragging and dropping** files, shortcuts or folders, or with the **+** button (searchable list of installed programs).
- **Drag a shortcut out** of the flyout onto the desktop or any folder to move it there (it leaves the flyout).
- **Follows your Windows theme**: light/dark mode, accent color and transparency settings.
- Smooth animations: items rise from the taskbar and pop forward on hover.
- File Explorer keeps the default Windows behavior (middle-click opens a new window). You can exclude other apps too.
- **Follows the Windows display language** (English, Portuguese, Spanish, French and German; other languages fall back to English).
- Lives in the system tray, with an option to start with Windows.

## Requirements

- Windows 10 or 11 (taskbar at the bottom of the screen)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (not needed for the self-contained build)

## Usage

1. Run `ShortcutForWin.exe`. An icon appears in the system tray.
2. Middle-click an icon on the taskbar.
3. Click **+** to pick a program, or drag something into the flyout. Use the pin button to keep the flyout open while dragging.
4. Hover a shortcut and click the **×** to remove it, or right-click it to open the Windows context menu (with an extra **Replace…** option).

### Excluding apps

To keep the default middle-click behavior for other apps, create `%AppData%\TaskbarFlyout\excluded.txt` with one name per line (case-insensitive, matched against the taskbar button name or id):

```
# one name per line
Chrome
Spotify
```

### Debug log

Logging is off by default. To enable it, create an empty file named `debug.flag` in `%AppData%\TaskbarFlyout\` and restart the app. The log is written to `log.txt` in the same folder.

## Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```
dotnet run
```

Single-file release build:

```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none
```

## Privacy

The app uses a global low-level mouse hook only to detect middle-clicks on the taskbar. It does not record, store or send any input. Your shortcuts are saved locally in `%AppData%\TaskbarFlyout\groups.json`. The app makes no network connections.

## Known limitations

- Designed for a taskbar at the bottom of the screen.
- Windows opened as administrator may not receive the middle-click unless this app also runs as administrator.
- Because it uses a global mouse hook and is not code-signed, some antivirus software or Windows SmartScreen may show a warning.

## License

[MIT](LICENSE)
