// Patch the DSH Web frontend brand wordmark from "deepseek" to "TakeTop".
//
// In the v0.1.2 bundle the brand name is drawn as vector <path> outlines (9
// paths spelling "deepseek") immediately before the whale <g> and the HARNESS
// badge <g>. Because a name cannot be search-replaced through strings, we:
//   1) inject a <text> element at the start of the brand SVG children, and
//   2) delete the 9 vector outline <path> elements right after it (the old
//      "deepseek" letters), leaving only our <text> + whale + badge.
//
// Idempotent: safe to run repeatedly. Also rewrites an older "TakeTopDSH"
// marker to "TakeTop" if one was already injected.
const fs = require("fs");
const path = require("path");

const ROOT = path.resolve(__dirname);
const GLOBS = [
  // Bundled (self-contained) install used by the GUI.
  path.join(ROOT, "node", "node_modules", "@deepseek-ai", "dsh", "node_modules", "@deepseek-ai", "dsh-web-frontend", "dist", "assets", "index-*.js"),
  // Legacy app/ install (if present).
  path.join(ROOT, "app", "node_modules", "@deepseek-ai", "dsh-web-frontend", "dist", "assets", "index-*.js"),
  path.join(ROOT, ".dsh", "profiles", "node_modules", "@deepseek-ai", "dsh-web-frontend", "dist", "assets", "index-*.js"),
];

const MARKER = "TakeTop";
const OLD_MARKER = "TakeTopDSH";
const COMP_ANCHOR = "includeMark:o=!0";
// The whale <g> that follows the deepseek letter paths.
const WHALE_G_OPEN = 'd.jsx("g",{clipPath:"url(#dsh-wordmark-whale-clip)",children:d.jsx(';

// <text> that paints the brand name over the letter zone (x ~26..~128).
function makeTextEl() {
  return (
    'd.jsx("text",{x:"26",y:"19",fontSize:"12.5",fontWeight:"600",' +
    'fill:"currentColor",textLength:"52",lengthAdjust:"spacingAndGlyphs",' +
    'children:"' + MARKER + '"})'
  );
}

function patchFiles() {
  let changed = 0;
  for (const glob of GLOBS) {
    const dir = path.dirname(glob);
    let files = [];
    try {
      files = fs.readdirSync(dir).filter((n) => /^index-.*\.js$/.test(n) && !n.endsWith(".map"));
    } catch (e) {
      continue;
    }
    for (const fname of files) {
      const full = path.join(dir, fname);
      let code;
      try {
        code = fs.readFileSync(full, "utf8");
      } catch (e) {
        continue;
      }
      // Rewrite an old "TakeTopDSH" marker (already injected) to "TakeTop".
      if (code.includes('children:"' + OLD_MARKER + '"')) {
        const old = code.replace('children:"' + OLD_MARKER + '"', 'children:"' + MARKER + '"');
        fs.writeFileSync(full, old, "utf8");
        console.log("[patch-taketop-brand] rebranded " + OLD_MARKER + " -> " + MARKER + " in " + full);
        changed++;
        continue;
      }
      if (code.includes('children:"' + MARKER + '"')) continue;

      const a = code.indexOf(COMP_ANCHOR);
      if (a < 0) continue;

      const childrenOpen = code.indexOf("children:[", a);
      if (childrenOpen < 0) continue;
      const insertAt = childrenOpen + "children:[".length;

      const whale = code.indexOf(WHALE_G_OPEN, childrenOpen);
      if (whale < 0) continue;

      // Everything between insertAt and whale is the vector letter outlines.
      // Replace [insertAt, whale) with our <text> element (plus a trailing comma
      // inserted by the original content's own separator handling).
      const patched = code.slice(0, insertAt) + makeTextEl() + "," + code.slice(whale);
      fs.writeFileSync(full, patched, "utf8");
      console.log("[patch-taketop-brand] patched: " + full);
      changed++;
    }
  }
  console.log(
    changed > 0
      ? "[patch-taketop-brand] done, patched/rebranded " + changed + " bundle(s)."
      : "[patch-taketop-brand] nothing to patch (already TakeTop or not found)."
  );
  return changed;
}

patchFiles();
