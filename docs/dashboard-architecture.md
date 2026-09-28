# Dashboard structure

The dashboard is being migrated in stages. The running UI uses these page views in `DesktopApp/Views/Dashboard`:

| Page | View model | Existing interaction code |
| --- | --- | --- |
| Live TV | `LiveTvPageViewModel` | `DashboardWindow.LiveTv.cs` |
| Movies and series | `MoviesSeriesPageViewModel` | `DashboardWindow.MoviesSeries.cs` |
| Scheduler | `SchedulerPageViewModel` | `DashboardWindow.Scheduler.cs` |
| Settings | `SettingsPageViewModel` | Bound commands; `SettingsInteraction` handles Windows dialogs/processes |

`DashboardWindow` remains the shell. `DashboardNavigationViewModel.ActivePage` and `MoviesSeriesPageViewModel.ContentType` represent navigation explicitly. Button colors and control visibility are presentation outputs, not inputs to content-loading decisions. Viewport geometry still determines which thumbnails and guides to prioritize within the active page.

Page view models own catalog collections, collection views, selection state, and catalog request coordinators. Live TV, Movies/Series, and Scheduler currently inherit the shell's data context and bind to the appropriate page model. Their existing event handlers forward through the owning window while their implementations remain in focused partial files. `DashboardWindow.PageControls.cs` adapts their XAML namescopes for that existing code.

Settings binds directly to `SettingsPageViewModel`. The model owns the editable draft, validation messages, player choice, and commands for saving/restoring, browsing/testing players, EPG refresh, and cache clearing. It has no control lookups, window references, or direct access to static session state. `IApplicationSettingsService` supplies detached settings snapshots and persists a validated snapshot before updating the session. Explicit save failures propagate to the editor. The disk-cache checkbox retains its existing immediate behavior; loading the draft does not toggle it. `ISettingsInteraction` isolates filesystem checks, native dialogs, executable detection, and process launching. Tests replace these dependencies without opening dialogs or touching user settings.

The Settings view raises host events for favorites import/export and opening the shared cache inspector. It does not reference `DashboardWindow`. Navigation activates the settings editor through `SettingsPageModel.Load()`, including programmatic page changes. Log controls belong to the separate Logs page; their existing handlers are now in `DashboardWindow.Logs.cs`.

Continue the migration by moving one interaction at a time into a page-model command, binding the view to it, and removing its forwarding handler and control lookup. Scheduler dialogs and playback/recording interactions still use the compatibility path. Avoid creating a second copy of page state in the shell or adding new behavior to the legacy, unused `DashboardViewModel`.

Catalog parsing and preparation run in background work. UI collections use `BulkObservableCollection.ReplaceAll`, which emits one WPF-compatible reset. Keep cancellation and current-selection checks around result application; never mutate an already-bound model from background work.

`VirtualizationRegressionTests` loads the compiled page views and production templates, checks page-model bindings, and verifies bulk collection filtering/sorting. It also exercises settings commands, two-way form bindings, validation, save failures, canceled dialogs, and cache-clear failure/retry. `CatalogLoadingRegressionTests` exercises background preparation, request cancellation, thumbnails, and guide loading without a provider account.
