# 机器连接运维手册（症状→根因→解法）

> 新会话先跑 `tools/remote.sh preflight`——本表所有坑都已被它前置检查。
> 本表只记已实踩的事故，按症状检索。敏感信息（域名/凭据）不在此记录。

## SSH / 文件传输

| 症状 | 根因 | 解法 |
|---|---|---|
| Windows 构建机 scp 拉取的文件**在 204800 字节处整齐截断** | Windows OpenSSH 服务端 scp 缺陷（与网络无关，重试无效） | `tools/remote.sh pull-bin build <远端> <本地>`（ssh stdout 流）；推送用 `push-bin` |
| 本地 scp/cat 报 "No space left on device"，文件看似被截断 | 容器 /tmp 是 **256MB tmpfs**，满了 | 大文件放 `$HOME` 下；`df -m /tmp` 查余量（preflight 已查） |
| `Could not chdir to home directory`（nas） | nas 账号无 home 目录 | 无害警告，忽略；命令用绝对路径即可 |
| GitHub push 挂起 / GnuTLS recv error | 间歇性网络故障 | `timeout 90 git push ...` + 后台重试循环；成功以 `git ls-remote origin main` 为准 |

## 构建机（192.168.5.55，Windows）

| 症状 | 根因 | 解法 |
|---|---|---|
| gradle 报 "Unsupported class file major version 65" | 系统默认 JDK 21 跑了 gradle 7.5 | 用 Unity 自带 JDK 11：`JAVA_HOME=D:\Tools\2022.3.62f3c1\Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK`（`tools/build-host/build_full.ps1` 已钉死） |
| gradle wrapper 下载 TLS 失败 / dist 目录只有 .lck 和 0 字节 .part | wrapper dist 缓存损坏 | 手动下载 gradle-7.5-all.zip 放到 `C:\Users\lx\.gradle\wrapper\dists\gradle-7.5-all\6qsw290k5lz422uaf8jf6m7co\`，解压出 `gradle-7.5\` 并建 `gradle-7.5-all.zip.ok` 空标记 |
| `flutter build aar --offline` 直接 exit 64 | 没有 --offline 这个参数 | 别加；模块 AAR 产物在 `build\host\outputs\repo\com\lingxi\banxia\...\flutter_release-1.0.aar` |
| Unity 构建失败 CS 编译错 | 容器侧改 .cs 后 BOM 被编辑工具剥掉（`Assets/Scripts/**` 必须 BOM） | 编辑后 `head -c 3` 验 `ef bb bf`，缺了补回 |
| D:\banxia_build 与仓库不同步 | 它不是 git 仓库 | 改动文件逐个 scp 同步（或 rsync 等价物），再跑 `tools/build-host/` 里的脚本 |

## 模拟器 / adb（192.168.5.21）

| 症状 | 根因 | 解法 |
|---|---|---|
| 复杂 quoting 的 adb shell 命令行为诡异 | remote.sh→ssh→guest 多层转义 | 写 .sh 脚本 → push 到 guest `/data/local/tmp/` → `sh` 执行；或 `tools/remote.sh qa <cmd>` 直达 QA 广播 |
| adb 命令打到/可能打到 `2G0YC5ZHBF00R0` | 那是 **Quest 头显**（用户 2026-09-07 确认），与 emulator-5554 同在一个 adb server；误发 reboot/设置命令 = 违反 Quest 电源纪律 | **一切命令经 `tools/remote.sh adb`**（已锁死 `-s emulator-5554`）；手写 adb 必须带 `-s`；preflight 会警告多余设备 |
| 杀 qemu 把自己的 ssh 会话也杀了 | `pkill -f qemu` 匹配到自己命令行 | `remote.sh emu-release`（用 `[q]emu` 技巧）；或 `ps -e -o pid=,comm=` 按 PID kill |
| 模拟器不在线 | qemu 被杀（用完必须杀，内存纪律） | `remote.sh exec emu '~/banxia-emu/start-emu.sh'`（20-35s 开机，自带 ≥1800M 内存护栏） |
| screencap 的 .rgba 无法用图像工具打开 | 裸 RGBA dump：16 字节头（uint32 w,h）+ RGBA  payload | Node 读 `buf.readUInt32LE(0/4)` 取宽高；本模型无图像输入，用像素统计/ASCII 分析 |

## Unity/应用运行态

| 症状 | 根因 | 解法 |
|---|---|---|
| 场景里模型"站屏幕中间/缩一半"，骨骼/蒙皮全部正常 | 场景 XR 遗留 TrackedPoseDriver 在无 XR 设备时把相机逐帧重置（已修 f3cfd03） | 别回退 `DisableXrPoseDrivers`；验收看 logcat `[PhoneBoot] disabled XR pose driver` |
| minimize 后回前台全白、fps=0、进程内不可恢复 | 非 UI 线程调 `moveTaskToBack` 毁掉 SurfaceView 事务（已修 3f63e3b） | 活动生命周期方法一律 `runOnUiThread` |
| Flutter 面板在时 Unity 完全收不到触摸 | 面板吞掉所有触摸事件（已修：Flutter 手势层→桥命令） | 见 `_SceneGestureLayer`；视频通话/AR 放置态手势层按设计不激活 |
