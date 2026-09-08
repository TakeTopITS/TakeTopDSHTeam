# TakeTopDSH Team

TakeTopDSH Team turns [DeepSeek Harness] into a **team-ready, multi-user platform**,it is a **web-based AI collaboration platform** designed for software development and office teamwork. Team members access AI-assisted coding, document editing, and task management directly from their browser — **team experience data accumulates and is shared across the team**, getting smarter over time.

**Key Features**:
- 🌐 **Web-based** — Runs in the browser, no client installation needed, accessible from anywhere.
- 👥 **Multi-user collaboration** — Multiple users online simultaneously, each with an isolated workspace.
- 📚 **Experience accumulation** — Conversations, files, and task data are persistently stored; knowledge is reusable.
- 🚀 **One-click install** — Copy and run, no compilation or .NET runtime required.
- 🖱️ **Full graphical interface** — All operations via mouse clicks; intuitive and easy to learn.

> 📌 **Note**: This documentation is text-only and does not include richly illustrated pages. The best way to experience it is to try the software yourself! Run start.bat and visit http://127.0.0.1:46001 to see it in action..

---

TakeTopDSHTeam is a **team-ready, multi-user platform: every member gets their own isolated DSH instance behind a single launcher, with a built-in file manager, task assignment, and a split-screen workbench. Copy the whole folder to any machine and run — **no install, no compilation**.

> **Dual-licensed.** The source code is released under **GPL-3.0** (free for unlimited use/modification/redistribution under its terms). Commercial users or teams that need commercial support and private deployment terms can obtain a **commercial license** from 泰顶拓鼎信息科技（上海）有限公司 (TakeTop Information Technology (Shanghai)(EMail: service@taketopits.com). It is **free for up to 10 users**; larger teams or those needing extra support should contact us for a paid license.

---

## Highlights

- 🔌 **Single entry, single port** — one launcher (`:46001`) proxies to each member's private DSH, so you expose only one public port.
- 👤 **Per-user isolation** — each user runs DSH under a dedicated OS user + sandboxed workspace; no cross-user access.
- 🖥️ **Split-screen workbench** — left pane = File Manager / My Tasks (two tabs), right pane = the DSH chat. Drag or one-click (⤢) to resize 30% ↔ 70%.
- 📁 **Built-in file manager** — upload, new folder, tree browsing, zip/unzip, rename, move, delete, inline preview, download.
- ✅ **Task assignment** — admins assign tasks per member (rich text + images + attached files). Tasks persist as XML in each workspace (`TaskData/tasks-<id>.xml`), files go to `TaskData/Doc`.
- 🌐 **i18n** — full Chinese / English UI (buttons, table headers, statuses, task form, login page).
- 🧩 **Survives upgrades** — a patch system re-applies all custom behavior after every DSH update.
- 📦 **Copy-to-run across platforms** — Windows / macOS / Linux x64 & arm64, self-contained, bundles the runtime and Node. No compilation on the target machine.
- 🚚 **Portable paths** — all internal paths are relative to the install root, so you can move the folder anywhere.

---

## Quick Start

```
Windows : double-click start.bat
macOS   : ./start.sh
Linux   : ./start.sh
```

Then open `http://127.0.0.1:46001` → log in → click **Open DSH**.

> Do **not** open `:46000` directly (returns 404); DSH needs the `token` that the launcher injects.

See [INSTALL.md](INSTALL.md) for full setup, and [操作说明.md](操作说明.md) for usage.

---

## Screenshots (conceptual layout)

```
┌──────────────────────────────────────────────────────────────────────────┐
│ TakeTopDSH Team Launcher                         admin (admin)    [语言/EN] │
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
- Tasks stored as **XML** under `<workspace>/TaskData/tasks-<id>.xml`; files uploaded to `<workspace>/TaskData/Doc`.

### 6. Language
Full **中文 / English** toggle across the console, workbench, file manager, task assignment, and task form. The login page prefers the configured default language.

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
| macOS / Linux (x64 & arm64) | bundled (tar, offline) | first-run requires one network `npm install`, then offline |

---

## License

This project is **dual-licensed**:

- **Open Source**: the full source code is licensed under the **GNU GPL v3.0** (see [LICENSE](LICENSE) / [COPYING](COPYING)). You may use, copy, modify and redistribute it under the GPL-3.0 terms.
- **Commercial**: for teams/organizations that require commercial support, private deployment, custom features, or alternative licensing terms, a separate **commercial license** is available from **泰顶拓鼎信息科技（上海）有限公司(TakeTop Information Technology (Shanghai) Co., Ltd.)** (EMail: service@taketopits.com). It is **free for up to 10 users**; larger teams or those needing commercial support should contact us for a paid license.

Copyright (C) 2026-2036 泰顶拓鼎信息科技（上海）有限公司(TakeTop Information Technology (Shanghai) Co., Ltd.). All rights reserved.
