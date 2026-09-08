# TakeTopDSH Team — User Guide

TakeTopDSH Team is a **web-based multi-user AI collaboration platform** designed for software development and office teamwork. Team members access AI-assisted coding, document editing, and task management directly from their browser — **team experience data accumulates and is shared across the team**, getting smarter over time.

**Key Features**:
- 🌐 **Web-based** — Runs in the browser, no client installation needed, accessible from anywhere.
- 👥 **Multi-user collaboration** — Multiple users online simultaneously, each with an isolated workspace.
- 📚 **Experience accumulation** — Conversations, files, and task data are persistently stored; knowledge is reusable.
- 🚀 **One-click install** — Copy and run, no compilation or .NET runtime required.
- 🖱️ **Full graphical interface** — All operations via mouse clicks; intuitive and easy to learn.

> 📌 **Note**: This documentation is text-only. **The best way to experience it is to try the software!** Run `start.bat` and visit `http://127.0.0.1:46001` to see it in action.

---

This guide is for **daily users**. It covers: Login → Open Workspace → File Manager / My Tasks → Admin task assignment, instance management, language switching.

> **Prerequisite**: Run the startup script to launch the launcher service before accessing `http://127.0.0.1:46001` in your browser.
> - Windows: Double-click **`start.bat`**
> - macOS / Linux: Run **`./start.sh`**
> - As long as the launcher is running in the background, you can re-open the browser to continue. If the launcher has stopped (e.g. after a reboot), run the script again.

> See《安装说明.md / INSTALL.md》for installation instructions; this file explains **how to use it after installation**.

---

## Table of Contents

