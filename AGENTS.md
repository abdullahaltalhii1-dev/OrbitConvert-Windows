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

## Historical Phase 1.1 scope

The following restrictions applied only to Phase 1.1 and are retained as history. Later owner-approved tasks supersede this section; the repository-wide development and safety rules above always apply.

Phase 1.1 is limited to the OrbitConvert Studio UI shell: a responsive header, preview navigation, workspace presentation, an explicitly fictional image demo, coming-soon pages, and in-memory theme and accent choices. Preserve the Phase 0 solution, MVVM binding, theme resource structure, tests, and Windows CI.

Do not add real file selection, drag-and-drop, filesystem access, metadata extraction, conversion, encoding, FFmpeg, Magick.NET, PDF tooling, Explorer integration, radial menus, installers, updaters, persistence, telemetry, cloud services, tray behavior, or global hooks. Disabled actions and fictional content must remain clearly labeled as previews or coming soon.

## Completion report

For each task, report files changed, dependencies and rationale, commands and results, Windows GUI verification, known limitations, and confirmation that later phases were not implemented.
