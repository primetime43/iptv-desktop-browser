# IPTV Desktop Browser

A **lightweight Windows desktop IPTV browser** built with **WPF / .NET 9** for fast, clean, and secure access to IPTV channel lists.

v1.0.1
<img width="1186" height="593" alt="image" src="https://github.com/user-attachments/assets/fed90a5e-31d8-4fac-b715-fd1b1514fda7" />
<img width="1920" height="1032" alt="image" src="https://github.com/user-attachments/assets/dd423c91-dd90-4508-af42-6b285e6c2f82" />


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
