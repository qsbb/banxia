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
  tools/remote.sh preflight          # 新会话第一步：全链路自检（红绿榜+修复提示）
  tools/remote.sh exec PROFILE 'REMOTE COMMAND'
  tools/remote.sh adb [ADB ARGS...]          # 已锁定 -s emulator-5554，碰不到物理机
  tools/remote.sh qa CMD [MODE]              # QA 广播（enter_scene/skin_audit/set_mode ...）
  tools/remote.sh push-bin PROFILE LOCAL REMOTE   # 二进制安全推送
  tools/remote.sh pull-bin PROFILE REMOTE LOCAL   # 二进制安全拉取（stdout 流，
                                                  # 绕开 Windows scp 204800 截断）
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

ok()   { printf '  \033[32m✓\033[0m %s\n' "$1"; }
bad()  { printf '  \033[31m✗ %s\033[0m\n' "$1"; }
warn() { printf '  \033[33m! %s\033[0m\n' "$1"; }

# 新会话开机自检：把历次"猛猛碰壁"的机器坑全部前置检查，红绿榜 + 修复提示。
# 设计原则：任何一项失败都给出确切修复命令；不让单点失败中断后续检查。
preflight() {
  local fails=0
  printf '== 本机（容器）==\n'
  if [[ -r "$SSH_KEY" ]]; then ok "ssh key: $SSH_KEY"; else bad "ssh key 缺失: $SSH_KEY"; fails=$((fails+1)); fi
  if [[ -x "$ASKPASS" ]]; then ok "askpass: $ASKPASS"; else warn "askpass 不可执行（nas 连不上时查这个）: $ASKPASS"; fi
  local tmp_free
  tmp_free=$(df -m /tmp | awk 'NR==2{print $4}')
  if [[ "$tmp_free" -ge 200 ]]; then ok "/tmp 余量 ${tmp_free}MB"; else bad "/tmp 只剩 ${tmp_free}MB（256MB tmpfs，大文件别放这；清理或改用 \$HOME）"; fails=$((fails+1)); fi

  printf '== build（192.168.5.55，Windows）==\n'
  if remote_exec build 'echo BUILD_OK' >/dev/null 2>&1; then
    ok "ssh 连通"
    # 注意：构建机是 Windows PowerShell 5.1，不支持三元运算符——用 if/else。
    local bline
    while IFS= read -r bline; do
      case "$bline" in
        OK\ *) ok "${bline#OK }" ;;
        MISSING\ *) bad "${bline#MISSING }"; fails=$((fails+1));;
      esac
    done < <(remote_exec build 'powershell -NoProfile -Command "$paths = @(@(\"D:\Tools\2022.3.62f3c1\Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK\", \"JDK11-OpenJDK\"), @(\"D:\dev\flutter\bin\flutter.bat\", \"flutter\"), @(\"D:\Tools\2022.3.62f3c1\Editor\Unity.exe\", \"Unity\"), @(\"D:\banxia_build\", \"build-dir D:\\\\banxia_build\")); foreach ($p in $paths) { if (Test-Path $p[0]) { Write-Output (\"OK \" + $p[1]) } else { Write-Output (\"MISSING \" + $p[1]) } }"' 2>/dev/null || true)
  else
    bad "ssh 不通——构建机可能关机（需要编译时请用户开机）"; fails=$((fails+1))
  fi

  printf '== emu（192.168.5.21）==\n'
  if remote_exec emu 'echo EMU_OK' >/dev/null 2>&1; then
    ok "ssh 连通"
    local qcount
    qcount=$(remote_exec emu "ps -e -o comm= | grep -c qemu || true" 2>/dev/null | tr -d '\r' || true)
    if [[ "$qcount" =~ ^[0-9]+$ ]] && [[ "$qcount" -ge 1 ]]; then
      ok "qemu 运行中 ($qcount)"
    else
      warn "qemu 未运行——需要模拟器时: remote.sh exec emu '~/banxia-emu/start-emu.sh'（20-35s 开机）"
    fi
    local devices
    devices=$(remote_exec emu "$EMU_ADB devices 2>/dev/null | grep -w device | awk '{print \$1}'" 2>/dev/null | tr -d '\r' || true)
    if printf '%s\n' "$devices" | grep -qx "$EMU_SERIAL"; then
      ok "模拟器在线: $EMU_SERIAL"
    else
      warn "模拟器 $EMU_SERIAL 不在线（qemu 未起或 adb 未认到）"
    fi
    # 物理机地雷：在线则显著警告（命令已锁 -s，但 raw adb 习惯要戒）
    local physical
    physical=$(printf '%s\n' "$devices" | grep -v "$EMU_SERIAL" | grep -v '^$' || true)
    if [[ -n "$physical" ]]; then
      warn "检测到其他 adb 设备在线：$(printf '%s ' $physical)——2G0YC5ZHBF00R0 是 Quest 头显（用户确认），绝不发 reboot/设置命令；本工具已锁定 $EMU_SERIAL"
    fi
  else
    bad "ssh 不通"; fails=$((fails+1))
  fi

  printf '== nas（192.168.5.88）==\n'
  if remote_exec nas 'echo NAS_OK' >/dev/null 2>&1; then
    ok "ssh 连通"
    if remote_exec nas 'test -w /vol2/1000/download-WD40EZRZ/文件传输 && echo W_OK' >/dev/null 2>&1; then
      ok "文件传输目录可写"
    else
      warn "文件传输目录不可写或不存在（发布前必须通）"
    fi
  else
    bad "ssh 不通（nas 走 askpass，查本机 askpass 项）"; fails=$((fails+1))
  fi

  printf '== GitHub ==\n'
  if timeout 15 git -C "$SCRIPT_DIR/.." ls-remote origin main >/dev/null 2>&1; then
    ok "push 通道正常"
  else
    warn "GitHub 当前连不上或超时（常见间歇故障：push 用 timeout 90 + 重试循环）"
  fi

  printf '\n'
  if [[ $fails -eq 0 ]]; then ok "preflight 通过（warn 项按需处理）"; else bad "$fails 项失败，按上面提示修"; return 1; fi
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

