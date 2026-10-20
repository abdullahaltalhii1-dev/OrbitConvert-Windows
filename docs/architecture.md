# Architecture

OrbitConvert is a deliberately small WPF application organized around MVVM. Phase 1.2 adds safe file intake to the approved Studio UI shell without adding conversion infrastructure.

## Projects

- `src/OrbitConvert.App` contains the Windows desktop executable, XAML views, theme resources, and view models.
- `tests/OrbitConvert.Tests` contains fast xUnit tests for presentation logic.

## UI composition

`MainWindow.xaml` binds its title to `MainViewModel.AppTitle` and stays focused on shell composition. The `Views` folder contains small `UserControl` components for the header, sidebar, workspace, fictional image demo, settings, and a reusable coming-soon page. These controls inherit the window data context, so navigation and preview commands remain centralized rather than duplicated.

`MainViewModel` owns session-only navigation, theme, accent, and fictional image preview state. It exposes a focused `FileIntakeViewModel`, which owns the observable selection, Windows-style duplicate prevention, status messages, and add/remove/clear commands.

`FileMetadataProvider` uses filesystem metadata APIs to obtain only the normalized path, name, extension, and byte length. It never opens or parses file content. Its small interface keeps missing, inaccessible, invalid, and directory cases testable without coupling the view model to Windows dialogs.

`WorkspaceView` code-behind contains only the native `OpenFileDialog` boundary and WPF drag/drop event translation. It forwards paths to the file-intake commands; all selection behavior remains outside the view.

The window code-behind translates the view model's theme and accent choices into dynamic WPF resources. It also asks the supported Windows DWM API to match the native title bar to the selected theme while retaining standard window controls and gracefully keeping the system default when the attribute is unavailable. This code is intentionally UI-specific, contains no product logic, and does not persist values.

## Phase boundary

Phase 1.2 accepts individual files and reads basic filesystem metadata. Folders are rejected without scanning. The Images page remains a fictional, isolated demo. The application contains no file-content parsing, conversion, output generation, persistence, Explorer integration, installer, updater, telemetry, or network features. Those capabilities require separately approved milestones.
