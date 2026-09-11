# TakeTopDSHTeam

TakeTopDSHTeam turns DeepSeek Harness into a **team-ready, multi-user platform**. It is a **web-based AI collaboration platform** designed for software development and office teamwork. Team members access AI-assisted coding, document editing, and task management directly from their browser — **team experience data accumulates and is shared across the team**, getting smarter over time.

**Key Features**:
- 🌐 **Web-based** — Runs in the browser, no client installation needed, accessible from anywhere.
- 👥 **Multi-user collaboration** — Multiple users online simultaneously, each with an isolated workspace.
- 📚 **Experience accumulation** — Conversations, files, and task data are persistently stored; knowledge is reusable.
- 🚀 **One-click install** — Copy and run, no compilation or .NET runtime required.
- 🖱️ **Full graphical interface** — All operations via mouse clicks; intuitive and easy to learn.

> 📌 **Note**: This document contains text only and does not include any screenshots or images. The best way to experience the software is to try it out yourself!

---

TakeTopDSHTeam is a **team-ready, multi-user platform: every member gets their own isolated DSH instance behind a single launcher, with a built-in file manager, task assignment, and a split-screen workbench. Copy the whole folder to any machine and run — **no install, no compilation**.

> **BSL 1.1 (source-available).** Licensed under the **Business Source License 1.1** (see [LICENSE](LICENSE) / [COPYING](COPYING)) — **free for organizations with up to 10 users**; above 10 users a commercial license is required (tiered: **USD 10 per year for each additional block of 10 users**) ([LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md)). It is **source-available, not OSI open source**, and **automatically converts to Apache-2.0** on the Change Date (2030-09-11). Earlier MIT-licensed releases remain under MIT. The intellectual property remains vested in 泰顶拓鼎信息科技（上海）有限公司 (TakeTop Information Technology (Shanghai) Co., Ltd.) — EMail: service@taketopits.com. All rights reserved.

---