# 二进制安全推送。实测结论（2026-09-07，300KB 随机数据哈希比对）：
# - 推到 Windows 用 scp 即可，无损（截断缺陷只影响"拉"方向）；
#   PowerShell stdin 方案在 PS 5.1 下挂死，勿用。
# - 推到 Linux 用 ssh stdin 流（79MB APK 实测哈希一致）。
push_bin() {
  local profile_name=$1 local_path=$2 remote_path=$3
  [[ -f "$local_path" ]] || { printf 'local file missing: %s\n' "$local_path" >&2; return 2; }
  ssh_options "$profile_name"
  case "$profile_name" in
    build)
      scp "${SSH_OPTIONS[@]}" "$local_path" "$PROFILE_TARGET:$remote_path"
      ;;
    *)
      ssh "${SSH_OPTIONS[@]}" "$PROFILE_TARGET" "cat > '${remote_path}'" < "$local_path"
      ;;
  esac
  printf 'Pushed %s -> %s:%s\n' "$local_path" "$profile_name" "$remote_path"
}

# 二进制安全拉取：stdout 流。Windows scp 会在 204800 字节处截断，必须走这条。
pull_bin() {
  local profile_name=$1 remote_path=$2 local_path=$3
  ssh_options "$profile_name"
  mkdir -p "$(dirname -- "$local_path")"
  case "$profile_name" in
    build)
      ssh "${SSH_OPTIONS[@]}" "$PROFILE_TARGET" \
        "powershell -NoProfile -Command \"\$i=[IO.File]::OpenRead('${remote_path}'); \$o=[Console]::OpenStandardOutput(); \$i.CopyTo(\$o); \$o.Flush(); \$i.Close()\"" \
        > "$local_path"
      ;;
    *)
      ssh "${SSH_OPTIONS[@]}" "$PROFILE_TARGET" "cat '${remote_path}'" > "$local_path"
      ;;
  esac
  printf 'Pulled %s:%s -> %s (%s bytes)\n' "$profile_name" "$remote_path" "$local_path" "$(stat -c%s "$local_path")"
}

# QA 广播快捷方式：remote.sh qa enter_scene / qa skin_audit / qa set_mode virtualScene
qa_broadcast() {
  local cmd=$1 mode=${2:-}
  [[ -n "$cmd" ]] || { printf 'usage: remote.sh qa CMD [MODE]\n' >&2; return 2; }
  local intent="am broadcast -a com.lingxi.banxia.phone.QA_COMMAND --es cmd $cmd"
  if [[ -n "$mode" ]]; then
    intent+=" --es mode $mode"
  fi
  remote_adb shell "$intent"
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
  preflight)
    preflight
    ;;
  push-bin)
    [[ $# -eq 4 ]] || { usage; exit 2; }
    push_bin "$2" "$3" "$4"
    ;;
  pull-bin)
    [[ $# -eq 4 ]] || { usage; exit 2; }
    pull_bin "$2" "$3" "$4"
    ;;
  qa)
    shift
    qa_broadcast "$@"
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
