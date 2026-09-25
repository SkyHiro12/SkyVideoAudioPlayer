<p align="center">
  <b>English</b> | <a href="README.ru.md">Русский</a>
</p>

# 🎬 SkyPlayer (FluentPlayer)

<p align="center">
  <img src="icon.ico" width="96" height="96" alt="SkyPlayer Logo" />
</p>

<p align="center">
  <b>Next-generation, lightweight media player for Windows 10 & 11</b><br/>
  Engineered with <b>WinUI 3</b>, <b>Windows App SDK</b>, and <b>.NET 8</b> with native Fluent Design.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D4?logo=windows&logoColor=white" alt="Platform" />
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white" alt=".NET 8" />
  <img src="https://img.shields.io/badge/UI-WinUI%203-0078D4?logo=windows" alt="WinUI 3" />
  <img src="https://img.shields.io/badge/Arch-x64-informational" alt="Architecture" />
  <img src="https://img.shields.io/badge/License-MIT-success" alt="License" />
</p>

<p align="center">
  <a href="https://github.com/SkyHiro12/SkyVideoAudioPlayer/releases/latest">
    <img src="https://img.shields.io/badge/Download-Latest%20Release-brightgreen?style=for-the-badge&logo=windows" alt="Download SkyPlayer" />
  </a>
</p>

---

## ✨ Features

### 🎨 Native Fluent Design
- Translucent **Acrylic** and **Mica** backdrops with subtle micro-animations and rounded controls.
- Three built-in themes: **Dark**, **Light**, and vibrant **Ultraviolet** with adaptive timeline gradients.
- Automatic control bar and cursor auto-hiding during playback.

### ⏱️ Interactive Precision Timeline
- **Instant Click-to-Seek**: Click anywhere on the track to jump precisely to that timestamp.
- **Hover Preview Badge**: A floating badge smoothly tracks your mouse above the timeline with no boundary clipping.
- **Visual Hover Scrubbing**: Live illuminated track segment showing the preview range up to your cursor.
- Download / network buffer progress bar with easy switching between remaining and total duration.

### 🔍 Dynamic UI Scaling (70% - 200%)
- Smooth slider with fine **5% increments** and real-time percentage readout.
- Quick preset buttons: `75%`, `90%`, `100%`, `125%`, `150%`, `200%`.
- Keyboard shortcuts: `Ctrl` + `+` / `Ctrl` + `-` and mouse wheel zoom (`Ctrl` + Wheel).
- One-click instant reset to default (`Ctrl` + `0`).
- The settings overlay is shielded from scaling out of view, ensuring control elements are always accessible.

### 💬 Intelligent Subtitle Engine
- Broad format compatibility: `.srt`, `.ass`, `.ssa`, and `.vtt`.
- Embedded subtitle stream extraction directly from MP4, MOV, and MKV containers.
- Automatic detection and loading of subtitle files from the video directory.
- Subtitle font size is independent of the UI scale.
- Adaptive positioning: Subtitles float cleanly above control bars when visible, then glide down to the standard reading position when controls auto-hide.
- Real-time subtitle sync offset adjustment ($\pm 5$ seconds).

### 🎵 Dedicated Music & Audio Player Mode
- Instant recognition of audio files (`.mp3`, `.flac`, `.wav`, `.aac`, `.m4a`, `.ogg`, `.opus`).
- Automatic ID3 / metadata tag extraction (Track title, Artist, Album).
- Embedded album art display with an animated vinyl record placeholder fallback.

### 📦 Clean & Portable Package
- Clean root folder with **only a single `SkyPlayer.exe` executable**.
- All 200+ runtime DLLs, DirectX assets, and dependencies are neatly isolated in an `app/` subfolder.
- Fully portable — run from anywhere without complex installers.

---

## ⌨️ Keyboard Shortcuts

| Shortcut | Action |
| :--- | :--- |
| <kbd>Space</kbd> / <kbd>K</kbd> | Play / Pause |
| <kbd>←</kbd> / <kbd>J</kbd> | Skip backward (configurable: 5s, 10s, 15s, 30s) |
| <kbd>→</kbd> / <kbd>L</kbd> | Skip forward |
| <kbd>↑</kbd> / <kbd>↓</kbd> | Volume Up / Down ($\pm 5\%$) |
| <kbd>M</kbd> | Toggle Mute |
| <kbd>F</kbd> / <kbd>F11</kbd> | Toggle Fullscreen |
| <kbd>[</kbd> / <kbd>]</kbd> | Playback speed ($\pm 0.25x$) |
| <kbd>,</kbd> / <kbd>.</kbd> | Step backward / forward one frame ($\pm 0.04s$) |
| <kbd>Ctrl</kbd> + <kbd>+</kbd> | Zoom UI in (+5%) |
| <kbd>Ctrl</kbd> + <kbd>-</kbd> | Zoom UI out (-5%) |
| <kbd>Ctrl</kbd> + <kbd>0</kbd> | Reset UI scale to 100% |
| <kbd>Ctrl</kbd> + <kbd>Wheel</kbd> | Smooth UI zoom with mouse wheel |
| <kbd>S</kbd> | Open / close Settings overlay |
| <kbd>Esc</kbd> | Dismiss settings / exit fullscreen |

---

## 🛠️ Building from Source

### Prerequisites
* Windows 10 (Build 19041+) or Windows 11
* [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
* Platform: **x64**

### Build Debug
```powershell
dotnet build -p:Platform=x64
```

### Build Clean Portable Release
```powershell
# 1. Publish all player dependencies to app/ subfolder
dotnet publish -c Release -p:Platform=x64 -o "Release\app"

# 2. Compile lightweight launcher to Release root
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /target:winexe /win32icon:"icon.ico" /out:"Release\SkyPlayer.exe" "scratch\Launcher.cs"
```

The resulting `Release` folder will contain only `SkyPlayer.exe` and the `app/` folder.

---

## 📄 License

This project is open-source software licensed under the **MIT License**. See the [LICENSE](LICENSE) file for details.
