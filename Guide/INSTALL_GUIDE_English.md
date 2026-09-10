# TakeTopDSH Team — Platform Installation Guide

This guide covers Windows / macOS / Linux. **Copy-and-run**: copy the whole `TakeTopDshTeam` folder to the target machine — **no .NET install, no compilation needed**.

---

## 1. Quick Start

### Windows (x64)
1. Copy the whole `TakeTopDshTeam` folder to the target computer.
2. Double-click **`start.bat`** (or right-click → "Run as administrator" if the port is in use).
3. The browser opens the launcher UI at `http://127.0.0.1:46001`.
4. Click **"Open DSH"** in the launcher UI to enter the workspace (the token is injected automatically — no manual entry). The workspace is a left/right split: the left pane has **"File Manager"** and **"My Tasks"** tabs, the right pane is the DSH page.

### macOS (Apple Silicon, arm64)
1. Copy the whole `TakeTopDshTeam` folder to the target Mac.
2. Open Terminal, go to the folder:
   ```bash
   cd /path/to/TakeTopDshTeam
   chmod +x start.sh
   ./start.sh
   ```
3. On first run it extracts Node and installs `@deepseek-ai/dsh` (**needs network once**).
4. Open `http://127.0.0.1:46001`, click **"Open DSH"** to enter the left/right workspace.

### Linux (x64 / arm64)
1. Copy the whole `TakeTopDshTeam` folder to the target machine.
2. Open Terminal, go to the folder:
   ```bash
   cd /path/to/TakeTopDshTeam
   chmod +x start.sh
   ./start.sh
   ```
3. On first run it extracts Node (already bundled, no download); only `@deepseek-ai/dsh`'s native dependencies need **one network install**, then it works offline.
4. Open `http://127.0.0.1:46001`, click **"Open DSH"** to enter the left/right workspace.

---

## 2. Folder Contents

```
TakeTopDshTeam/
├── start.bat              # Windows launcher
├── start.sh               # macOS/Linux launcher
├── dsh-launcher/
│   ├── win-x64/           # Windows self-contained launcher (.exe)
│   ├── linux-x64/         # Linux x64 self-contained launcher
│   ├── linux-arm64/       # Linux arm64 self-contained launcher
│   └── osx-arm64/         # macOS Apple Silicon self-contained launcher
├── node/
│   ├── (Windows node.exe + npm + dsh deps)   # Windows node/runtime
│   └── platforms/         # Linux/macOS Node distribution (tar)
├── config/                # accounts + instance definitions (launcher.db, SQLite; legacy users.json/instances.json auto-migrated)
├── instances/             # per-user DSH home dirs (instances/<user>/.dsh)
├── .dsh/                  # admin default dsh config / profiles / credentials
├── docs/                  # work experience data, etc.
├── appsettings.json       # DshWeb config (URL / port / default language / workspace)
└── wwwroot (bundled)      # admin page, workspace (/work), task assignment (/tasks)
```

- The launcher under `dsh-launcher/<platform>/` is a **self-contained single file** (bundles the .NET runtime). **No .NET is required on the target machine.**

---

## 3. Ports

| Port    | Purpose |
|---------|---------|
| `46001` | Launcher UI |
| `46000` | Admin default DSH Web (requires token) |
| `46002` | User instance jackzhong's DSH |
| `46003` | User instance ericliu's DSH |
| `46004` | User instance sofiali's DSH |
| `46005` | User instance bookge's DSH |

> ⚠️ **Do NOT open `http://127.0.0.1:46000` directly** — it returns 404. DSH Web requires a `?token=` parameter. Use the **"Open DSH"** button in the launcher UI (auto-injects the token).

To change the port: edit `DshWeb/Url` in `appsettings.json`, or use the launcher UI "Port" field + Save / Start.

**Default accounts:**

| User | Default password | Notes |
|------|------------------|-------|
| admin  | 12345678 | Administrator (launcher console & task assignment) |
| ****** | (set by admin) | Regular user, instance 46002 |
| ****** | (set by admin) | Regular user, instance 46003 |
| ****** | (set by admin) | instance 46004 |
| ****** | (set by admin) | instance 46005 |

> The **admin** default password is `12345678`. Other accounts can be set/reset via **Reset Password** in the console.

---

## 4. First-Run Network Notes

| Platform | Node runtime | @deepseek-ai/dsh deps |
|----------|--------------|-----------------------|
| Windows      | ✅ Bundled, offline        | ✅ Bundled, offline |
| macOS/Linux  | ✅ Bundled (extract tar, offline) | ⚠️ Needs one-time network `npm install`, then offline |

- **Node runtime**: all three platform versions are already downloaded under `node/platforms/`; `start.sh` just extracts them — **no network needed**.
- **dsh deps**: include platform-specific native modules (koffi, node-pty, etc.). Windows binaries cannot be used on Linux/macOS, so macOS/Linux need a **one-time** network `npm install` (npm auto-downloads the platform-native modules); afterwards fully offline.
- **Windows**: Node and dsh both bundled, fully offline.
- **macOS/Linux**: Node bundled (offline). Only `@deepseek-ai/dsh`'s native deps need network on first run, then offline.

---

## 5. FAQ

**Q: Opening `127.0.0.1:46000` shows 404?**
A: That's expected. Use the **"Open DSH"** button on the launcher UI (46001) — it auto-injects the token.

**Q: The launcher UI won't open / port is occupied?**
A: `46001` may already be in use. You can set the env var `LAUNCHER_PORT` to a different port and launch with `start.bat`/`start.sh`; or close the occupying process and retry.

**Q: macOS says "cannot be opened because the developer cannot be verified"?**
A: macOS restricts unsigned apps. Right-click the app → Open; or run the code-sign from Terminal:
```bash
xattr -d com.apple.quarantine /path/to/TakeTopDshTeam/dsh-launcher/osx-arm64/TakeTopDshLauncher
```
Handle large single-file quarantine the same way.

**Q: Linux reports `chmod` / permission error?**
A: Make sure you ran `chmod +x start.sh`, and that `dsh-launcher/<platform>/TakeTopDshLauncher` has execute permission (`start.sh` auto-runs `chmod +x`).

**Q: How do I change the default model / API Key?**
A: In the DSH Web UI (after opening with the token) → **Settings → Models** to configure model and API Key; or edit the relevant config files under `.dsh/`.

---

## 6. Verified Environment

- This package has been verified on Windows x64 end-to-end (start.bat → launcher → auto-start DSH Web → open with token).
- The macOS / Linux builds were cross-published on Windows with `dotnet publish -r <rid> --self-contained` (no native GUI dependency); they still need to be run/verified on their respective platforms. Node and dsh deps are prepared automatically on first run.

---

## TL;DR

Copy `TakeTopDshTeam` to your machine → double-click `start.bat` (Windows) or run `./start.sh` (macOS/Linux) → open `http://127.0.0.1:46001` → click **"Open DSH"** (left pane = File Manager / My Tasks, right pane = DSH).