- [1. Overview](#1-overview)
- [2. Login](#2-login)
- [3. Launcher Console (Admin View)](#3-launcher-console-admin-view)
- [4. Open Workspace / Split Screen](#4-open-workspace--split-screen)
- [5. File Manager (Left Pane)](#5-file-manager-left-pane)
- [6. My Tasks (Left Pane)](#6-my-tasks-left-pane)
- [7. Task Assignment Page /tasks (Admin)](#7-task-assignment-page-tasks-admin)
- [8. Language Switching (Chinese / English)](#8-language-switching-chinese--english)
- [9. Quick Reference](#9-quick-reference)

---

## 1. Overview

Access `http://127.0.0.1:46001` to enter the launcher. The core interface has two parts:

- **Launcher Console** (home page): Start/Stop, Open DSH, Configuration, Instance Management.
- **Workspace** (click "Open DSH" to enter): Split-screen view with File Manager / My Tasks on the left, DSH chat interface on the right.

```
+--------------------------------------------------------------------------+
| TakeTopDSH Team Launcher                                admin (admin) En |  <- Top bar: current user + language
+--------------------------------------------------------------------------+
| Launcher card:  Port[46000] [Start] [Stop] [Open DSH]     ● Running     |
| Config card:    appsettings URL / Workspace / Default Language  [Save]   |
| Instance Mgmt:  Username Port Workspace Status Actions                  |
|   ******      46002  ...  Stopped  [Start][Delete][Reset Password]     |
|   ******      46003  ...  Running  [Open][Stop][Delete][Reset Password] |
|   ******      46004  ...  Stopped  [Start]...                          |
|   ******      46005  ...  Stopped  [Start]...                          |
+--------------------------------------------------------------------------+
```

---

## 2. Login

1. Open `http://127.0.0.1:46001` to enter the login page.
2. Select language (top-right), enter **username / password**, click [Login].
   - Login page default language = system default language; can be switched.
3. After login, the top-right shows the current username (admin shows `admin (admin)`).

> Accounts are created by admin via "Instance Management" / "Reset Password". Login passwords are set by the admin.

**Default accounts (initial passwords; change them ASAP via the console):**

| Username | Default Password | Notes |
|----------|------------------|-------|
| admin | 12345678 | Admin (launcher console / task assignment) |
| ****** | (set by admin) | Regular user, instance 46002 |
| ****** | (set by admin) | Regular user, instance 46003 |
| ****** | (set by admin) | Instance 46004 |
| ****** | (set by admin) | Instance 46005 |

> **Admin default login password is 12345678**. Use "Instance Management → Reset Password" to reset other accounts.

---

## 3. Launcher Console (Admin View)

### 3.1 Launcher Card (Admin Default DSH)

```
Port [46000]   [Start]   [Stop]   [Open DSH]
              ● Running (PID 11384)
```

- **Port**: Admin default instance port (default 46000).
- **Start / Stop**: Start/stop the admin default DSH. The button shows a **spinner** while processing.
- **Open DSH**: Opens the admin workspace with token auto-injected (left: File Manager / My Tasks + right: DSH). **Do not** access `127.0.0.1:46000` directly (will show 404).

### 3.2 Config Card

- **appsettings URL**, **Workspace path**, **External access URL**, **Default Language**: Modify and click [Save].
- **Default Language** = language shown on the login page / first seen by new users (takes effect immediately after save).
> **Important**: On first use, set the **Workspace path** in the Config card — this is the root directory for AI file reading/writing and new instance workspaces (e.g. E:\WorkBuddy). Leave empty to use the DSH default directory. After saving, new instance workspaces are auto-created as "Workspace path/username".

### 3.3 Instance Management Card (Multi-user)

```
Username[userA] Workspace [leave empty = auto global workspace/username] Password[****]  [Create Instance]
+------------+-------+--------------------+----------+---------------------------+
| Username   | Port  | Workspace          | Status   | Actions                   |
+------------+-------+--------------------+----------+---------------------------+
| ******   | 46002 | E:\WorkSpace\****** | Stopped  | [Start] [Delete] [Reset] |
| ******   | 46003 | E:\WorkSpace\******   | Running  | [Open][Stop][Delete][Reset]|
+------------+-------+--------------------+----------+---------------------------+
```

- **Create Instance**: Enter username, workspace (leave empty = auto "global workspace/username"), password, click [Create Instance].
- **Start/Stop**: Start/stop that user's instance; button shows spinner while processing.
- **Open**: Open that user's workspace.
- **Delete**: Delete the instance (requires confirmation).
- **Reset Password**: Reset/create that user's account password.

---

## 4. Open Workspace / Split Screen

Click any "Open DSH / Open" to enter the workspace (`/work`), which is a **split-screen view**:

```
+----------------------+---------------------------------------------------+
| <- Back to Admin  Workspace · ericliu              [Tasks] [En v]     |  <- Top bar
+----------------------+---------------------------------------------------+
| [File Manager] [My Tasks] |                                               |
|  <- Left pane (draggable) |        DSH chat interface (right pane)          |
|  File Manager content     |   Into the Unknown                              |
|                           |   Input: Describe what you want to build...    |
+----------------------+---------------------------------------------------+
```

- Top bar: **Back to Admin**, workspace title (shows current user), **Tasks** (admin), **Language switching**.
- **Left pane width**: Drag the middle vertical bar to adjust; click the **arrow** button on the divider to toggle between 30% / 70%.
- **Left pane tabs**: File Manager, My Tasks.

---

## 5. File Manager (Left Pane)

Switch to the [File Manager] tab in the left pane:

```
[Upload] [Refresh] [New Folder]           Current directory: /
+-------------------------------------+
| [+] Project Documents        Zip Rename Move Delete |
|      readme.txt              Open Zip Rename Move Delete |
| [+] Images                   Zip Rename Move Delete |
+-------------------------------------+
```

- **Upload**: Select local files to upload to the current directory.
- **Refresh**: Reload the file list.
- **New Folder**: Enter a name to create a folder.
- **Folders**: Click `+` to expand subdirectories; supports multi-level tree.
- **File operations**: Zip / Unzip / Rename / Move / Delete.
- **Preview**: Click a filename; previewable files (images/text/video) open directly; non-previewable files prompt for download.

---

## 6. My Tasks (Left Pane)

Switch to the [My Tasks] tab in the left pane to list tasks assigned by the admin to **the current user**:

```
+----------------------------------------------+
| No.  Type  Name                  Status  Files  |
| 1    Test  Project plan feature  Active  img.png |
+----------------------------------------------+
```

- **Task name** is a link: click to open a popup showing the task's **rich text content** (including images; double-click images for fullscreen preview).
- **Related files** are links: images open directly for preview; other types prompt for download.
- Status: Processing / Done / Cancelled.

---

## 7. Task Assignment Page /tasks (Admin)

Click [Tasks] in the launcher console top-right or workspace top bar to enter `/tasks`:

```
+----------+------------------------------------------------------------+
| Members  |  Current member: ericliu                     [English v]  |
| Admin    |  [Add Task]                                               |
| ******   | +------------------------------------------------------+ |
| ******   | | No. Type  Name                  Status Files Actions | |
| ******   | | 1   Test  Project plan feature  Active  xxx.png [Edit][Del] | |
| ******   | +------------------------------------------------------+ |
+----------+------------------------------------------------------------+
```

- Left **member list**: Select a member to view their tasks on the right.
- **Add Task / Edit**: Opens the form:
  - Task Type, Task Name
  - **Task Content**: Rich text with bold/lists/quotes; **paste or insert images** (stored in `TaskData/Doc`); double-click images for fullscreen preview
  - **Status**: Processing / Done / Cancelled
  - **Related Files**: Multiple file upload; files stored in that member's workspace `TaskData/Doc`
- Tasks are saved as **XML** in the member's workspace `TaskData/tasks-<id>.xml`.

---

## 8. Language Switching (Chinese / English)

- Each page has a language dropdown in the top-right (Chinese / English).
- Switching updates **all UI text** (buttons, column headers, status labels, etc.) across the launcher, workspace, file manager, task assignment, my tasks, and task edit modals.
- Login page default language = system "Default Language"; regular users can switch.

---

## 9. Quick Reference

| I want to... | How to do it |
|--------------|--------------|
| Use DSH | Login → Click [Open DSH] → Enter workspace, right pane is the DSH chat |
| Upload/manage files | Workspace left pane [File Manager] → Upload/New/Zip/Rename/Move |
| View tasks assigned to me | Workspace left pane [My Tasks] → Click task name for details, click files to open/download |
| Assign tasks to users (Admin) | Top bar [Tasks] → Select member → Add task (rich text + images + files) |
| Start a user's instance | Console "Instance Management" → That row's [Start] |
| Change login language | Top-right language dropdown to English/Chinese |
| Fix "authentication required" in right pane | Use launcher [Start/Stop] for that instance to refresh the token |

---

> If anything goes wrong, try **Ctrl+F5 hard refresh** to clear cache. For instance issues, [Stop] then [Start] that user's instance.
