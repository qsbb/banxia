# 多机连接说明

本项目把开发容器、5.21 测试机、5.55 Unity 构建机和 5.88 NAS/AstrBot 主机分开管理。日常远程调用统一使用 `tools/remote.sh`，避免重复手写 SSH 参数。

## 机器清单

| Profile | 地址 / 用户 | 用途 | 当前认证 |
|---|---|---|---|
| `emu` | `lingxi@192.168.5.21` | Android 模拟器、Quest 3 ADB、手机 APK 盲测 | 本机 `~/.ssh/id_ed25519`（已验证免密） |
| `build` | `lx@192.168.5.55` | Unity `2022.3.62f3c1` Android 构建 | 本机 `~/.ssh/id_ed25519` |
| `nas` | `lingxi@192.168.5.88` | NAS / AstrBot 服务检查与发布 | 本机 `~/.ssh/askpass.sh` |

已验证的 5.21 ADB 设备：

- `emulator-5554`：Android 模拟器
- `adb-2G0YC5ZHBF00R0-zAeOwF._adb-tls-connect._tcp`：Quest 3

5.21 的手机测试默认使用 `emulator-5554`，包名为 `com.lingxi.banxia.phone`。

## 凭据约定

- 密码不写入仓库，也不放进脚本参数。
- 当前密码型连接（目前为 `nas`）默认读取 `~/.ssh/askpass.sh`。该文件只应由当前用户可读/执行，例如：

```bash
chmod 700 ~/.ssh/askpass.sh
chmod 600 ~/.ssh/id_ed25519
```

- 5.55 使用 `~/.ssh/id_ed25519`。如果在另一台开发容器运行，需要通过环境变量指定凭据路径：

```bash
export DSH_SSH_KEY="$HOME/.ssh/id_ed25519"
export DSH_SSH_ASKPASS="$HOME/.ssh/askpass.sh"
```

`tools/ssh_known_hosts` 固定了三台机器的 ED25519 host key，工具默认开启严格 host key 校验。主机重装或更换 SSH host key 后，先人工核对指纹，再更新该文件。

## 日常命令

在 `banxia` 仓库根目录执行：

```bash
# 检查三台机器
./tools/remote.sh check

# 5.21 ADB
./tools/remote.sh adb devices -l
./tools/remote.sh adb shell getprop ro.build.version.release
./tools/remote.sh screenshot captures/5.21-screen.png
./tools/remote.sh logcat -d -v threadtime '*:S' Unity:V Banxia:V

# 5.21 测试收尾
./tools/remote.sh phone-stop
./tools/remote.sh emu-release

# 5.55 构建
./tools/remote.sh build status
./tools/remote.sh build start

# 任意远程命令
./tools/remote.sh exec emu 'free -h; df -h /'
./tools/remote.sh exec nas 'hostname; systemctl --failed --no-pager'

# 清理本地 SSH multiplexing socket
./tools/remote.sh ssh-close
```

`emu` 和 `build` 已配置公钥免密，且同一轮操作会复用 SSH ControlMaster。`nas` 仍通过外部 `~/.ssh/askpass.sh` 认证，因为该账号的 home 目录当前不存在，暂时不能由账号自身落地 `authorized_keys`；后续由 NAS 管理员创建 home 并备份原有 `authorized_keys` 后，再切换为公钥认证。

## 构建与测试职责

- `build` 只负责 Unity 构建，不运行 5.21 模拟器。
- `emu` 负责安装、启动、截图、logcat 和设备收尾。
- `nas` 只用于服务检查和发布流程。
- 手机测试结束执行 `phone-stop`，释放模拟器执行 `emu-release`；不要用 `close` 代替单独的应用收尾，除非确认本轮测试全部结束。
