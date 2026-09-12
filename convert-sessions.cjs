// Convert dsh session .zstd logs into readable per-day Markdown files.
// Walks <admin-workspace>/sharedata/data/sessions-backup/**/*.zstd (all users),
// decompresses, extracts user questions + assistant replies, and writes one
// sessions-<date>.md per day under <admin-workspace>/sharedata/data/sessions-md/.
//
// Backup file naming convention: <timestamp>_<userId>_<sessionId>_session.jsonl.zstd
// The <userId> portion identifies who owned the session.
//
// Idempotent: regenerates all MD from the .zstd sources each run.
const fs = require("fs");
const path = require("path");
const zlib = require("zlib");

const ROOT = path.resolve(__dirname);

// Resolve the admin workspace the same way the launcher does: prefer the
// configured DshWeb.WorkspacePath in appsettings.json; fall back to the portable
// default <root>/WorkSpace. Never hard-code a machine-specific path here.
function readWorkspaceFrom(file) {
  try {
    const cfg = JSON.parse(fs.readFileSync(file, "utf8"));
    const ws = cfg && cfg.DshWeb && cfg.DshWeb.WorkspacePath;
    if (typeof ws === "string" && ws.trim()) return ws.trim();
  } catch (e) { /* ignore missing/invalid file */ }
  return null;
}
function resolveWorkspace() {
  // Match the launcher's precedence: the gitignored local override wins, then
  // appsettings.json, then the portable default <root>/WorkSpace. Reading only
  // appsettings.json missed the runtime-configured WorkspacePath, so the script
  // resolved the wrong workspace and reported "no backup dir" forever.
  return readWorkspaceFrom(path.join(ROOT, "config", "launcher.local.json"))
    || readWorkspaceFrom(path.join(ROOT, "appsettings.json"))
    || path.join(ROOT, "WorkSpace");
}

const WS = resolveWorkspace();
// Raw session backups are written by the launcher under
// <ws>/sharedata/data/sessions-backup; some layouts used an "adminroot" prefix.
// Accept whichever exists so the export keeps working either way.
const BACKUP_DIR = [
  path.join(WS, "adminroot", "sharedata", "data", "sessions-backup"),
  path.join(WS, "sharedata", "data", "sessions-backup"),
].find((d) => fs.existsSync(d)) || path.join(WS, "sharedata", "data", "sessions-backup");
// Per-day Markdown goes where the launcher's GET /api/sessions reads it:
// <ws>/sharedata/data/sessions-md (must match Program.cs exactly).
const OUT_DIR = path.join(WS, "sharedata", "data", "sessions-md");

// Decompress a .zstd JSONL session file -> array of parsed event objects.
function decompressSession(file) {
  const buf = fs.readFileSync(file);
  const frames = [];
  for (let i = 0; i < buf.length; i++) {
    if (buf[i] === 0x28 && buf[i + 1] === 0xb5 && buf[i + 2] === 0x2f && buf[i + 3] === 0xfd) {
      frames.push(i);
    }
  }
  const lines = [];
  for (let i = 0; i < frames.length; i++) {
    const slice = buf.subarray(frames[i], i + 1 < frames.length ? frames[i + 1] : buf.length);
    try {
      const text = zlib.zstdDecompressSync(slice).toString("utf8");
      text.split("\n").forEach((l) => { if (l.trim()) lines.push(l.trim()); });
    } catch (e) { /* skip unreadable frame */ }
  }
  return lines.map((l) => { try { return JSON.parse(l); } catch (e) { return null; } }).filter(Boolean);
}

// Format a Unix ms timestamp / ISO string to YYYY-MM-DD.
function dayKey(ts) {
  try {
    const d = ts > 1e12 ? new Date(ts) : new Date(ts * 1000);
    return d.toISOString().slice(0, 10);
  } catch (e) { return "unknown"; }
}

