# Third-party notices

Patterns bundles or optionally integrates the following third-party components:

| Component | License | Use |
|---|---|---|
| [Avalonia](https://avaloniaui.net) | MIT | UI framework |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | MIT | 2D rendering engine |
| [Inter typeface](https://rsms.me/inter/) | SIL Open Font License 1.1 | Embedded UI/overlay font (`src/Patterns.Core/Assets/Inter-LICENSE-OFL.txt`) |
| [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) | LGPL-2.1 | Managed bindings for optional video playback |

Runtime integrations that are **not** bundled and are loaded only when the user provides them:

- **libVLC / VLC** (LGPL/GPL, VideoLAN) — video decoding. Install 64-bit VLC or place a
  `libvlc` folder beside `Patterns.exe`.
- **NDI® runtime** (NewTek/Vizrt license) — NDI output. Install the free NDI runtime from
  [ndi.video](https://ndi.video) or place `Processing.NDI.Lib.x64.dll` beside `Patterns.exe`.
  NDI® is a registered trademark of Vizrt NDI AB.
