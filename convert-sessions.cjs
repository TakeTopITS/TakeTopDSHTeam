// Convert dsh session .zstd logs into readable per-day Markdown files.
// Walks .dsh/sessions/**/session.jsonl.zstd, decompresses (node:zlib zstd),
// extracts real user questions + assistant text replies, and writes one
// sessions-<date>.md per day under docs/opencode-experience/data/sessions-md/.
//
// Idempotent: regenerates all MD from the .zstd sources each run, so a run can
// be triggered anytime (e.g. after the launcher mirrors sessions).
const fs = require("fs");
const path = require("path");
const zlib = require("zlib");

const ROOT = path.resolve(__dirname);
const DSH_HOME = path.join(ROOT, ".dsh");
const SESSIONS = path.join(DSH_HOME, "sessions");
const OUT_DIR = path.join(ROOT, "docs", "opencode-experience", "data", "sessions-md");

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

function collectConversation(lines) {
  let title = "";
  let createdDay = null;
  const turns = [];
  for (const o of lines) {
    if (o.type === "session/title" && o.data && o.data.title) title = o.data.title;
    if (o.type === "session" && o.data && o.data.createdAt) createdDay = dayKey(o.data.createdAt);
    // Real user question only (source.kind === "user"); skip plugin/system.
    if (o.type === "user/message" && o.data && o.data.source && o.data.source.kind === "user") {
      const text = (o.data.content || []).filter((c) => c.type === "text").map((c) => c.text).join("\n").trim();
      if (text) turns.push({ role: "user", text });
    }
    // Assistant final message: take only text content (skip reasoning/tool-call).
    if (o.type === "assistant/message" && o.data && o.data.message) {
      const text = (o.data.message.content || []).filter((c) => c.type === "text").map((c) => c.text).join("\n").trim();
      if (text) turns.push({ role: "assistant", text });
    }
  }
  return { title, createdDay, turns };
}

function main() {
  if (!fs.existsSync(SESSIONS)) { console.log("[convert-sessions] no sessions dir"); return; }
  fs.mkdirSync(OUT_DIR, { recursive: true });

  const byDay = new Map(); // day -> array of {title, turns, id}
  const files = [];
  (function walk(d) {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      const p = path.join(d, e.name);
      if (e.isDirectory()) walk(p);
      else if (e.name === "session.jsonl.zstd") files.push(p);
    }
  })(SESSIONS);

  for (const file of files) {
    try {
      const lines = decompressSession(file);
      const { title, createdDay, turns } = collectConversation(lines);
      if (!turns.length) continue;
      const day = createdDay || dayKey(fs.statSync(file).mtimeMs);
      const sessionId = path.basename(path.dirname(file));
      if (!byDay.has(day)) byDay.set(day, []);
      byDay.get(day).push({ title, turns, id: sessionId });
    } catch (e) { /* skip bad session */ }
  }

  let count = 0;
  for (const [day, sessions] of byDay) {
    const out = path.join(OUT_DIR, `sessions-${day}.md`);
    const md = [];
    md.push(`# 会话汇总（${day}）`);
    md.push("");
    let n = 0;
    for (const s of sessions) {
      n++;
      md.push(`## 会话 ${n}${s.title ? "：" + s.title : ""}`);
      md.push(`（会话 ID：${s.id}）`);
      md.push("");
      for (const t of s.turns) {
        md.push(`### ${t.role === "user" ? "用户" : "AI"}`);
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
