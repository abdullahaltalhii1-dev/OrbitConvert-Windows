# Architecture

OrbitConvert is a deliberately small WPF application organized around MVVM. Phase 1.3 adds maintainable English and Arabic localization to the safe file-intake Studio shell. The first Phase 2A checkpoint adds only an isolated, non-UI image inspection boundary.

## Projects

- `src/OrbitConvert.App` contains the Windows desktop executable, XAML views, theme resources, and view models.
- `tests/OrbitConvert.Tests` contains fast xUnit tests for presentation logic.

## UI composition

`MainWindow.xaml` binds its title to `MainViewModel.AppTitle` and stays focused on shell composition. The `Views` folder contains small `UserControl` components for the header, sidebar, workspace, fictional image demo, settings, and a reusable coming-soon page. These controls inherit the window data context, so navigation and preview commands remain centralized rather than duplicated.

`MainViewModel` owns session-only navigation, theme, accent, fictional image preview state, and language selection. It exposes a focused `FileIntakeViewModel`, which owns the observable selection, Windows-style duplicate prevention, logical status state, and add/remove/clear commands. Dynamic status text is resolved at display time, so an existing message updates when the language changes.

`LocalizationService` resolves stable keys from `Resources/Strings.resx` and `Resources/Strings.ar.resx`, publishes the current culture and flow direction, and falls back to English when an Arabic key is missing. Views bind to the service indexer, allowing one XAML tree to refresh at runtime. The root content grid follows LTR or RTL direction; paths, filenames, format labels, and vector icons opt into LTR where mirroring would be incorrect.

`FileLanguagePreferenceStore` persists only the `en` or `ar` code in `%LOCALAPPDATA%\OrbitConvert\language.txt`. A missing, unreadable, or invalid value safely falls back to English. Selected files, theme, and accent remain in memory only.

`FileMetadataProvider` uses filesystem metadata APIs to obtain only the normalized path, name, extension, and byte length. It never opens or parses file content. Its small interface keeps missing, inaccessible, invalid, and directory cases testable without coupling the view model to Windows dialogs.

`IImageEngine` is the narrow boundary for the Phase 2A image foundation. `MagickImageEngine` uses the pinned Magick.NET Q8 package to decode a caller-provided stream and report its format, dimensions, bit depth, and alpha presence. Expected empty, corrupt, and unreadable inputs return explicit failures. The service is not connected to file intake or WPF and has no output or conversion API.

`WorkspaceView` code-behind contains only the native `OpenFileDialog` boundary and WPF drag/drop event translation. It forwards paths to the file-intake commands; all selection behavior remains outside the view.

The window code-behind translates the view model's theme and accent choices into dynamic WPF resources. It also asks the supported Windows DWM API to match the native title bar to the selected theme while retaining standard window controls and gracefully keeping the system default when the attribute is unavailable. This code is intentionally UI-specific, contains no product logic, and does not persist values.

## Phase boundary

Phase 2A preserves the localized shell and adds image inspection only behind an isolated service. File intake still reads basic filesystem metadata; folders are rejected without scanning. The Images page remains a fictional, isolated demo and Convert remains disabled. The application contains no user-file image decoding, conversion, output generation, selected-file persistence, Explorer integration, installer, updater, telemetry, or network features. Those capabilities require separately approved milestones.
