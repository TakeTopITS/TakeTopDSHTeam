# TakeTopDSHTeam

![TakeTopDshTeam — split-screen workbench: File Manager (left) + DSH chat (right)](assets/workbench.png)

TakeTopDSHTeam transforms DeepSeek Harness (DSH) into a team-ready, multi-user AI collaboration platform — a self-hosted web workspace for any industry (software development, engineering design, document editing, media processing, and more). Each member gets a dedicated, isolated DSH with file management, task management, and a split-screen workbench. Conversations, files, and tasks are shared across the team, so knowledge accumulates and efficiency grows. It supports multiple LLMs (DeepSeek, OpenAI, Claude, Kimi, and more) with a single shared API Key — easy to manage, easy to control, and cheaper to run!

> 👥 **Who it's for:** teams that use (or want to use) DSH but need **multiple users, per-user isolation, and full control of their data** — without running one DSH per person and without handing data to a cloud service.

## Highlights

- 🧠 **Shared team experience** — one member's conversations, findings and files become reusable knowledge for the whole team, getting better the more you use it!
- 👤 **Per-user isolation** — each user runs DSH under a dedicated sandboxed workspace; no cross-user workspace access.
- 🖥️ **Split-screen workbench** — left pane = File Manager / My Tasks; right pane = the DSH chat.
- 🔌 **Single entry, single port** — one launcher proxies to every member's private DSH, sharing one login URL.
- ✅ **Task assignment & delegation** — assign tasks per member (rich text + images + attached files); delegate follow-ups with **Continue Assign**, and drill into the **parent / subtask tree** inline. Stored in a shared database.
- 📁 **Built-in file manager** — upload, folders, tree browsing, zip/unzip, rename, move, delete, inline preview, download.
- 🌐 **Bilingual UI** — full English / Chinese, supports all pages. Switch anytime!
- 🚀 **One-click, offline install** — copy the folder and run; no compilation, no extra downloads.
- 🧩 **Survives upgrades** — one-click upgrade, enjoy new features instantly!
- 📦 **Cross-platform** — supports Windows / macOS / Linux. Safe and reliable!

