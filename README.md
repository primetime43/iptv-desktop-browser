# IPTV Desktop Browser

A **lightweight Windows desktop IPTV browser** built with **WPF / .NET 9** for fast, clean, and secure access to IPTV channel lists.

v1.06/v2.0.0
<img width="1920" height="1032" alt="Screenshot 2025-09-21 235704" src="https://github.com/user-attachments/assets/0c2eaa27-7071-404f-b843-3233ca513ff8" />
<img width="1920" height="1032" alt="Screenshot 2025-09-21 235726" src="https://github.com/user-attachments/assets/75c7c5d7-7a4f-45f2-82b5-7b0c8d67e472" />


## v1.0.1
<details>
<summary>Click to expand screenshots</summary>
<img width="1186" height="593" alt="image" src="https://github.com/user-attachments/assets/fed90a5e-31d8-4fac-b715-fd1b1514fda7" />
<img width="1920" height="1032" alt="image" src="https://github.com/user-attachments/assets/dd423c91-dd90-4508-af42-6b285e6c2f82" />
</details>


---

## 🚀 Features

- **Multi-source support**
  - **Xtream Codes portals** (`player_api` / `panel_api`)
  - **M3U / M3U8 playlists** (remote URL or local file)
  - **XMLTV (EPG)** for program data (optional)
  - **VOD support** - Video on Demand browsing and playback
- **Smart connection handling**
  - Automatic Xtream endpoint detection
  - Credential Manager with secure storage via Windows DPAPI
- **Modern, fast UI**
  - Channel list with grouping (`group-title`)
  - **Favorites system** - save preferred channels per account/playlist
  - Grid-style EPG with per-channel timelines
  - External player integration (VLC, MPC-HC, MPV, custom)
  - HLS playback quality selection using provider-advertised resolutions and bitrates
- **Performance & extras**
  - **High-speed channel loading** with optimized caching
  - Channel recording with FFmpeg integration
  - Connection diagnostics with raw request/response logging
  - **Smart caching system** for faster data access

---

## 🛠 Installation

1. Go to the [**Releases**](https://github.com/primetime43/iptv-desktop-browser/releases) page.
2. Download the latest release:
   - **Framework-dependent**: `...framework-dependent-win-x64.zip`
3. Install [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) if not already installed.
4. Unzip and run the executable.

---

## 📖 Usage

### **Xtream Login**
1. Select **Xtream Login** mode.
2. Enter:
   - Host (or full URL)
   - Port
   - Username
   - Password  
   *(toggle SSL if needed)*
3. Click **Login** → Dashboard opens.
4. Optionally, save credentials for future sessions.

### **M3U Playlist**
1. Switch to **M3U Playlist** mode.
2. Paste a playlist URL or select a `.m3u`/`.m3u8` file.
3. (Optional) Add XMLTV URL or file for EPG.
4. Click **Load Playlist**.

### **Playback quality**

- For Live TV, select a channel and click **Play with quality…** in its information panel.
- For movies, click **Quality…** in the details panel. Episode rows also have a **Quality…** button.
- Choose an advertised resolution/bitrate, or **Automatic** to let the player use the original/master stream. Normal Play and double-click playback start immediately without quality discovery.
- Xtream live channels are checked through their HLS (`.m3u8`) endpoint. Playlist channels, movies, and episodes are checked at their supplied stream URL. Direct TS/MP4 streams and providers without advertised alternatives remain playable at their original quality.
- Fixed HLS quality selection preserves associated audio, subtitles, and encryption-key references. Keep the app open while using this mode: it supplies a small in-memory playlist to the external player, which downloads the actual media directly from the provider. No credential-bearing playlist is saved to disk.
- This feature selects existing HLS variants; it does not transcode, combine separately listed SD/HD channels, or select DASH representations. Playlists requiring HLS variable substitution or content steering use Automatic instead.

### **Favorites**
- Click the ⭐ star button on any channel to add/remove from favorites.
- Access favorites by selecting **⭐ Favorites** from the categories dropdown.
- Favorites are saved per account/playlist and persist between sessions.
- Favorite channels maintain all EPG data and functionality.
- Xtream “Now playing” is recalculated from the loaded schedule every 10 seconds. Guide gaps clear the label, and expired schedules refresh automatically. Older snapshot-only EPG caches are replaced when loaded.
- Images and Xtream guides load for the selected item first, then visible cards, then nearby cards. A shared queue limits loading to four active jobs and 128 retained requests, cancels obsolete viewport work, and shares duplicate requests. “Now playing” and upcoming programs use the same guide.

### Scheduled recording failures
- Failed or prematurely ended recordings show **Failed** immediately, with a notification.
- Hover over the status or open **Props** to see the FFmpeg exit code and recent error output. Error details redact credentials and remain available after restarting the app.
- Scheduled stops let FFmpeg finalize the output. Forced termination is reported as a failure; user cancellation remains **Cancelled**.
- Active scheduled recordings continue through logout and account switches while the app remains open. Their stop times, buffers, and results stay tied to the original account; switching back reconnects the list to the existing recording.

---

## 🔒 Security & Privacy

- Login profile passwords are saved **only if you choose to remember them**, encrypted using Windows DPAPI (per-user).
- Scheduled and series recordings retain the stream URLs needed to record. Their files are encrypted with Windows DPAPI and can only be decrypted by the same Windows user. Existing plaintext recording files are migrated when first loaded, including records for other IPTV accounts.
- Diagnostics redact URLs and credentials; login response bodies and headers are omitted. Previously saved or exported logs are not modified.
- No telemetry, no analytics.  
  Network traffic is limited to:
  - Your IPTV endpoints
  - GitHub (for update checks)

---

## 🧰 Development

### Prerequisites
- [Visual Studio 2022+](https://visualstudio.microsoft.com/) with **.NET 9 SDK**
- Windows 10/11 (x64)

### Build Steps
```bash
git clone https://github.com/primetime43/iptv-desktop-browser.git
cd iptv-desktop-browser
dotnet build
```

Run the Windows regression checks (also run in CI):
```bash
dotnet run --project Tests/SecurityRegressionTests/SecurityRegressionTests.csproj -c Release
dotnet run --project Tests/RecordingRegressionTests/RecordingRegressionTests.csproj -c Release
dotnet run --project Tests/EpgRegressionTests/EpgRegressionTests.csproj -c Release
dotnet run --project Tests/UrlRegressionTests/UrlRegressionTests.csproj -c Release
dotnet run --project Tests/CategoryRegressionTests/CategoryRegressionTests.csproj -c Release
dotnet run --project Tests/VirtualizationRegressionTests/VirtualizationRegressionTests.csproj -c Release
dotnet run --project Tests/CatalogLoadingRegressionTests/CatalogLoadingRegressionTests.csproj -c Release
dotnet run --project Tests/VodRegressionTests/VodRegressionTests.csproj -c Release
```

See [dashboard architecture](docs/dashboard-architecture.md) for the page split and incremental view-model migration.
