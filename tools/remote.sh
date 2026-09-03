#!/usr/bin/env bash
set -euo pipefail

# One entry point for the banxia lab machines. Credentials stay outside the
# repository: password profiles use ~/.ssh/askpass.sh via SSH_ASKPASS.

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
CONTROL_DIR=${DSH_SSH_CONTROL_DIR:-${XDG_RUNTIME_DIR:-/tmp}/banxia-ssh}
KNOWN_HOSTS=${DSH_SSH_KNOWN_HOSTS:-${SCRIPT_DIR}/ssh_known_hosts}
SSH_KEY=${DSH_SSH_KEY:-${HOME}/.ssh/id_ed25519}
ASKPASS=${DSH_SSH_ASKPASS:-${HOME}/.ssh/askpass.sh}
CONNECT_TIMEOUT=${DSH_SSH_CONNECT_TIMEOUT:-8}
CONTROL_PERSIST=${DSH_SSH_CONTROL_PERSIST:-10m}
EMU_ADB=${DSH_EMU_ADB:-\$HOME/banxia-tools/platform-tools/adb}
EMU_SERIAL=${DSH_EMU_SERIAL:-emulator-5554}
PHONE_PACKAGE=${DSH_PHONE_PACKAGE:-com.lingxi.banxia.phone}

usage() {
  cat <<'EOF'
Usage:
  tools/remote.sh check
  tools/remote.sh exec PROFILE 'REMOTE COMMAND'
  tools/remote.sh adb [ADB ARGS...]
  tools/remote.sh screenshot [LOCAL PNG]
  tools/remote.sh logcat [ADB LOGCAT ARGS...]
  tools/remote.sh build status|start|stop
  tools/remote.sh phone-stop
  tools/remote.sh emu-release
  tools/remote.sh close
  tools/remote.sh ssh-close

Profiles: build (192.168.5.55), emu (192.168.5.21), nas (192.168.5.88).
Set DSH_SSH_ASKPASS or DSH_SSH_KEY to override local credential paths.
The default host-key allowlist is tools/ssh_known_hosts.
EOF
}

profile() {
  case "$1" in
    build)
      PROFILE_TARGET='lx@192.168.5.55'
      PROFILE_AUTH='key'
      ;;
    emu)
      PROFILE_TARGET='lingxi@192.168.5.21'
      PROFILE_AUTH='key'
      ;;
    nas)
      PROFILE_TARGET='lingxi@192.168.5.88'
      PROFILE_AUTH='askpass'
      ;;
    *)
      printf 'Unknown profile: %s\n' "$1" >&2
      return 2
      ;;
  esac
}

ssh_options() {
  local profile_name=$1
  profile "$profile_name"
  mkdir -p "$CONTROL_DIR"
  chmod 700 "$CONTROL_DIR"

  if [[ ! -r "$KNOWN_HOSTS" ]]; then
    printf 'Known-hosts file does not exist: %s\n' "$KNOWN_HOSTS" >&2
    printf 'Run: ssh-keyscan -H 192.168.5.21 192.168.5.55 192.168.5.88 >> %s\n' "$KNOWN_HOSTS" >&2
    return 2
  fi

  SSH_OPTIONS=(
    -o ConnectTimeout="$CONNECT_TIMEOUT"
    -o ServerAliveInterval=15
    -o ServerAliveCountMax=2
    -o UserKnownHostsFile="$KNOWN_HOSTS"
    -o StrictHostKeyChecking=yes
    -o ControlMaster=auto
    -o ControlPersist="$CONTROL_PERSIST"
    -o ControlPath="$CONTROL_DIR/%C"
    -T
  )

  if [[ "$PROFILE_AUTH" == key ]]; then
    if [[ ! -r "$SSH_KEY" ]]; then
      printf 'SSH key does not exist: %s\n' "$SSH_KEY" >&2
      return 2
    fi
    SSH_OPTIONS+=(
      -i "$SSH_KEY"
      -o BatchMode=yes
      -o PreferredAuthentications=publickey
      -o PubkeyAuthentication=yes
    )
  else
    if [[ ! -x "$ASKPASS" ]]; then
      printf 'SSH askpass helper is missing or not executable: %s\n' "$ASKPASS" >&2
      printf 'Set DSH_SSH_ASKPASS to a local executable helper.\n' >&2
      return 2
    fi
    export DISPLAY=${DISPLAY:-:0}
    export SSH_ASKPASS="$ASKPASS"
    export SSH_ASKPASS_REQUIRE=force
    SSH_OPTIONS+=(
      -o PreferredAuthentications=keyboard-interactive,password
      -o PubkeyAuthentication=no
    )
  fi
}

