# IPTV Desktop Browser

A **lightweight Windows desktop IPTV browser** built with **WPF / .NET 9** for fast, clean, and secure access to IPTV channel lists.

v1.0.1
![App Screenshot v1.0.1](https://github-production-user-asset-6210df.s3.amazonaws.com/12754111/483490819-3dc59bf2-009c-4414-a0d0-33ab431a7535.png?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=AKIAVCODYLSA53PQK4ZA%2F20250829%2Fus-east-1%2Fs3%2Faws4_request&X-Amz-Date=20250829T065056Z&X-Amz-Expires=300&X-Amz-Signature=b94f241aaed8bb4e004d069c49d35614c491242c41cb415acb5f4e56aa721138&X-Amz-SignedHeaders=host)  


---

## 🚀 Features

- **Multi-source support**
  - **Xtream Codes portals** (`player_api` / `panel_api`)
  - **M3U / M3U8 playlists** (remote URL or local file)
  - **XMLTV (EPG)** for program data (optional)
- **Smart connection handling**
  - Automatic Xtream endpoint detection
  - Credential Manager with secure storage via Windows DPAPI
- **Modern, fast UI**
  - Channel list with grouping (`group-title`)
  - Grid-style EPG with per-channel timelines
  - External player integration
- **Extras**
  - Channel recording with status manager
  - Connection diagnostics with raw request/response logging
- **Flexible builds**
  - **Self-contained** (no runtime needed)
  - **Framework-dependent** (requires .NET 9 runtime, smaller size)

---

## 🛠 Installation

1. Go to the [**Releases**](https://github.com/primetime43/iptv-desktop-browser/releases) page.
2. Download your preferred build:
   - **Self-contained**: `...self-contained-win-x64.zip` → unzip & run.
   - **Framework-dependent**: `...framework-dependent-win-x64.zip` → install [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0), then run.

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

---

## 🔒 Security & Privacy

- Credentials are saved **only if you choose to remember them**.
- Passwords are **encrypted locally** using Windows DPAPI (per-user).
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
