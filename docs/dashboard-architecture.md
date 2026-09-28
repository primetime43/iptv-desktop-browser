# Dashboard structure

The dashboard is being migrated in stages. The running UI uses these page views in `DesktopApp/Views/Dashboard`:

| Page | View model | Existing interaction code |
| --- | --- | --- |
| Live TV | `LiveTvPageViewModel` | `DashboardWindow.LiveTv.cs` |
| Movies and series | `MoviesSeriesPageViewModel` | `DashboardWindow.MoviesSeries.cs` |
| Movie/series details | `MediaDetailsViewModel` | `MediaDetailsView` bindings and playback request events |
| Scheduler | `SchedulerPageViewModel` | `DashboardWindow.Scheduler.cs` |
| New recording form | `RecordingFormViewModel` | `RecordingFormView` bindings; `RecordingFormInteraction` handles dialogs |
| Settings | `SettingsPageViewModel` | Bound commands; `SettingsInteraction` handles Windows dialogs/processes |

`DashboardWindow` remains the shell. `DashboardNavigationViewModel.ActivePage` and `MoviesSeriesPageViewModel.ContentType` represent navigation explicitly. Button colors and control visibility are presentation outputs, not inputs to content-loading decisions. Viewport geometry still determines which thumbnails and guides to prioritize within the active page.

Page view models own catalog collections, collection views, selection state, and catalog request coordinators. Live TV, Movies/Series, and Scheduler currently inherit the shell's data context and bind to the appropriate page model. Their existing event handlers forward through the owning window while their implementations remain in focused partial files. `DashboardWindow.PageControls.cs` adapts their XAML namescopes for that existing code.

Settings binds directly to `SettingsPageViewModel`. The model owns the editable draft, validation messages, player choice, and commands for saving/restoring, browsing/testing players, EPG refresh, and cache clearing. It has no control lookups, window references, or direct access to static session state. `IApplicationSettingsService` supplies detached settings snapshots and persists a validated snapshot before updating the session. Explicit save failures propagate to the editor. The disk-cache checkbox retains its existing immediate behavior; loading the draft does not toggle it. `ISettingsInteraction` isolates filesystem checks, native dialogs, executable detection, and process launching. Tests replace these dependencies without opening dialogs or touching user settings.

The Settings view raises host events for favorites import/export and opening the shared cache inspector. It does not reference `DashboardWindow`. Navigation activates the settings editor through `SettingsPageModel.Load()`, including programmatic page changes. Log controls belong to the separate Logs page; their existing handlers are now in `DashboardWindow.Logs.cs`.

`MoviesSeriesPageViewModel.Details` owns the single movie/series selection and its explicit empty/loading/ready/failed state. `MediaDetailsView` binds directly to that model, with no window lookup or per-title property-change subscriptions. The model uses `IVodService` and `SelectedDetailsLoader` for detached, guarded requests, shares repeated selections, and supplies a retry command. Content-type commands clear obsolete details; category changes, page departures, and window closure clear them through the existing shell lifecycle. Playback commands raise typed requests handled by the shell's existing player integration.

Details metadata, season headers, and episodes form one flat list with a WPF recycling panel. A single batch replaces its rows, and scrolling realizes only nearby episode controls. Keep this list out of an outer `ScrollViewer` or `StackPanel`, which would remove its bounded viewport. Episode commands reject stale rows from a previously selected series.

`SchedulerPageViewModel.NewRecording` owns the scheduling draft: explicit guide/custom mode, channel/program selection, dates and times, title, buffers, output path, validation, and commands. `RecordingFormView` binds directly to that model; its only pointer handlers preview a hovered program's airtime. Live indicators use a display wrapper and never alter the underlying EPG title. A browsed output path is retained while editing the draft. The draft resets only after successful submission; declined conflicts and failures preserve it.

`IRecordingScheduleService` adapts session stream URLs and the existing recording scheduler. Its guide path reuses `IChannelService` on a detached channel off the UI thread, or snapshots the local M3U guide. The form prepares and batches at most 50 upcoming programs. `LatestRequestLoader` cancels superseded loads and guards their application by active page, mode, and channel identity. Shell navigation activates/deactivates the form, including programmatic navigation and window closure. Conflict confirmation and file browsing are isolated in `IRecordingFormInteraction`, so regression tests never launch recorders or open dialogs.

Continue the migration by moving one interaction at a time into a page-model command, binding the view to it, and removing its forwarding handler and control lookup. Series-recording dialogs, existing-recording management, catalog loading, and player integration still use the compatibility path. Avoid creating a second copy of page state in the shell or adding new behavior to the legacy, unused `DashboardViewModel`.

Catalog parsing and preparation run in background work. UI collections use `BulkObservableCollection.ReplaceAll`, which emits one WPF-compatible reset. Keep cancellation and current-selection checks around result application; never mutate an already-bound model from background work.

`VirtualizationRegressionTests` loads the compiled page views and production templates, checks page-model bindings, and verifies bulk collection filtering/sorting. It also exercises settings commands and failure recovery, details request races/retries/cancellation, a compiled details view containing 10,000 episodes, and recording-form bindings, guide races, validation, conflicts, and overnight scheduling. `CatalogLoadingRegressionTests` exercises background preparation, request cancellation, thumbnails, and guide loading without a provider account.
