# Dashboard structure

The dashboard is being migrated in stages. The running UI uses these page views in `DesktopApp/Views/Dashboard`:

| Page | View model | Existing interaction code |
| --- | --- | --- |
| Live TV | `LiveTvPageViewModel` | `DashboardWindow.LiveTv.cs` |
| Movies and series | `MoviesSeriesPageViewModel` | `DashboardWindow.MoviesSeries.cs` |
| Scheduler | `SchedulerPageViewModel` | `DashboardWindow.Scheduler.cs` |
| Settings | `SettingsPageViewModel` | `DashboardWindow.Settings.cs` |

`DashboardWindow` remains the shell. `DashboardNavigationViewModel.ActivePage` and `MoviesSeriesPageViewModel.ContentType` represent navigation explicitly. Button colors and control visibility are presentation outputs, not inputs to content-loading decisions. Viewport geometry still determines which thumbnails and guides to prioritize within the active page.

Page view models own catalog collections, collection views, selection state, and catalog request coordinators. Each extracted view inherits the shell's data context and binds to the appropriate page model. Existing event handlers forward through the owning window while their implementations remain in focused partial files. `DashboardWindow.PageControls.cs` adapts the new XAML namescopes for that existing code.

Continue the migration by moving one interaction at a time into a page-model command, binding the view to it, and removing its forwarding handler and control lookup. Settings form validation, scheduler dialogs, and playback/recording interactions still use the compatibility path. Avoid creating a second copy of page state in the shell or adding new behavior to the legacy, unused `DashboardViewModel`.

Catalog parsing and preparation run in background work. UI collections use `BulkObservableCollection.ReplaceAll`, which emits one WPF-compatible reset. Keep cancellation and current-selection checks around result application; never mutate an already-bound model from background work.

`VirtualizationRegressionTests` loads the compiled page views and production templates, checks page-model bindings, and verifies bulk collection filtering/sorting. `CatalogLoadingRegressionTests` exercises background preparation, request cancellation, thumbnails, and guide loading without a provider account.
