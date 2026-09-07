# TakeTopDSH Team — User Guide

This guide is for **everyday users**: log in → open the workbench → File Manager / My Tasks → admin task assignment, instance management, language switching.

> **Prerequisite**: run the launcher first so the browser can reach http://127.0.0.1:46001.
> - Windows: double-click **start.bat**
> - macOS / Linux: run **./start.sh**
> - As long as the launcher runs in the background you can reopen the browser anytime; if it was closed (e.g. after a reboot), run it once again.

> See `INSTALL.md / INSTALL.md` for how to install and start; this file covers **how to use it after installation**.

---

## Contents

- [1. What the UI looks like](#1-what-the-ui-looks-like)
- [2. Log in](#2-log-in)
- [3. Launcher Console (admin)](#3-launcher-console-admin)
- [4. Open the Workbench (split screen)](#4-open-the-workbench-split-screen)
- [5. File Manager (left pane)](#5-file-manager-left-pane)
- [6. My Tasks (left pane)](#6-my-tasks-left-pane)
- [7. Task Assignment page /tasks (admin)](#7-task-assignment-page-tasksadmin)
- [8. Language switching (ZH/EN)](#8-language-switching-zhen)
- [9. Quick reference](#9-quick-reference)

---

## 1. What the UI looks like

Open `http://127.0.0.1:46001` to enter the launcher. There are two main areas:

- **Launcher Console** (home): start/stop, Open DSH, configuration, instance management.
- **Workbench** (enter via **Open DSH**): split screen — left = File Manager / My Tasks, right = the DSH chat.

```
┌──────────────────────────────────────────────────────────────────────────┐
│ TakeTopDSH Team Launcher                          admin (admin)  Language │  ← top: account + language
├──────────────────────────────────────────────────────────────────────────┤
│ Launcher: Port[46000] [Start] [Stop] [Open DSH]   ● Running (PID xxxx)    │
│ Config:   appsettings URL / Workspace path / Default Lang     [Save]     │
│ Instance Manager: User  Port  Workspace  State  Actions                   │
│   jackzhong  46002  ...   Stopped   [Start][Delete][Reset Password]       │
│   ericliu    46003  ...   Running   [Open][Stop][Delete][Reset Password]  │
│   sofiali    46004  ...   Stopped   [Start]...                            │
│   bookge     46005  ...   Stopped   [Start]...                            │
└──────────────────────────────────────────────────────────────────────────┘
```

---

## 2. Log in

1. Open `http://127.0.0.1:46001`, the login page appears.
2. Pick a language (top-right), enter **Username / Password**, click **Log in**.
   - The login page defaults to the system default language; you can switch it.
3. After login the top-right shows your username (admin shows `admin (admin)`).

> Accounts are created / reset by the admin in **Instance Manager → Reset Password**. Sign in with the password the admin set.

**Default accounts (initial passwords — please change them in the console):**

| Username | Default password | Notes |
|----------|------------------|-------|
| admin | 12345678 | Administrator (launcher console & task assignment) |
| jackzhong | jack123 | Regular user, instance 46002 |
| ericliu | eric123 | Regular user, instance 46003 |
| sofiali | (set by admin) | instance 46004 |
| bookge | (set by admin) | instance 46005 |

> The **admin** default password is `12345678`. Other accounts can be set/reset via **Reset Password** in the console.

---

## 3. Launcher Console (admin)

### 3.1 Launcher card (admin default DSH)

```
Port [46000]   [Start]   [Stop]   [Open DSH]
              ● Running (PID 11384)
```

- **Port**: port of the admin default instance (default 46000).
- **Start / Stop**: start/stop the admin default DSH. The button shows a **spinner** until it finishes.
- **Open DSH**: enters the admin workbench with the token auto-injected (left = File Manager / My Tasks, right = DSH). **Do not** open `127.0.0.1:46000` directly (returns 404).

### 3.2 Config card

- **appsettings URL**, **Workspace path**, **External URL**, **Default language**: edit then click **Save**.
> **Important**: on first use set the **Workspace path** in the Config card — it is the root directory where the AI reads/writes files and where new instance workspaces are created (e.g. `E:\WorkBuddy`). Leave it empty to use dsh's default. Click **Save** after editing; new instance workspaces auto-generate as `Workspace path/<username>`.
- **Default language** = the language new users / the login page see first (save to apply; the UI updates immediately).

### 3.3 Instance Manager card (multi-user)

```
User[userA] Workspace[empty = auto global-workspace/username] Password[****]  [Create Instance]
┌────────────┬───────┬─────────────────────┬──────────┬──────────────────────────┐
│ Username   │ Port  │ Workspace           │ State    │ Actions                  │
├────────────┼───────┼─────────────────────┼──────────┼──────────────────────────┤
│ jackzhong  │ 46002 │ E:\WorkBuddy\jackzhong│ Stopped  │ [Start][Delete][Reset]  │
│ ericliu    │ 46003 │ E:\WorkBuddy\ericliu  │ Running  │ [Open][Stop][Delete][Reset]│
└────────────┴───────┴─────────────────────┴──────────┴──────────────────────────┘
```

- **Create instance**: fill username, workspace (empty = auto `global-workspace/<username>`), password, click **Create instance**.
- **Start/Stop**: start/stop that user's instance — shows a spinner while working.
- **Open**: open that user's workbench.
- **Delete**: delete the instance (with confirmation).
- **Reset Password**: reset/create that user account's password.

---

## 4. Open the Workbench (split screen)

Click **Open DSH / Open** to enter the workbench (`/work`), a **left/right split**:

```
┌──────────────────────┬───────────────────────────────────────────────────┐
│ ← Back to Admin      Workspace · ericliu          [Tasks][Language]        │  ← top bar
├──────────────────────┼───────────────────────────────────────────────────┤
│ [File Manager][My Tasks]                                                 │
│  ← left pane (drag/resize)        DSH chat (right pane)                    │
│  file manager content             Explore the unknown…                     │
│                                   Describe what you want to build…         │
└──────────────────────┴───────────────────────────────────────────────────┘
```

- Top bar: **Back to Admin**, workbench title (current user), **Tasks** (admin), **Language**.
- **Left pane width**: drag the divider, or click **⤢** to toggle 30% ↔ 70%.
- **Left pane tabs**: File Manager, My Tasks.

---

## 5. File Manager (left pane)

Switch to the **File Manager** tab:

```
[Upload] [Refresh] [New Folder]                  Current dir: /
┌─────────────────────────────────────┐
│ [+] 📁 Project Docs    Zip Rename Move Delete │
│      📄 readme.txt     Open  Zip Rename Move Delete │
│ [+] 📁 Images          Zip Rename Move Delete │
└─────────────────────────────────────┘
```

- **Upload**: pick local files to upload into the current directory.
- **Refresh**: reload the list.
- **New Folder**: create a folder by name.
- **Folders**: click `+` to expand subdirectories (multi-level tree).
- **File actions**: zip / unzip / rename / move / delete.
- **Preview**: click a file name — previewable files (images/text/video) open inline; non-previewable ones prompt to download.

---

## 6. My Tasks (left pane)

Switch to the **My Tasks** tab to see tasks assigned to the **current logged-in user**:

```
┌──────────────────────────────────────────────────┐
│ No. Type     Name            Status     Files    │
│ 1    Test    Project plan    Processing img_xxx.png│
└──────────────────────────────────────────────────┘
```

- **Task name** is a link: click to pop up the task's **rich content** (with images; double-click an image for full-screen preview).
- **Related files** are links: images preview directly, other types prompt to download.
- Status: Processing / Done / Cancelled.

---

## 7. Task Assignment page /tasks (admin)

Click **Tasks** (top of the console or workbench) to enter `/tasks`:

```
┌──────────┬────────────────────────────────────────────────────────────┐
│ Members  │  Current member: ericliu              [English Language]    │
│ Admin    │  [Add Task]                                                 │
│ jackzhong│ ┌──────────────────────────────────────────────────────┐   │
│ ericliu  │ │ No. Type   Name           Status   Files     Actions  │   │
│ sofiali  │ │ 1    Test  Project plan   Processing xxx.png [Edit][Delete] │   │
│ bookge   │ └──────────────────────────────────────────────────────┘   │
└──────────┴────────────────────────────────────────────────────────────┘
```

- Left **member list**: pick a member, their tasks show on the right.
- **Add Task / Edit**: opens a form:
  - Task **Type**, **Name**
  - **Content**: rich text — bold/lists/quote; **paste or insert images** (stored in `TaskData/Doc`); double-click an image for full-screen preview
  - **Status**: Processing / Done / Cancelled
  - **Related files**: multi-select upload; stored in that member's workspace `TaskData/Doc`
- Tasks are stored as **XML** under that member's workspace `TaskData/tasks-<id>.xml`.

---

## 8. Language switching (ZH/EN)

- A language dropdown (中文 / English) appears top-right on every page.
- After switching, **all UI strings** (buttons, column headers, statuses, labels) across the console, workbench, file manager, task assignment, My Tasks, and the task form switch instantly.
- The login page defaults to the configured default language; users may switch.

---

## 9. Quick reference

| I want to… | How |
|------------|-----|
| Use DSH | Log in → click **Open DSH** → workbench, right side is DSH |
| Upload/manage files | Workbench left pane **File Manager** → upload/new/zip/rename/move |
| See my tasks | Workbench left pane **My Tasks** → click task-name for details, click files to open/download |
| Assign tasks (admin) | Top **Tasks** → pick a member → Add task (rich text + images + files) |
| Start a user's instance | Console **Instance Manager** → that row's **Start** |
| Change the login language | Switch the language dropdown top-right |
| Fix "authentication required" on the right | Start/Stop that instance again from the launcher (restart refreshes the token) |

---

> If anything misbehaves, first **Ctrl+F5** to hard-refresh (clear cache); for instance issues, **Stop** then **Start** that user's instance.