remote_exec() {
  local profile_name=$1
  shift
  if (($# == 0)); then
    printf 'A remote command is required.\n' >&2
    return 2
  fi
  ssh_options "$profile_name"
  # Callers pass one quoted command string. This preserves PowerShell syntax
  # for the Windows build host and shell syntax for Linux test hosts.
  ssh "${SSH_OPTIONS[@]}" "$PROFILE_TARGET" "$*"
}

remote_adb() {
  local command="$EMU_ADB -s $EMU_SERIAL"
  local arg quoted
  for arg in "$@"; do
    printf -v quoted ' %q' "$arg"
    command+="$quoted"
  done
  remote_exec emu "$command"
}

remote_adb_pipe() {
  local command="$EMU_ADB -s $EMU_SERIAL"
  local arg quoted
  for arg in "$@"; do
    printf -v quoted ' %q' "$arg"
    command+="$quoted"
  done
  ssh_options emu
  ssh "${SSH_OPTIONS[@]}" "$PROFILE_TARGET" "$command"
}

check_host() {
  local profile_name=$1
  case "$profile_name" in
    build)
      remote_exec build "powershell -NoProfile -Command \"Write-Output ('BUILD ' + [Environment]::MachineName); Get-Item 'D:\\banxia_build\\Builds\\Banxia-Phone.apk' -ErrorAction SilentlyContinue | Select-Object FullName,LastWriteTime,Length\""
      ;;
    emu|nas)
      remote_exec "$profile_name" 'printf "HOST %s\\n" "$(hostname)"; uname -a; printf "MEMORY\\n"; (free -h 2>/dev/null || true); printf "DISK\\n"; df -h /'
      ;;
  esac
}

case "${1:-}" in
  check)
    check_host build
    check_host emu
    check_host nas
    ;;
  exec)
    [[ $# -ge 3 ]] || { usage; exit 2; }
    remote_exec "$2" "${*:3}"
    ;;
  adb)
    shift
    remote_adb "$@"
    ;;
  screenshot)
    output=${2:-captures/5.21-screen.png}
    mkdir -p "$(dirname -- "$output")"
    remote_adb_pipe exec-out screencap -p > "$output"
    printf 'Wrote %s\n' "$output"
    ;;
  logcat)
    shift
    if (($# == 0)); then
      set -- -d -v threadtime
    fi
    remote_adb logcat "$@"
    ;;
  build)
    case "${2:-}" in
      status)
        remote_exec build "powershell -NoProfile -Command \"Get-Item 'D:\\banxia_build\\Builds\\Banxia-Phone.apk' -ErrorAction SilentlyContinue | Select-Object FullName,LastWriteTime,Length\""
        ;;
      start)
        remote_exec build 'powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/lx/banxia_build_phone_wait.ps1'
        ;;
      stop)
        remote_exec build 'powershell -NoProfile -Command "Get-Process Unity,UnityHub -ErrorAction SilentlyContinue | Stop-Process -Force"'
        ;;
      *)
        usage
        exit 2
        ;;
    esac
    ;;
  phone-stop)
    remote_adb shell am force-stop "$PHONE_PACKAGE"
    ;;
  emu-release)
    remote_exec emu "pkill -f '[q]emu-system'"
    ;;
  close)
    remote_adb shell am force-stop "$PHONE_PACKAGE"
    remote_exec emu "pkill -f '[q]emu-system'"
    ;;
  ssh-close)
    for profile_name in build emu nas; do
      profile "$profile_name"
      ssh_options "$profile_name"
      ssh -O exit "${SSH_OPTIONS[@]}" "$PROFILE_TARGET" 2>/dev/null || true
    done
    ;;
  -h|--help|help|'')
    usage
    ;;
  *)
    usage
    exit 2
    ;;
esac
