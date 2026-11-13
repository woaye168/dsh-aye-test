#!/bin/sh
set -eu
: "${DSH_SUPERAGENT_DIR:?missing ticket directory}"
: "${DSH_SUPERAGENT_NAME:?missing ticket name}"
: "${DSH_SUPERAGENT_TICKET:?missing ticket id}"
: "${DSH_SUPERAGENT_GITHUB_REPOSITORY:?missing GitHub repository}"
: "${SCE_CMD:?set SCE_CMD to the machine's sce.cmd path}"
REPO=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
case "$DSH_SUPERAGENT_DIR" in
  /mnt/[a-z]/*) ;;
  *) echo "Ticket directory must be on a Windows drive mounted by WSL: $DSH_SUPERAGENT_DIR" >&2; exit 1;;
esac
if [ -e "$DSH_SUPERAGENT_DIR" ]; then
  if [ -f "$DSH_SUPERAGENT_DIR/src/WasiCoreSDK.props" ]; then exit 0; fi
  echo "Ticket directory exists but is not initialized: $DSH_SUPERAGENT_DIR" >&2
  exit 1
fi
cleanup() {
  status=$?
  if [ "$status" -ne 0 ] && [ -d "$DSH_SUPERAGENT_DIR" ] && git -C "$DSH_SUPERAGENT_DIR" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    git -C "$REPO" worktree remove --force "$DSH_SUPERAGENT_DIR" || true
  fi
  exit "$status"
}
trap cleanup EXIT
mkdir -p "$(dirname "$DSH_SUPERAGENT_DIR")"
git -C "$REPO" fetch --no-tags "https://github.com/$DSH_SUPERAGENT_GITHUB_REPOSITORY.git" "${DSH_SUPERAGENT_GITHUB_PR_HEAD:-HEAD}:refs/dsh-superagent/$DSH_SUPERAGENT_TICKET"
if git -C "$REPO" show-ref --verify --quiet "refs/heads/agent/$DSH_SUPERAGENT_NAME"; then
  git -C "$REPO" worktree add "$DSH_SUPERAGENT_DIR" "agent/$DSH_SUPERAGENT_NAME"
else
  git -C "$REPO" worktree add -b "agent/$DSH_SUPERAGENT_NAME" "$DSH_SUPERAGENT_DIR" "refs/dsh-superagent/$DSH_SUPERAGENT_TICKET"
fi
WIN_DIR=$(wslpath -w "$DSH_SUPERAGENT_DIR")
WIN_SCRIPT=$(wslpath -w "$REPO/agent/Invoke-ScePrepare.ps1")
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$WIN_SCRIPT" -ProjectPath "$WIN_DIR" -SceCommand "$SCE_CMD" -Offline
