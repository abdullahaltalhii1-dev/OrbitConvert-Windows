# OrbitConvert repository instructions

## Product and stack

OrbitConvert is an incremental Windows desktop application built with C#, .NET 10, WPF/XAML, MVVM, and xUnit.

## Development rules

1. Implement only the scope in the currently approved task. Never start the next phase automatically.
2. Keep changes small and reviewable. Add abstractions or dependencies only when the current feature needs them.
3. Keep presentation in XAML and state or behavior in view models. Restrict code-behind to UI and platform concerns.
4. Never alter, replace, or delete user files without explicit approval for that operation.
5. Do not add telemetry, analytics, accounts, cloud conversion, or unnecessary network access.
6. Never simulate unfinished functionality. Disable it or label it `Coming soon` when a future task requires it to be visible.
7. Add or update real tests for meaningful logic. Report exactly which build, test, and GUI checks ran.
8. Preserve the Windows build and test workflow. Do not merge a phase or begin another phase without owner approval.
9. Use original OrbitConvert design and branding. Do not copy Tangerine code, assets, screenshots, or trademarks.
10. Favor accessible controls, keyboard navigation, robust error handling, and straightforward documentation.

## Approved Phase 0 scope

Phase 0 is limited to the repository and WPF shell foundation: a minimal startup window, a bound application title, basic theme resources, genuine view-model tests, Windows CI, and documentation.

Do not add file selection, drag-and-drop, file lists, file inspection, converters, FFmpeg, PDF tooling, Explorer integration, radial menus, installers, updaters, tray behavior, global hooks, settings pages, or polished Phase 1 UI.

## Completion report

For each task, report files changed, dependencies and rationale, commands and results, Windows GUI verification, known limitations, and confirmation that later phases were not implemented.
