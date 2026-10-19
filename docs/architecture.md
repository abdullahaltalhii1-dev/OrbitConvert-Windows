# Architecture

OrbitConvert is a deliberately small WPF application organized around MVVM. Phase 1.1 adds the Studio UI shell without adding file or conversion infrastructure.

## Projects

- `src/OrbitConvert.App` contains the Windows desktop executable, XAML views, theme resources, and view models.
- `tests/OrbitConvert.Tests` contains fast xUnit tests for presentation logic.

## UI composition

`MainWindow.xaml` binds its title to `MainViewModel.AppTitle` and stays focused on shell composition. The `Views` folder contains small `UserControl` components for the header, sidebar, workspace, fictional image demo, settings, and a reusable coming-soon page. These controls inherit the window data context, so navigation and preview commands remain centralized rather than duplicated.

`MainViewModel` owns session-only navigation, theme, accent, and fictional image preview state. Small commands update that state without touching the filesystem. Shared colors and reusable control styles live in resource dictionaries under `Themes` and are merged by `App.xaml`.

The window code-behind translates the view model's theme and accent choices into dynamic WPF resources. It also asks the supported Windows DWM API to match the native title bar to the selected theme while retaining standard window controls and gracefully keeping the system default when the attribute is unavailable. This code is intentionally UI-specific, contains no product logic, and does not persist values.

## Phase boundary

Phase 1.1 provides only an interactive visual shell. The image shown in the Images page is a vector illustration with fictional metadata. The application contains no file selection, drag-and-drop, file inspection, conversion, Explorer integration, installer, updater, persistence, telemetry, or network features. Those capabilities require separately approved milestones.