// Extract userId from backup filename.
// Pattern: <timestamp>_<userId>_<sessionId>_session.jsonl.zstd
// userId may contain underscores (e.g. "admin", "ericliu").
function extractUserFromBackup(filename) {
  // Remove .session.jsonl.zstd suffix
  const base = filename.replace(/_session\.jsonl\.zstd$/, "");
  // Split by underscores — first part is timestamp (digits), last part is sessionId
  // Pattern: 20260909-143000_ericliu_abc123_session.jsonl.zstd
  // After removing suffix: 20260909-143000_ericliu_abc123
  // Timestamp is always 8 digits + dash + 6 digits = 15 chars
  const parts = base.split("_");
  if (parts.length >= 3) {
    // parts[0] = timestamp, parts[1..n-2] = userId (may have underscores), parts[n-1] = sessionId
    // Timestamp is 15 chars (20260909-143000), so take everything after first part minus last part
    const userId = parts.slice(1, -1).join("_");
    return userId || "unknown";
  }
  return "unknown";
}

// Extract the session id from the backup filename (the last underscore-separated
// field). Using the parent directory named every session "sessions-backup"
// because the backups sit flat under one folder.
function extractSessionIdFromBackup(filename) {
  const base = filename.replace(/_session\.jsonl\.zstd$/, "");
  const parts = base.split("_");
  return parts.length >= 2 ? parts[parts.length - 1] : base;
}

function collectConversation(lines) {
  let title = "";
  let createdDay = null;
  const turns = [];
  for (const o of lines) {
    if (o.type === "session/title" && o.data && o.data.title) title = o.data.title;
    if (o.type === "session" && o.data && o.data.createdAt) createdDay = dayKey(o.data.createdAt);
    if (o.type === "user/message" && o.data && o.data.source && o.data.source.kind === "user") {
      const text = (o.data.content || []).filter((c) => c.type === "text").map((c) => c.text).join("\n").trim();
      if (text) turns.push({ role: "user", text });
    }
    if (o.type === "assistant/message" && o.data && o.data.message) {
      const text = (o.data.message.content || []).filter((c) => c.type === "text").map((c) => c.text).join("\n").trim();
      if (text) turns.push({ role: "assistant", text });
    }
  }
  return { title, createdDay, turns };
}

function main() {
  if (!fs.existsSync(BACKUP_DIR)) { console.log("[convert-sessions] no backup dir"); return; }
  fs.mkdirSync(OUT_DIR, { recursive: true });

  const byDay = new Map(); // day -> array of {title, turns, id, user}
  const files = [];
  (function walk(d) {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      const p = path.join(d, e.name);
      if (e.isDirectory()) walk(p);
      else if (e.name.endsWith(".zstd")) files.push(p);
    }
  })(BACKUP_DIR);

  for (const file of files) {
    try {
      const lines = decompressSession(file);
      const { title, createdDay, turns } = collectConversation(lines);
      if (!turns.length) continue;
      const day = createdDay || dayKey(fs.statSync(file).mtimeMs);
      const sessionId = extractSessionIdFromBackup(path.basename(file));
      const user = extractUserFromBackup(path.basename(file));
      if (!byDay.has(day)) byDay.set(day, []);
      byDay.get(day).push({ title, turns, id: sessionId, user });
    } catch (e) { /* skip bad session */ }
  }

  let count = 0;
  for (const [day, sessions] of byDay) {
    const out = path.join(OUT_DIR, `sessions-${day}.md`);
    const md = [];
    md.push(`# Sessions (${day})`);
    md.push("");
    let n = 0;
    for (const s of sessions) {
      n++;
      md.push(`## Session ${n}${s.title ? ": " + s.title : ""}`);
      md.push(`User: **${s.user}** | ID: ${s.id}`);
      md.push("");
      for (const t of s.turns) {
        md.push(`### ${t.role === "user" ? "User" : "AI"}`);
        md.push("");
        md.push(t.text);
        md.push("");
      }
    }
    fs.writeFileSync(out, md.join("\n"), "utf8");
    console.log(`[convert-sessions] wrote ${out} (${sessions.length} sessions)`);
    count++;
  }
  console.log(`[convert-sessions] done (${count} day file(s), ${byDay.size > 0 ? "ok" : "none"})`);
}

main();
