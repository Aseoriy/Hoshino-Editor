# Hoshino Editor

**A fast, local-first photo and video editor for Windows, built with native WPF by Sail Solutions.**

[![Release](https://img.shields.io/github/v/release/Aseoriy/Hoshino-Editor?include_prereleases&label=release)](https://github.com/Aseoriy/Hoshino-Editor/releases)
[![License](https://img.shields.io/github/license/Aseoriy/Hoshino-Editor)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-7c3aed)](https://hoshino-editor.sailhub.fyi)

[Download Hoshino Editor](https://github.com/Aseoriy/Hoshino-Editor/releases) · [Visit the website](https://hoshino-editor.sailhub.fyi) · [Report a problem](https://github.com/Aseoriy/Hoshino-Editor/issues)

> **Beta notice:** `v0.9.0-beta-1` is the first public testing release. Save a copy of important media before editing and report anything unexpected.

## Why Hoshino?

Yeah bro idk, wait till its better.

## Download and install

1. Open [GitHub Releases](https://github.com/Aseoriy/Hoshino-Editor/releases).
2. Download `HoshinoEditor-Setup-v0.9.0-beta-1-win-x64.exe`.
3. Double-click the installer and follow the setup wizard.
4. Launch Hoshino Editor from the Start menu or the optional desktop shortcut.

The installer supports 64-bit Windows 10 version 1809 or later and Windows 11. It installs per user and does not require administrator privileges.

On first launch, Hoshino registers a per-user **Open with Hoshino Editor** command for supported photo and video files. On Windows 11 it may appear under **Show more options**. Uninstalling Hoshino removes these registrations.

## Features

### Photo editor

- Import PNG, JPEG, WEBP, BMP, GIF, and TIFF images using installed Windows codecs
- Arrange multiple images in a movable, reorderable layer stack
- Crop, rotate, flip, resize, and control per-layer opacity
- Adjust exposure, contrast, saturation, and temperature
- Zoom from 5% to 800%, reset to 100%, or fit the composition
- Upscale locally from 100% to 1000% with a 120-megapixel safety limit
- Remove backgrounds locally with adjustable edge-aware tolerance
- Undo, redo, reset, and export the visible composition
- Export PNG, JPEG, BMP, and TIFF files

### Video editor

- Import multiple clips into a media bin and horizontal timeline
- Trim in/out points, reorder, duplicate, remove, and split clips
- Adjust per-clip speed from 0.5× to 4×
- Preview the timeline with volume and mute controls
- Save and load local `.hoshino` project files
- Export MP4 using Windows Media Composition
- Upscale exports from 100% to 1000%
- Use FFmpeg with NVIDIA, Intel, or AMD hardware encoders when available

### Personalization and Windows integration

- Hoshino, Midnight, Sakura, Aurora, Ember, and custom-accent themes
- Editor, performance, export, keyboard, startup, and window preferences
- Optional start-with-Windows and minimize-on-close behavior
- Per-user settings stored in `%LOCALAPPDATA%\Sail Solutions\Hoshino Editor\settings.json`

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| `Ctrl+O` / `Ctrl+I` | Open or import media |
| `Ctrl+S` | Export an image or save a video project |
| `Ctrl+Z` / `Ctrl+Y` | Undo or redo |
| `Ctrl++` / `Ctrl+-` / `Ctrl+0` | Zoom in, out, or reset |
| `Ctrl+9` | Fit the photo composition |
| `Ctrl+,` | Open Settings |
| `Delete` | Remove the selected image layer |
| `Space` | Play or pause video preview |
| `Esc` | Close Settings, cancel crop, or pause preview |

## Privacy

Hoshino has no account system, advertising SDK, telemetry service, or cloud media pipeline. Application settings and projects are stored locally. The optional crash-report setting is off by default and no crash-upload service is included in this beta.

## Build from source

Requirements:

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

```powershell
git clone https://github.com/Aseoriy/Hoshino-Editor.git
cd Hoshino-Editor
dotnet build
dotnet run --project .\HoshinoEditor.csproj
```

Pass a media file directly when launching:

```powershell
dotnet run --project .\HoshinoEditor.csproj -- "C:\Media\clip.mp4"
```

For speed-adjusted or upscaled video export in a source build:

```powershell
.\scripts\Get-Ffmpeg.ps1
dotnet build
```

The script downloads the FFmpeg essentials build to `Tools\ffmpeg.exe`. The binary is intentionally ignored by Git and is copied beside Hoshino during publishing.

## Build the installer

Install [Inno Setup 6](https://jrsoftware.org/isinfo.php), then run:

```powershell
.\scripts\Build-Installer.ps1
```

The self-contained application is written to `dist\win-x64`. The installer, FFmpeg source archive, and SHA-256 checksums are written to `dist\installer` for publication together.

## Project structure

- `Controls/` — photo, video, start, and settings workspaces
- `Models/` — settings, project, layer, and clip models
- `Services/` — editing, export, FFmpeg, settings, and Windows integration
- `Themes/` — shared WPF styling
- `installer/` — Inno Setup definition
- `scripts/` — FFmpeg, publish, and installer build scripts

## Contributing

Bug reports and focused pull requests are welcome. Before opening a pull request, build the project in Release mode and describe the Windows version and media format used for testing.

## License and third-party software

Hoshino Editor source code is available under the [MIT License](LICENSE). That license does not grant rights to the Hoshino Editor or Sail Solutions names and branding; see [TRADEMARKS.md](TRADEMARKS.md).

Release builds include separately licensed third-party components. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for details, including FFmpeg source and license information.