> 🔓 **All source code is fully visible — no encrypted components.** The complete source of TakeTopDSH Team ships in this repository: the multi-user launcher (C#/.NET), the web UI (HTML/JS/CSS), the DSH integration and branding patches, and all start/build scripts. **No component is encrypted, obfuscated, or shipped as a black box** — you can read and audit every line before running it. The only pre-built pieces are the third-party runtimes (Node.js and the .NET runtime), which are open-source projects themselves. Use it with confidence.

> 🔒 **Self-hosted by design — not a cloud service.** We do **not** provide any cloud hosting or rental service. You download the release and install it locally on your own computer or server; accounts, sessions, files and API keys are stored on your own disk, and we never receive or hold your data — so your data stays fully under your control. (The only outbound connection is to the LLM provider you configure yourself.)

---

## Highlights

- 🔌 **Single entry, single port** — one launcher (`:46001`) proxies to each member's private DSH, so you expose only one public port.
- 👤 **Per-user isolation** — each user runs DSH under a dedicated OS user + sandboxed workspace; no cross-user access.
- 🖥️ **Split-screen workbench** — left pane = File Manager / My Tasks (two tabs), right pane = the DSH chat. Drag or one-click (⤢) to resize 30% ↔ 70%.
- 📁 **Built-in file manager** — upload, new folder, tree browsing, zip/unzip, rename, move, delete, inline preview, download.
- ✅ **Task assignment** — admins assign tasks per member (rich text + images + attached files). Tasks are stored per member in **SQLite** (`TaskData/tasks-<id>.db`; legacy XML is auto-imported on first use), files go to `TaskData/Doc`.
- 🌐 **i18n** — full English / Chinese UI (buttons, table headers, statuses, task form, login page).
- 🧩 **Survives upgrades** — a patch system re-applies all custom behavior after every DSH update.
- 📦 **Copy-to-run across platforms** — Windows / macOS / Linux x64 & arm64, self-contained, bundles the runtime and Node. No compilation on the target machine.
- 🚚 **Portable paths** — all internal paths are relative to the install root, so you can move the folder anywhere.

---

## Quick Start

```
Windows : double-click start.bat
macOS   : bash start.sh
Linux   : bash start.sh
```

> On a fresh `git clone`, run the script as `bash start.sh` — the executable bit is not guaranteed to survive a Windows checkout (you may also `chmod +x start.sh` once).

Then open `http://127.0.0.1:46001` → log in → click **Open DSH**.

> Do **not** open `:46000` directly (it answers `401 Unauthorized`); DSH needs the `token` that the launcher injects.

See  [Guide/USER_GUIDE_English.pdf](Guide/USER_GUIDE_English.pdf) for setup and usage.

---

## Screenshots (conceptual layout)

```
┌──────────────────────────────────────────────────────────────────────────┐
│ TakeTopDSH Team Launcher                         admin (admin)    [Lang/EN] │
├──────────────────────────────────────────────────────────────────────────┤
│ [Port 46000] [Start] [Stop] [Open DSH]      ● Running (PID 11384)          │
│ Instance Manager:                                                            │
│   ******  46002  Stopped  [Start][Delete][Reset Password]                │
│   ******    46003  Running  [Open][Stop][Delete][Reset Password]           │
└──────────────────────────────────────────────────────────────────────────┘

Workbench (/work):
┌───────────────────────┬───────────────────────────────────────────────────┐
│ [File Manager][My Tasks]                                                  │
│  ← left pane (resize) │        DSH chat (right pane)                       │
│   file explorer       │   "Explore the unknown…"                           │
└───────────────────────┴───────────────────────────────────────────────────┘
```

## Feature Tour

### 1. Launcher Console (admin)
Start/stop the default DSH, edit config (appsettings URL / workspace / default language), and manage every member:

- **Create instance** — auto workspace (`global-workspace/<username>`), dedicated OS user + sandbox.
- **Start / Stop / Open / Delete / Reset Password** per member.

### 2. Workbench (split screen)
- Click **Open DSH** on a member row (or the launcher's "Open DSH") → split workbench.
- **Left pane** tabs: **File Manager** and **My Tasks**.
- Resize: drag the divider, or the ⤢ button toggles 30% ↔ 70%.

### 3. File Manager
- Upload, new folder, refresh; tree view with `+`/`-` expand.
- Per-file actions: zip / unzip / rename / move / delete.
- Preview inline (images, text, video); non-previewable files prompt to download.

### 4. My Tasks
Each user sees tasks assigned to them:

- Task-name link opens a detail popup (rich content; double-click an image for full-screen preview).
- Related files open/download; images preview directly.

### 5. Task Assignment (admin) — `/tasks`
- Member list on the left; pick a member to see their tasks on the right.
- **Add / Edit** a task: **Type**, **Name**, **Content** (rich text — bold/lists/quote, paste or insert images), **Status** (Processing / Done / Cancelled), **Related files** (multi-select upload).
- Tasks stored per member in **SQLite** at `<workspace>/TaskData/tasks-<id>.db` (legacy XML auto-imported on first use); files uploaded to `<workspace>/TaskData/Doc`.

### 6. Language
Full **English / Chinese** toggle across the console, workbench, file manager, task assignment, and task form. The login page prefers the configured default language.

---

## Architecture

```
┌────────────┐  :46001   ┌─────────────┐   WebSocket / HTTP proxy
│  Browser    │─────────▶│  Launcher    │──────────▶ DSH :46000 (admin default)
└────────────┘           │  (ASP.NET)   │──────────▶ DSH :46002 (jackzhong)
                         │  multi-user  │──────────▶ DSH :46003 (ericliu)
                         └─────────────┘──────────▶ ...
```

- Launcher (self-contained, no .NET needed on target) serves the admin/workspace/tasks UI and proxies DSH requests, injecting the auth token so users never see "authentication required".
- Each DSH runs under a **restricted OS user** with sandboxed workspace and contained read access.
- A **patch layer** (idempotent) re-applies launcher customizations after DSH upgrades.

---

## Platform support

| Platform | Node | @deepseek-ai/dsh deps |
|----------|------|------------------------|
| Windows x64 | bundled, offline | bundled, offline |
| macOS / Linux (x64 & arm64) | bundled (tar, offline) | bundled (offline tarball); falls back to `npm install` only if the bundle is missing |

---

## License

This project is licensed under the **Business Source License 1.1 (BSL 1.1)** — see [LICENSE](LICENSE) / [COPYING](COPYING). BSL 1.1 is a **source-available** license (it is not OSI open source).

- You may copy, modify, create derivative works, redistribute, and make **non-production** use of the software.
- **Additional Use Grant:** production use is **free for an organization with up to 10 users**; above 10 users a **commercial license** is required, priced in **blocks of 10 users at USD 10 per additional block per year** (e.g. 11–20 users = USD 10/year; 21–30 users = USD 20/year) (contact service@taketopits.com; see [LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md)).
- **Automatic conversion:** on the Change Date (**2030-09-11**) — or the fourth anniversary of a version's first public release, whichever is earlier — that version converts to the **Apache License, Version 2.0** (fully open source). The license applies per version.
- Earlier releases published under the **MIT License remain available under MIT** permanently.

The intellectual property of this software — including its source code, design, documentation and related materials — is vested in **泰顶拓鼎信息科技（上海）有限公司 (TakeTop Information Technology (Shanghai) Co., Ltd.)** (EMail: service@taketopits.com). The license grants usage rights but does not transfer ownership of the intellectual property, which remains with the copyright holder.

Third-party components bundled with this product remain under their own licenses (see `gui-cs/src/wwwroot/vendor/licenses/` and [NOTICE](NOTICE)).

Copyright (c) 2026-2036 泰顶拓鼎信息科技（上海）有限公司 (TakeTop Information Technology (Shanghai) Co., Ltd.). All rights reserved.
