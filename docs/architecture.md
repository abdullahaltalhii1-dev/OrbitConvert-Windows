# Architecture

OrbitConvert Phase 0 is a deliberately small WPF application organized around MVVM.

## Projects

- `src/OrbitConvert.App` contains the Windows desktop executable, XAML views, theme resources, and view models.
- `tests/OrbitConvert.Tests` contains fast xUnit tests for presentation logic.

## UI composition

`MainWindow.xaml` owns presentation and binds its title and visible application name to `MainViewModel.AppTitle`. The code-behind contains only the generated window initialization required by WPF. Shared colors and styles live in separate resource dictionaries under `Themes` and are merged by `App.xaml`.

## Phase boundary

Phase 0 provides only a launchable shell. It contains no file selection, file inspection, conversion, Explorer integration, installer, updater, telemetry, or network features. Those capabilities require separately approved milestones.
