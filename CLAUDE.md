@AGENTS.md

## Tray app conventions

This is a tray-only WinUI 3 app built from the `winui-tray-app` skill scaffold on top of `winapp new`. Load that skill before changing app lifetime, the flyout, the tray icon, polling, settings, or secrets.

- Launch with `winapp run` (or `dotnet run`); never register the package by hand. Use `winapp run --clean` to test first-run behavior.
- `App.xaml.cs` owns the tray icon, single instance, and the only exit path (the Exit menu). There is no main window.
- The flyout (`Views/TrayFlyoutWindow`) is hidden on dismiss and closed after a minute hidden. Anything it starts, it must stop in `FlyoutPage.OnHidden` / `Dispose`.
- Nothing runs while the flyout is hidden: `FlyoutPage` re-reads the folder on show and watches it (`Services/FolderWatcher`) only while visible.
- No secrets: the app has no network access or keys. `SettingsService` holds the folder, recent folders, and the view options (JSON).
- Put pure decision logic (parsing, policies, formatting) in classes with no WinRT dependency, so a plain `net10.0` test project can cover it.

## Tray Folder specifics

- The core interaction is dragging files out of the list. `Services/FileActions.FillDragData` puts StorageItems in the DataPackage (Copy only); rows resolve their StorageItem ahead of time (thumbnail load, pointer press, selection) because drag events cannot await.
- Sort/filter/group logic is pure and lives in `Core/` (`FolderViewPolicy`, `FileKinds`), covered by `TrayFolder.Tests` (`dotnet test TrayFolder.Tests`).
- Pages swap inside `TrayFlyoutWindow.PageHost`: `FlyoutPage` (list), `DetailPage` (preview + info, disposed on back), `SettingsPage`.
- Shell and StorageFile calls run on a worker thread: made on the UI thread inside layout callbacks (ContainerContentChanging) they pump messages and XAML fails fast with a reentrancy error.
- Thumbnails come from `Services/ShellThumbnail` (IShellItemImageFactory via raw vtable calls). Keep it free of `[ComImport]`: trimmed Release builds drop built-in COM marshalling.
- The group header uses `{Binding}` on a `[Bindable]` `FileGroup`; an `x:Bind` group header crashes the XAML compiler (WMC9999 with no message).
