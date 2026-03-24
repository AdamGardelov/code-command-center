#!/usr/bin/env bash
set -euo pipefail

REPO="AdamGardelov/code-command-center"
INSTALL_DIR="/usr/local/bin"
BINARY="ccc"

# Detect platform
OS="$(uname -s)"
ARCH="$(uname -m)"

case "$OS" in
  Linux)  RID="linux-x64" ;;
  Darwin)
    case "$ARCH" in
      arm64) RID="osx-arm64" ;;
      *)     RID="osx-x64" ;;
    esac
    ;;
  *) echo "Unsupported OS: $OS" >&2; exit 1 ;;
esac

echo "Detected platform: $RID"

# Get latest version
LATEST=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" | grep '"tag_name"' | cut -d'"' -f4)
echo "Latest version: $LATEST"

# Download and extract
TMP_DIR=$(mktemp -d)
trap 'rm -rf "$TMP_DIR"' EXIT

ARCHIVE="ccc-${RID}.tar.gz"
URL="https://github.com/$REPO/releases/download/${LATEST}/${ARCHIVE}"

echo "Downloading $URL..."
curl -fsSL "$URL" -o "$TMP_DIR/$ARCHIVE"
tar -xzf "$TMP_DIR/$ARCHIVE" -C "$TMP_DIR"

# Install
if [ -w "$INSTALL_DIR" ]; then
  cp "$TMP_DIR/$BINARY" "$INSTALL_DIR/$BINARY"
  chmod +x "$INSTALL_DIR/$BINARY"
else
  echo "Installing to $INSTALL_DIR (requires sudo)..."
  sudo install -m 755 "$TMP_DIR/$BINARY" "$INSTALL_DIR/$BINARY"
fi

echo "Installed $BINARY $LATEST to $INSTALL_DIR/$BINARY"

# Install notification hook script
HOOK_DIR="$HOME/.ccc/hooks"
HOOK_URL="https://raw.githubusercontent.com/$REPO/main/hooks/ccc-state.sh"
mkdir -p "$HOOK_DIR"
HOOK_PATH="$HOOK_DIR/ccc-state.sh"
if [ -f "$HOOK_PATH" ]; then
  echo "Hook already exists at $HOOK_PATH — skipping (remove it manually to reinstall)."
else
  echo "Downloading notification hook..."
  if curl -fsSL "$HOOK_URL" -o "$HOOK_PATH"; then
    chmod +x "$HOOK_PATH"
    echo "Installed hook to $HOOK_PATH"
    echo ""
    echo "To enable notifications, add hooks to ~/.claude/settings.json:"
    echo "  See https://github.com/$REPO#notification-hooks"
  else
    echo "Note: Could not download hook script. See README for manual setup."
  fi
fi
