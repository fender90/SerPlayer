# SER Player

Play .ser astronomy videos on Windows, frame by frame. By [Vesperis Labs](https://www.vesperislabs.com).

SER is the raw video format written by planetary and solar capture software such as FireCapture and SharpCap. SER Player opens these captures directly, without converting them first.

## Download

Get the installer from the [latest release](https://github.com/fender90/SerPlayer/releases/latest) and run it. It installs for you alone or for everyone on the PC, adds SER Player to "Open with" for .ser files, and uninstalls from Settings > Apps.

Requires Windows 10 or 11 (64-bit). See [CHANGELOG.md](CHANGELOG.md) for what's new.

## Features

- Opens mono, Bayer (RGGB, GRBG, GBRG, BGGR) and RGB captures, 8 to 16 bits per sample, including large multi-gigabyte files
- Plays at the frame rate recorded in the file's timestamps, and shows each frame's UTC time
- Step frame by frame (arrow keys or mouse wheel), seek, skip 10 s, loop
- Zoom up to 64x with Ctrl + / -, Ctrl + wheel or pinch, and drag to pan; pixels stay sharp when magnified
- Brightness (gamma) slider for faint detail
- Debayer preview: Auto from the file header, Off (raw sensor data), or force a Bayer pattern
- Fullscreen, drag and drop, and controls that fade away while you watch

## Building

Needs the .NET 8 SDK. From PowerShell:

```powershell
.\build.ps1
```

This writes the app to `publish\SerPlayer.exe` and the installer to `dist\SerPlayer-Setup-<version>.exe`. The installer is the same self-contained exe (the .NET runtime is bundled); the "Setup" in its name switches it to the install wizard. The version is set in [Directory.Build.props](Directory.Build.props).

Tests live in `tests\Harness`, a separate console project: build it, then run `harness.exe decode brand`.

## License

SER Player is © 2026 Vesperis Labs and licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). The SER Player and Vesperis Labs names and logos are not covered by that license; see [LICENSE.txt](LICENSE.txt).

See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for the other components and their licenses.
