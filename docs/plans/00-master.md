# Banxia 手机端修复总纲（Master Plan）

> 版本：v2（第一性原理版）· 2026-09-01
> 证据基线：用户三张真机截图（20:12–20:13，1440×3200）+ 本会话像素级取证
> 前置状态：commit `80862b2` 已 push（椭圆批次修复/二级导航/bounds 构图/yaw 归零/idle 停写）

---

## 1. 七条不变量（本计划的宪法）

| INV | 陈述 | 当前状态 | 负责里程碑 |
|-----|------|---------|-----------|
| INV-1 脸部可见 | 眼线投影 ∈ 可视区 28%–38%，头顶不被任何 chrome 遮挡 | 🔴 红 | M1 |
| INV-2 画面填充 | 通话可视区人物覆盖 >30%；全身像头/脚 ∈ [8%, 92%] | 🔴 红 | M1 |
| INV-3 形状派生 | 胶囊半径 = 精确 高/2；全仓禁止绝对大半径（999px） | 🟡 半绿（键帽 75px 已是 h/2，但布局塌成 2 列） | M3 |
| INV-4 内容对齐 | 字形中心 = 容器几何中心，偏差 <8px | 🔴 红 | M3 |
| INV-5 层级所有权 | 模态 sheet 打开时底层通话控件不可见不可点；关闭即归还 | 🔴 红 | M2 |
| INV-6 骨骼单写者 | 每帧每骨骼唯一写入者 | 🟢 绿（待真机复验） | —（仅守规则） |
| INV-7 验证闭环 | 每条不变量有可执行断言；构图必须可观测 | 🔴 红 | M1（叠加层）+ 全程 |

**判例**：症状会换马甲，不变量不会。所有修复与验收只对不变量负责。

---

## 2. 文档索引（本目录）

| 文件 | 内容 | 对应 |
|------|------|------|
| `M1-call-framing.md` | 通话构图闭式解 + QA 叠加层 + 安全区接线 | INV-1/2/7 |
| `M2-sheet-layer.md` | 弹层状态机 + 遮罩 + 控件让位 + 抓手下拉 | INV-5 |
| `M3-keypad-and-radius.md` | 配对键盘派生重排 + 全仓圆角令牌体检 | INV-3/4 |
| `M4-delivery.md` | 交付链（commit→push→release→NAS→收尾） | — |
| `QA-assertions.md` | 断言脚本集规格与实现（assert-*.py / grep-radius） | INV-7 |

---

## 3. 里程碑与产出

| 阶段 | 内容 | 变绿不变量 | 构建 | 验证环境 |
|------|------|-----------|------|---------|
| M1 | 闭式构图 + 叠加层 + 断言 | INV-1/2/7 | 1 次 | 模拟器（数学+叠加层）→ 用户真机（像素） |
| M2 | sheet 状态机 | INV-5 | 1 次 | 模拟器（tap 链路） |
| M3 | 键盘重排 + 令牌体检 | INV-3/4 | 1 次 | 模拟器（描边/对齐断言） |
| M4 | 交付链 | — | — | release + NAS + 用户复拍 |

每阶段独立 commit，可单独回滚。M1 为用户最痛点，最先执行。

---

## 4. 关键代码坐标（已核实，步骤引用用）

| 坐标 | 位置 | 现状 |
|------|------|------|
| 通话构图入口 | `PhoneCoPresenceDirector.EnterVideoCall()` | bounds 72% + 距离 0.85h + yaw 归零（80862b2 版）|
| 头骨骼 | `AvatarController.head`（private, L89） | 需新增公开访问器 |
| 取景器 | `PhoneOrbitCamera.FrameModel / ComputeRenderBounds / SetOrbitTarget / OrbitDistance` | ComputeRenderBounds 已 public static |
| 通话顶栏 | `BanxiaUiShell` → `videoCallChrome` 内 `.call-top`（伴夏 + 计时 + 字幕） | 即截图中挡脸的白色毛玻璃条 |
| 通话底控件 | `.call-controls`（挂断红丸 + 模式 + 环境） | sheet 打开时仍叠压弹层 |
| 弹层 | `coPresenceSheet`（`RebuildModeCards` / `RebuildEnvironmentChips`）、`ToggleCoPresenceSheet` / `HideCoPresenceSheets` | 无遮罩、无控件让位 |
| 配对键盘 | `BuildPairingNumpad` + `MakeNumpadKey`（对话页 `chatPairingCard`，未连接时显示） | 12 键 = 1–9 + 清/0/退，flex-wrap 布局 |
| 键帽 USS | `BanxiaTheme.uss` `.numpad-key`（190×150, radius 75）| 2 列塌陷 + 数字偏移 |
| 配对码点 | `.code-dot`（38px, 6 个实心） | 正常 |

---

## 5. 验证环境矩阵与操作协议

### 5.1 环境事实

