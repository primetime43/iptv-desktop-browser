# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is an IPTV Desktop Browser - a Windows WPF application built with .NET 9 that provides a clean interface for browsing IPTV channel lists from multiple sources:

- **Xtream Codes portals** (player_api/panel_api endpoints)
- **M3U/M3U8 playlists** (remote URLs or local files)
- **XMLTV EPG support** for program data
- **Video on Demand (VOD)** browsing and playback for Xtream services

## Architecture

### Core Components

- **MainWindow.xaml.cs**: Main login window that handles credential management, API endpoint discovery, and M3U playlist loading
- **DashboardWindow.xaml.cs**: Primary interface showing categories, channels, EPG data, VOD content, and player controls
- **VodWindow.xaml.cs**: Dedicated Video on Demand interface with category browsing and poster display
- **Session.cs**: Static session management with connection details, user info, and global settings
- **Models/**: Data models including Channel, Category, VodContent, EpgEntry, CredentialStore, RecordingManager, and user preferences
- **Services/**: Business logic layer with HttpService, ChannelService, VodService, CacheService, and SessionService
- **Views/**: Additional windows including CredentialManagerWindow, SettingsWindow, RecordingStatusWindow, and AboutWindow
- **ViewModels/**: MVVM pattern implementation for UI data binding and state management
- **Converters/**: WPF value converters for data transformation in UI binding

### Key Architectural Patterns

- **Session Management**: Static `Session` class stores connection state, user preferences, and runtime data
- **Service Layer Architecture**: Dependency injection with IChannelService, IVodService, ICacheService, IHttpService, and ISessionService
- **MVVM Pattern**: ViewModels handle UI logic and data binding, separating concerns from code-behind
- **Dual Mode Operation**:
  - Xtream mode: API-based with live EPG fetching
  - M3U mode: File/URL-based with optional XMLTV EPG
- **Credential Storage**: Uses Windows DPAPI for secure local credential encryption via CredentialStore
- **Caching Strategy**: Multi-layer caching with CacheService and PersistentCacheService for performance
- **External Player Integration**: Supports VLC, MPC-HC, MPV, and custom players via template system
- **Recording System**: FFmpeg integration with RecordingManager and ScheduledRecording support

### Data Flow

1. **Login Flow**: MainWindow validates credentials → populates Session → opens DashboardWindow
2. **Channel Loading**: DashboardWindow loads categories → loads channels per category → loads EPG data
3. **VOD Loading**: DashboardWindow loads VOD categories → loads VOD content per category → loads poster images
4. **Playback**: Double-click channel/VOD → builds stream URL → launches external player
5. **Recording**: Uses FFmpeg with configurable arguments and output directory

## Development Commands

### Building
```bash
# Build the solution
dotnet build DesktopApp/DesktopApp.csproj

# Build for release
dotnet build DesktopApp/DesktopApp.csproj -c Release

# Publish framework-dependent (requires .NET 9 runtime)
dotnet publish DesktopApp/DesktopApp.csproj -c Release -f net9.0-windows -r win-x64 --self-contained false /p:PublishSingleFile=true -o publish/win-x64-fdd

# Publish self-contained (includes runtime)
dotnet publish DesktopApp/DesktopApp.csproj -c Release -f net9.0-windows -r win-x64 --self-contained true /p:PublishSingleFile=true -o publish/win-x64-sc
```

### Running
```bash
# Run from source
dotnet run --project DesktopApp/DesktopApp.csproj

# Run specific configuration
dotnet run --project DesktopApp/DesktopApp.csproj -c Release
```

### Development Setup
- Requires Visual Studio 2022+ with .NET 9 SDK
- Windows 10/11 (x64) target platform
- WPF application framework
- No test framework configured - this is a desktop application without unit tests

## Key Technical Details

### Version Management
- Version is set in `DesktopApp.csproj` via `<Version>`, `<AssemblyVersion>`, etc.
- Auto-update checking against GitHub releases API
- Version display and comparison logic in MainWindow.xaml.cs

### Credential Security
- `CredentialStore` class uses Windows DPAPI for encryption
- Credentials stored per-user, accessible only to the current Windows user
- Supports multiple stored profiles via `CredentialManagerWindow`

### EPG (Electronic Program Guide)
- Xtream mode: Fetches current/upcoming programs via `get_simple_data_table` API
- M3U mode: Parses XMLTV files for program data
- EPG data is cached and refreshed on configurable intervals
- Base64 decoding support for API responses

### External Player Integration
- `Session.BuildPlayerProcess()` creates ProcessStartInfo for different players
- Template-based argument system with `{url}` and `{title}` placeholders
- Fallback path resolution (VlcPath property for backward compatibility)

### Recording System
- FFmpeg-based recording with configurable arguments
- Template system supports `{url}`, `{output}`, and `{title}` tokens
- Automatic filename sanitization and timestamp generation
- Recording status management via `RecordingManager` singleton

### Video on Demand (VOD) System
- Xtream API integration via `get_vod_categories` and `get_vod_streams` endpoints
- Category-based browsing with dropdown selection in VOD view
- Poster image loading and caching for movie artwork
- `VodContent` model with metadata: plot, cast, director, genre, rating, duration, etc.
- `Session.BuildVodStreamUrl()` constructs movie stream URLs with proper container extensions
- Search functionality across movie titles, genres, and plot descriptions

## Common Workflows

### Adding New Player Support
1. Add new `PlayerKind` enum value in Session.cs
2. Update `BuildPlayerProcess()` method with default executable and arguments
3. Update Settings UI to include new player option

### API Endpoint Modifications
- Xtream API calls are built via `Session.BuildApi()` method
- Live stream URLs via `Session.BuildStreamUrl()` method
- VOD stream URLs via `Session.BuildVodStreamUrl()` method
- All HTTP requests use the shared `HttpClient _http` instance

### UI State Management
- Most windows implement `INotifyPropertyChanged` for data binding
- ViewModels use CommunityToolkit.Mvvm for MVVM pattern implementation
- Collection views with filtering support for search functionality
- Async loading patterns with cancellation token support
- Dependency injection configured in App.xaml.cs with Microsoft.Extensions.DependencyInjection

### Favorites System
- `FavoritesStore` manages per-account/playlist favorite channels
- Favorites persist between sessions and maintain EPG data
- Star button UI for adding/removing favorites in channel lists
- Dedicated "⭐ Favorites" category in category dropdown