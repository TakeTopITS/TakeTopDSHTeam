#!/bin/bash
# TakeTopDSH Team launcher (Linux / macOS)
# First run: unpacks the platform Node and installs @deepseek-ai/dsh (needs
# network once). Then starts the self-hosted C# launcher (browser UI).
set -e

cd "$(dirname "$0")"
PROJ="$PWD"
NODE_DIR="$PROJ/node"

export DSH_HOME="$PROJ/.dsh"

# --- detect platform -------------------------------------------------------
OS="$(uname -s)"
ARCH="$(uname -m)"
case "$OS" in
  Linux)
    case "$ARCH" in
      x86_64|amd64) PLATFORM="linux-x64"; TAR="$NODE_DIR/platforms/linux-x64.tar.xz"; ;;
      aarch64|arm64) PLATFORM="linux-arm64"; TAR="$NODE_DIR/platforms/linux-arm64.tar.xz"; ;;
      *) echo "[!] Unsupported Linux arch: $ARCH"; exit 1; ;;
    esac ;;
  Darwin)
    case "$ARCH" in
      arm64) PLATFORM="macos-arm64"; TAR="$NODE_DIR/platforms/macos-arm64.tar.gz"; ;;
      *) echo "[!] Unsupported macOS arch: $ARCH"; exit 1; ;;
    esac ;;
  *) echo "[!] Unsupported OS: $OS"; exit 1 ;;
esac

# --- unpack Node if needed -------------------------------------------------
NODE_ROOT="$NODE_DIR/$PLATFORM"
NODE_BIN="$NODE_ROOT/bin/node"
if [ ! -f "$NODE_BIN" ]; then
  echo "[*] Unpacking Node for $PLATFORM ..."
  mkdir -p "$NODE_ROOT"
  tar -xf "$TAR" -C "$NODE_ROOT"
  # The Node tarball ships a single top-level folder (e.g. node-v24.x-linux-x64).
  # Move its contents up so $NODE_ROOT/bin/node exists. NOTE: a bare
  # `SRC="$NODE_ROOT"/node-v*` assignment does NOT expand the glob, so iterate.
  for d in "$NODE_ROOT"/node-v*; do
    if [ -d "$d" ]; then
      mv "$d"/* "$NODE_ROOT"/ 2>/dev/null || true
      rmdir "$d" 2>/dev/null || true
    fi
  done
fi
[ -f "$NODE_BIN" ] || { echo "[!] Node not found: $NODE_BIN"; exit 1; }

# node has no bin/npm symlink on some packs; invoke npm-cli.js through node.
npm_cmd() { "$NODE_BIN" "$NODE_ROOT/lib/node_modules/npm/bin/npm-cli.js" "$@"; }

# --- install dsh if missing (prefer the bundled offline tarball) ------------
DSH_PKG="$NODE_ROOT/lib/node_modules/@deepseek-ai/dsh"
if [ ! -d "$DSH_PKG" ]; then
  DSH_TAR=""
  for cand in "$NODE_DIR/platforms/dsh-$PLATFORM.tar.xz" "$NODE_DIR/platforms/dsh-$PLATFORM.tar.gz"; do
    if [ -f "$cand" ]; then DSH_TAR="$cand"; break; fi
  done
  if [ -n "$DSH_TAR" ]; then
    echo "[*] Unpacking bundled @deepseek-ai/dsh (offline) ..."
    mkdir -p "$NODE_ROOT/lib"
    case "$DSH_TAR" in
      *.tar.gz) tar -xzf "$DSH_TAR" -C "$NODE_ROOT/lib" ;;
      *.tar.xz) tar -xJf "$DSH_TAR" -C "$NODE_ROOT/lib" ;;
      *)        tar -xf  "$DSH_TAR" -C "$NODE_ROOT/lib" ;;
    esac
  fi
fi
if [ ! -d "$DSH_PKG" ]; then
  echo "[*] Installing @deepseek-ai/dsh (no offline bundle; first run needs network) ..."
  npm_cmd install -g @deepseek-ai/dsh >/dev/null 2>&1 || {
    echo "[!] npm install failed. Check network and retry."; exit 1;
  }
fi

# --- launch the C# launcher -------------------------------------------------
OS_LC="$OS"
if [ "$OS" = "Linux" ]; then SUB="$PLATFORM"; else SUB="osx-arm64"; fi
BIN="$PROJ/dsh-launcher/$SUB/TakeTopDshLauncher"
if [ ! -f "$BIN" ]; then
  echo "[!] Launcher not found for this platform: $BIN"
  exit 1
fi
chmod +x "$BIN"

# --- apply the brand patch (deepseek -> TakeTopDSH) -------------------------
# Re-applies on every start so the UI brand survives dsh upgrades. Idempotent.
if [ -f "$NODE_BIN" ] && [ -f "$PROJ/patch-taketop-brand.cjs" ]; then
  echo "[*] Applying brand patch (deepseek -> TakeTopDSH) ..."
  "$NODE_BIN" "$PROJ/patch-taketop-brand.cjs" || echo "[!] brand patch skipped (non-fatal)"
fi

# --- idempotent: if the launcher is already listening on :46001, just open it ---
LAUNCHER_URL="http://127.0.0.1:46001"
port_in_use() {
  # Prefer `ss` (Linux); fall back to `lsof` (macOS/BSD).
  if command -v ss >/dev/null 2>&1; then
    ss -tln 2>/dev/null | grep -qE "[:.]46001[[:space:]]"
  elif command -v lsof >/dev/null 2>&1; then
    lsof -iTCP:46001 -sTCP:LISTEN >/dev/null 2>&1
  else
    (exec 3<>/dev/tcp/127.0.0.1/46001) >/dev/null 2>&1 && exec 3>&-
  fi
}
if port_in_use; then
  echo "[*] Launcher already running. Opening $LAUNCHER_URL ..."
  open_browser() {
    if command -v xdg-open >/dev/null 2>&1; then xdg-open "$1"; elif command -v open >/dev/null 2>&1; then open "$1"; else echo "[*] Open $1 in your browser."; fi
  }
  open_browser "$LAUNCHER_URL"
  exit 0
fi

echo "[*] Starting TakeTopDSH Team launcher ..."

# Check if running as root (required for OS user creation)
if [ "$(id -u)" -ne 0 ]; then
  echo "[*] Requesting sudo for OS user isolation..."
  exec sudo "$0" "$@"
fi

# Detach into a NEW session so the launcher AND its DSH child survive the
# terminal/session closing. Plain `nohup ... &` only protects the launcher:
# when the launching shell exits, the DSH child still receives SIGHUP and dies.
if command -v setsid >/dev/null 2>&1; then
  setsid "$BIN" >/dev/null 2>&1 < /dev/null &
else
  nohup "$BIN" >/dev/null 2>&1 &
fi
echo "[*] Started (PID $!). Open http://127.0.0.1:46001 in your browser."
