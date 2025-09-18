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
- **Session.cs**: Static session management with connection details, user info, and global settings
- **Models/**: Data models for channels, categories, EPG entries, VOD content, credentials, and user info
- **Views/**: Additional windows like credential manager and settings

### Key Architectural Patterns

- **Session Management**: Static `Session` class stores connection state, user preferences, and runtime data
- **Dual Mode Operation**:
  - Xtream mode: API-based with live EPG fetching
  - M3U mode: File/URL-based with optional XMLTV EPG
- **Credential Storage**: Uses Windows DPAPI for secure local credential encryption
- **External Player Integration**: Supports VLC, MPC-HC, MPV, and custom players
- **Recording**: FFmpeg integration for channel recording

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
- Collection views with filtering support for search functionality
- Async loading patterns with cancellation token support