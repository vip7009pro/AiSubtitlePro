# CONTEXT

- Solution: AiSubtitlePro WPF app with projects `AiSubtitlePro`, `AiSubtitlePro.Core`, and `AiSubtitlePro.Infrastructure`.
- Current features: 
  - Video editing UI/FFmpeg workflow via **Edit Video** feature
  - Video cutting (Cut Video)
  - Subtitle management (SRT, ASS, VTT formats)
- **NEW (Latest)**: Complete video editing pipeline with 9 configurable steps:
  1. Remove metadata
  2. Change aspect ratio (1:1, 4:5, 9:16, 16:9)
  3. Horizontal flip
  4. Speed adjustment (0.5x - 2.0x)
  5. Color adjustment (brightness, contrast, saturation)
  6. Audio pitch shift (-12 to +12 semitones)
  7. Background music replacement (optional)
  8. Re-encode video (H.264 or H.265)
  9. Add text watermark (optional)
- Files created:
  - `Services/VideoObfuscator.cs` - FFmpeg wrapper for video processing
  - `ViewModels/EditVideoViewModel.cs` - ViewModel for Edit Video window
  - `Views/EditVideoWindow.xaml` - UI for video editing
  - `Views/EditVideoWindow.xaml.cs` - Code-behind
  - Added `EditVideoCommand` to MainViewModel
  - Enhanced `Converters/ValueConverters.cs` with `DoubleFormatConverter`
  - Added "Edit Video..." menu item in File menu
- Runtime fix note: `EditVideoWindow` now assigns its `DataContext` in code-behind and `EditVideoViewModel` has a true parameterless constructor to avoid XAML load-time constructor binding crashes.
- UI contrast fix note: the Edit Video content area now uses a light foreground so text is readable on the dark editing panels while keeping the light header unchanged.
- Thread-safety fix note: `EditVideoViewModel` captures `Application.Current.Dispatcher` and uses it to marshal `LogMessages` updates onto the UI thread, preventing ItemContainerGenerator inconsistency errors when FFmpeg emits log events from background threads.
- Final runtime fix note: `EditVideoViewModel` now marshals log updates through the captured application dispatcher, and the Edit Video log `ListBox` has virtualization disabled to prevent WPF generator inconsistencies during rapid collection updates.
- Sync fix note: the pitch-shift step in `VideoObfuscator` now uses `asetrate` plus inverse `atempo` and invariant-culture numeric formatting so audio duration stays aligned with video while changing pitch.
- Safety note: do not implement features intended to evade copyright detection or bypass rights-management systems.
- Safety: All video editing operations are legally compliant (no DRM bypass, no illegal fingerprint alteration).