| 资源 | 值 |
|------|-----|
| 构建机 | `ssh -i /data/dsh/home/.ssh/id_ed25519 -o UserKnownHostsFile=/data/dsh/home/.ssh/known_hosts -o BatchMode=yes lx@192.168.5.55` |
| 构建流 | tar 传输 → PowerShell touch 源文件 → `cmd /c del D:\banxia_build\Builds\Banxia-Phone.apk` → `powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/lx/banxia_build_phone_wait.ps1` → dir 验证新时间戳（约 10 分钟，sleep 570 轮询） |
| 模拟器 | `lingxi@192.168.5.21`（本机 ED25519 公钥免密，`~/banxia-tools/platform-tools/adb -s emulator-5554`），包名 `com.lingxi.banxia.phone`，base64 -w0 传截图 |
| 截图分析 | `/data/dsh/home/dsh/bridge-venv/bin/python` + PIL（本机无图输入，全像素断言） |
| NAS | `\\192.168.5.88\download-WD40EZRZ\文件传输`（smbclient；SSH 使用外部 `~/.ssh/askpass.sh`） |

### 5.2 模拟器点击协议（防第 3 击失效）

1. 任何验证会话前：`am force-stop` 冷启动，等 26 秒
2. 点击间隔 ≥4 秒；连点超过 3 次失效时：HOME → 重新 `am start`（注意会丢 call chrome，只有 force-stop 能完全恢复）
3. 失效仍发生：force-stop 重来整个链路

### 5.3 模拟器构图验证的诚实边界

- 模拟器无 PMX 模型且 fallback 角色当前不生成（`FallbackAvatarFactory` 仅在 loader==null 时创建）→ 像素级「脸部可见」断言必须在**用户真机**跑
- 模拟器承担：**求解器数学断言 + QA 叠加层渲染断言**（叠加层把 d/相机高/眼线%直接画在屏幕上，无需看见角色）
- 用户真机承担：**像素断言**（assert-framing.py 消费用户复拍截图）

### 5.4 交付纪律

- 每里程碑完成 = 对应不变量在对应环境**观测到达标**，不是「构建成功」
- 模拟器纪律（CLAUDE.md 钦定）：应用退出 `am force-stop com.lingxi.banxia.phone`；**整机用完 `pkill -f qemu-system`**（该机常驻他人服务，内存紧张）；需要时 `~/banxia-emu/start-emu.sh` 重启（20–35 秒）
- 构建机（5.55）：每轮构建完即告知用户可关机
- `Assets/Scripts/**/*.cs` 一律 UTF-8 BOM；python replace 后立即验证替换计数；版本号 0.3.2 不变
- vendored `Packages/com.candidumgames.unitymmdtools/` 零改动（本计划不涉）

## 5.5 双端同步义务（CLAUDE.md 钦定原则）

| 里程碑 | 性质判定 | 同步义务 |
|--------|---------|---------|
| M1 构图闭式解 | **平台无关业务逻辑**（纯投影几何） | 求解器写入平台无关层（`Assets/Scripts/Core/CallFramingSolver.cs`，无 `#if`）；Quest 端三模式 UI 同步为既有登记项，本计划完成后在 `PHONE_PORT_PLAN_CN.md` 更新「待同步」条目（含求解器复用说明） |
| M2 弹层层级 | 手机壳层（Quest 无触屏 sheet） | 不需同步；在 `PHONE_PORT_PLAN_CN.md` 备注「Quest 等价物 = 菜单面板层级规则，待统一」 |
| M3 配对键盘 | 双端共享 Flutter 壳层（`flutter_ui/lib/scene/pairing_numpad.dart`，`1..9/⌫0✓`，保留 6 位清/0/退语义 + 显式 ✓ 提交校验） | 随 Flutter 统一壳层共享；原「手机壳层不同步」理由失效（见 PHONE_PORT_PLAN_CN.md §3.6） |



---

## 5.6 Flutter 共享壳层迁移（2026-09-02）

手机端修复（M1–M3）的 UI 壳层已由 UI Toolkit 重构为**双端共享 Flutter 壳层**
（`flutter_ui/`，设计 `flutter-ui-module-design.md`）：手机与 Quest 跑**同一套 Flutter UI**；
Quest 经**纹理/世界面板**呈现（离屏 Quest→Flutter 纹理渲染尚未实现，仅编译安全接缝
`Assets/Scripts/Flutter/QuestFlutterTextureHost.cs`，`IsSupported=false`）。完整迁移门与
诚实状态见 `PHONE_PORT_PLAN_CN.md` §3.6「Flutter 共享壳层」。

## 6. 总验收（用户视角终局画面）

通话页：毛玻璃顶栏只含「伴夏 · 计时 · 字幕」，脸完整露出在顶栏下方 1/3 线处，胸口切底，无空下半屏。
弹层：打开时挂断键消失、遮罩压暗通话画面，三条路径可关。
对话页配对卡：3×4 键盘、胶囊键帽数字居中、卡片不再空旷。
