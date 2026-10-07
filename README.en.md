[English](README.en.md) | [한국어](README.md)

# 🐾 NyaaChat Client

A lightweight real-time chat client for Windows desktop (.NET Framework 4.8). Runs as a single standalone executable without any external DLLs, and supports simultaneous multi-server connections.

- 🌐 **Backend Chat Server (Node.js)**: [nyaa-chat-server Repository](https://github.com/nemunemulo/nyaa-chat-server)

---

## AI-Friendly & Hackable

NyaaChat Client is designed to be lightweight and simple so anyone can easily customize and extend it.
It compiles instantly using just the single source file and `build.bat`, without complex IDE or toolchain setups.

Feel free to pass the code to an AI tool to request UI design changes, custom notifications, auto-reply macros, or any other features you need.
After editing, simply run `build.bat` to generate your own custom client executable.
Customize and improve it freely to fit your needs!

---

## 📁 Directory & File Structure

```text
client/
├── NyaaChat.exe             # Client executable (.NET Framework 4.8)
├── build.bat                # Windows built-in csc.exe compile batch script
├── settings.example.ini     # Settings example (settings.ini auto-generated on first run)
├── aliases.txt              # Slash command shortcuts (/j, /w, etc.)
├── scripts/
│   └── user_script.txt      # Event trigger script examples
├── modules/
│   ├── README.txt           # Server extension module specifications
│   └── sample_CCC.txt       # Server-specific extension module example
├── themes/
│   ├── default_dark.ini     # Default dark theme
│   ├── classic_white.ini    # Classic white theme
│   ├── pc_hitel.ini         # Blue theme
│   └── matrix_green.ini     # Green theme
├── sounds/
│   └── README.txt           # Custom sound connection guide
├── linux_arm/
│   └── nyaachat_native.py   # Linux / ARM console client
└── android_apk/
    └── README.txt           # Mobile access guide
```

---

## 🚀 Build Instructions

You can build the client directly using the built-in Windows C# compiler (`csc.exe`):
```cmd
build.bat
```
Once compilation succeeds, `NyaaChat.exe` is generated immediately.

---

## ⚙️ Configuration (`settings.ini`)

On first launch, `settings.ini` is automatically created from `settings.example.ini`.

```ini
[General]
Language=en

[Server]
Url=https://nemulo.duckdns.org
DefaultChannel=#자유대화
AutoConnect=true
AutoConnectServers=https://nemulo.duckdns.org

[AutoJoin]
nemulo.duckdns.org=#자유대화

[User]
DefaultNickname=
NickPassword=
Avatar=🐾
```

- **`Language`**: Display language (`en` or `ko`). On first run, a language selection dialog appears automatically.
- **`Url`**: Default chat server URL to connect to.
- **`DefaultChannel`**: Default channel to enter upon initial connection.
- **`AutoConnectServers`**: Comma-separated list of server URLs to connect to automatically on startup.
- **`[AutoJoin]`**: Automatic channel join configuration per server.

---

## 🌐 Key Features & Shortcuts

- **Keyboard Shortcuts**:
  - `F2`: Whitelisted server directory and public channel explorer
  - `F10`: Settings dialog (server, nickname, language, theme, sound, panel widths, etc.)
  - `F9` or `/modules`: Server extension module manager
  - `Alt + R`: Custom script editor (`aliases.txt` / `user_script.txt`)
  - `Alt + Q`: Boss key (instantly hide/restore window)
- **Multi-Server Connection**:
  - Connected servers are listed on the left tree view; click to switch between servers.
  - Connect to additional servers concurrently using `/server <URL> [#channel]`.

---

## 💬 Common Slash Commands

| Command | Description | Example |
| :--- | :--- | :--- |
| `/help` | View command help | `/help` |
| `/nick <new_nick>` | Change nickname | `/nick Nyaa` |
| `/join <#channel> [key]` | Join or create a channel | `/join #gaming` |
| `/part` (or `/leave`) | Leave current channel | `/part` |
| `/list` | Open channel list dialog | `/list` |
| `/servers` (or `F2`) | Open server list explorer | `/servers` |
| `/server <url> [#channel]` | Connect to another server concurrently | `/server https://c.org #gaming` |
| `/whois <nickname>` | View user info | `/whois Alice` |
| `/msg <nickname> <text>` | Send a 1:1 direct whisper | `/msg Bob Hello!` |
| `/me <action>` | 3rd person action message | `/me stretches paws` |
| `/lang <ko/en>` | Switch display language | `/lang en` |
| `/ping` | Check server round-trip latency | `/ping` |
| `/clear` | Clear chat buffer | `/clear` |

---

## 📜 License

This project is licensed under the [MIT License](LICENSE). Feel free to use, modify, and distribute.
