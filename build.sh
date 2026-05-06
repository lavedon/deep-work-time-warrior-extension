#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$ROOT_DIR/deepwork.csproj"
APP_NAME="deepwork"
CONFIGURATION="${CONFIGURATION:-Release}"

usage() {
  cat <<'EOF'
Usage: ./build.sh [command] [args]

Commands:
  build       Restore and build the project in Release mode
  publish     Build a native AOT binary into artifacts/publish/<rid> (default)
  run         Run the app with dotnet run. Remaining args are passed to the app
  install     Publish, then install the binary to ~/.local/bin/deepwork
  clean       Remove bin, obj, and artifacts
  help        Show this help

Environment variables:
  CONFIGURATION   Build configuration. Default: Release
  RID             Runtime identifier. Auto-detected by default
  PUBLISH_DIR     Publish output directory. Default: artifacts/publish/<rid>
  INSTALL_DIR     Install directory. Default: ~/.local/bin

Examples:
  ./build.sh
  ./build.sh publish
  ./build.sh run -- --help
  ./build.sh run -- blocks --days 7
  ./build.sh install
  RID=linux-x64 ./build.sh publish
EOF
}

detect_rid() {
  local arch libc_suffix

  case "$(uname -m)" in
    x86_64|amd64) arch="x64" ;;
    aarch64|arm64) arch="arm64" ;;
    *)
      echo "Unsupported architecture: $(uname -m)" >&2
      exit 1
      ;;
  esac

  if { ldd --version 2>&1 || true; } | grep -qi musl; then
    libc_suffix="musl-"
  else
    libc_suffix=""
  fi

  echo "linux-${libc_suffix}${arch}"
}

RID="${RID:-$(detect_rid)}"
PUBLISH_DIR="${PUBLISH_DIR:-$ROOT_DIR/artifacts/publish/$RID}"
COMMAND="${1:-publish}"

run_build() {
  echo "==> Restoring $PROJECT"
  dotnet restore "$PROJECT"

  echo "==> Building $APP_NAME ($CONFIGURATION)"
  dotnet build "$PROJECT" -c "$CONFIGURATION" --no-restore
}

run_publish() {
  echo "==> Publishing native AOT binary"
  echo "    RID: $RID"
  echo "    Output: $PUBLISH_DIR"

  dotnet publish "$PROJECT" \
    -c "$CONFIGURATION" \
    -r "$RID" \
    --self-contained true \
    -p:PublishAot=true \
    -o "$PUBLISH_DIR"

  chmod +x "$PUBLISH_DIR/$APP_NAME"

  echo
  echo "Published binary: $PUBLISH_DIR/$APP_NAME"
  echo "Try: $PUBLISH_DIR/$APP_NAME --help"
}

run_install() {
  run_publish

  local install_dir="${INSTALL_DIR:-$HOME/.local/bin}"
  mkdir -p "$install_dir"
  cp "$PUBLISH_DIR/$APP_NAME" "$install_dir/$APP_NAME"
  chmod +x "$install_dir/$APP_NAME"

  echo
  echo "Installed: $install_dir/$APP_NAME"
  if [[ ":$PATH:" == *":$install_dir:"* ]]; then
    echo "Run: $APP_NAME --help"
  else
    echo "Note: $install_dir is not on PATH. Add it or run with the full path above."
  fi
}

run_clean() {
  echo "==> Removing build output"
  rm -rf "$ROOT_DIR/bin" "$ROOT_DIR/obj" "$ROOT_DIR/artifacts"
}

case "$COMMAND" in
  build)
    run_build
    ;;
  publish)
    run_publish
    ;;
  run)
    shift
    if [[ "${1:-}" == "--" ]]; then
      shift
    fi
    dotnet run --project "$PROJECT" -- "$@"
    ;;
  install)
    run_install
    ;;
  clean)
    run_clean
    ;;
  help|-h|--help)
    usage
    ;;
  *)
    echo "Unknown command: $COMMAND" >&2
    echo >&2
    usage >&2
    exit 1
    ;;
esac