> 🔓 **All source code is fully visible — no encrypted components.** The complete source of TakeTopDshTeam ships in this repository: the multi-user launcher (C#/.NET), the web UI (HTML/JS/CSS), the DSH integration and branding patches, and all start/build scripts. **No component is encrypted, obfuscated, or shipped as a black box** — you can read and audit every line before running it. The only pre-built pieces are the third-party runtimes (Node.js and the .NET runtime), which are open-source projects themselves. Use it with confidence.

> 🔒 **Self-hosted by design — not a cloud service.** We do **not** provide any cloud hosting or rental service. You download the release and install it locally on your own computer or server; accounts, sessions, files and API keys are stored on your own disk, and we never receive or hold your data — so your data stays fully under your control. (The only outbound connection is to the LLM provider you configure yourself.)

> **BSL 1.1 (source-available).** Licensed under the **Business Source License 1.1** (see [LICENSE](LICENSE) / [COPYING](COPYING)) — **free for organizations with up to 10 users**; above 10 users a commercial license is required (**USD 10 per additional user per month**: e.g. 11 users = USD 10/month, 15 users = USD 50/month, 20 users = USD 100/month) ([LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md)). It is **source-available, not OSI open source**, and **automatically converts to Apache-2.0** on the Change Date (2030-09-11). The intellectual property remains vested in 泰顶拓鼎信息科技（上海）有限公司 (TakeTop Information Technology (Shanghai) Co., Ltd.) — Email: service@taketopits.com. All rights reserved.

---

## Quick Start

```
Windows : double-click start.bat   (UAC administrator prompt — required for per-user OS isolation)
macOS   : bash start.sh
Linux   : bash start.sh
```

> **On startup,** on Windows the elapsed time is shown live in the window title bar, and the browser opens automatically once it is ready — no need to refresh.

Open `http://127.0.0.1:46001` → log in → click **Open DSH**.

Setup and usage: [USER_GUIDE_English.pdf](USER_GUIDE_English.pdf).

> 📦 **Why is the download package so large (700+ MB)?** On purpose — for your convenience. The release package **bundles every file needed to install on all three operating systems — Windows, macOS and Linux** (the self-contained launcher for each platform, the Node.js runtime, and the offline `@deepseek-ai/dsh` packages). So one download gives you a **fully offline, one-click install** on any of them — no further downloads and no compilation on the target machine.

---

## Feature Tour

### 1. Launcher Console (admin)
Start/stop the default DSH, edit config (appsettings URL / workspace / default language), and manage every member:

![Launcher console — start/stop the default DSH, set the workspace, and create/manage members](assets/launcher.png)

Each member also gets a simple **My Instance** page — start/stop their own DSH, open it, and see that instance's logs:

![Member view — My Instance and per-instance logs](assets/MemberLauncher.png)

- **Create instance** — auto workspace (`<global-workspace>/<username>`), dedicated OS user + sandbox.
- **Start / Stop / Open / Delete / Reset Password** per member.

### 2. Workbench (split screen)
- Click **Open DSH** on a member row (or the launcher's "Open DSH") → split workbench.
- **Left pane** tabs: **File Manager** and **My Tasks**.
- Resize: drag the divider, or the ⤢ button toggles 30% ↔ 70%.

![Split-screen workbench — File Manager / My Tasks on the left, DSH chat on the right](assets/workbench.png)

### 3. File Manager
- Upload, new folder, refresh; tree view with `+`/`-` expand.
- Per-file actions (right-click): zip / unzip / rename / move / delete.
- Preview inline (images, text, video); non-previewable files prompt to download.
- **Drag a file into the AI dialog** to hand it straight to the agent.

![File Manager — right-click a file or folder for actions](assets/FileManage.png)

### 4. My Tasks
Each user sees tasks assigned to them:

- Task-name link opens a detail popup (rich content; double-click an image for full-screen preview).
- Related files open/download; images preview directly.
- **Drag a task name into the AI dialog** to start working on it.
- **Continue Assign** — create a follow-up (child) task and choose its **assignee**; the sub-task lands in that member's My Tasks, and the parent row auto-expands to show it.
- **Edit / Delete** — rows you created get Edit and Delete buttons (your own tasks only).
- **Subtask tree** — a `+` in the subtask column expands a task's children inline (indented, multi-level); tasks without children show no `+`.
- Opening this tab automatically widens the left pane to **70%**.

![My Tasks — drag a task name into the AI dialog to start on it](assets/TaskHandleForCoding.png)
![My Tasks — each task keeps its rich-text feedback and attachments](assets/TaskFeedback.png)
![My Tasks — Continue Assign, the subtask tree, and edit/delete](assets/MyTasksAssign.png)

### 5. Task Assignment — `/tasks`
- Member list on the left; pick a member to see their tasks on the right.
- **Add / Edit** a task: **Assignee**, **Type** (type dropdown + `+` to add a new type), **Name**, **Content** (rich text — bold/lists/quote, paste or insert images), **Assigned At**, **Status** (Processing / Done / Cancelled), **Related files** (multi-select upload).
- **Subtask hierarchy** — every task row has a `+` column; click it to expand that task's subtasks inline (indented, multi-level). Each child stores its parent's **No. (seq)** as its parent task (default `0` = top level); tasks without children show no `+`.
- Tasks are stored in the launcher's shared **database** (`<workspace>/database/taketopDSHTeam.db`); attachments go to `<workspace>/TaskData/Doc`.

![Task assignment — pick a member, then add/edit their tasks](assets/TaskAssignment.png)
![Task assignment — pick a member, then add/edit their tasks](assets/AddTask.png)
![Task assignment — expand a task's subtasks inline with the "+" column](assets/TaskSubtask.png)

### 6. Language
Full **English / Chinese** toggle across the console, workbench, file manager, task assignment, and task form. The login page prefers the configured default language.

![Login page — language selector (English / 中文)](assets/Login.png)

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

## Get started

- **Download** — grab the latest release from the **Releases** page (or clone this repository). One package covers Windows / macOS / Linux and installs offline.
- **Questions, feedback, bugs** — open an **Issue** on GitHub.
- **Commercial licensing (more than 10 users) & other enquiries** — service@taketopits.com

Topics: `deepseek-harness` · `multi-user` · `self-hosted` · `ai-agent` · `llm` · `team-collaboration` · `sqlite`

---

## License

This project is licensed under the **Business Source License 1.1 (BSL 1.1)** — see [LICENSE](LICENSE) / [COPYING](COPYING). BSL 1.1 is a **source-available** license (it is not OSI open source).

- You may copy, modify, create derivative works, redistribute, and make **non-production** use of the software.
- **Additional Use Grant:** production use is **free for an organization with up to 10 users**; above 10 users a **commercial license** is required, priced at **USD 10 per additional user per month** (e.g. 11 users = USD 10/month; 15 users = USD 50/month; 20 users = USD 100/month) (contact service@taketopits.com; see [LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md)).
- **Automatic conversion:** on the Change Date (**2030-09-11**) — or the fourth anniversary of a version's first public release, whichever is earlier — that version converts to the **Apache License, Version 2.0** (fully open source). The license applies per version.
- Earlier releases published under the **MIT License remain available under MIT** permanently.

The intellectual property of this software — including its source code, design, documentation and related materials — is vested in **泰顶拓鼎信息科技（上海）有限公司 (TakeTop Information Technology (Shanghai) Co., Ltd.)** (Email: service@taketopits.com). The license grants usage rights but does not transfer ownership of the intellectual property, which remains with the copyright holder.

Third-party components bundled with this product remain under their own licenses (see `gui-cs/src/wwwroot/vendor/licenses/` and [NOTICE](NOTICE)).

Copyright (c) 2026-2036 泰顶拓鼎信息科技（上海）有限公司 (TakeTop Information Technology (Shanghai) Co., Ltd.). All rights reserved.
