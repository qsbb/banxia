# banxia 安卓手机端移植方案（第一性原理设计）

> 目的：**把手机变成随身测试设备**——随时验证物理抽搐修复、音频链路、对话功能，
> 摆脱「构建机+头显」重型链路；同时为未来手机端产品形态打底。
> 约束不变：只改 banxia 仓库（含 vendored UnityMMDTools），不动 AstrBot 远端/容器。

---

## 0. 第一性原理拆解：这个 App 的本质是什么

把所有 Quest/VR 名词剥掉，banxia 的最小必要回路只有四件事：

```
① 渲染一个 3D 角色        —— MMD 模型加载 + URP 前向渲染（与 VR 无关）
② 物理驱动头发/披风/饰品   —— UnityMMDTools + Bullet 原生库（与 VR 无关）
③ 语音/文字对话回路       —— 麦克风采集 → ASR/LLM → TTS 播放（Android 通用 API）
④ 与角色的交互           —— 「注视/触摸/呼唤」等抽象事件（VR 里=头+手；手机=触屏）
```

VR/MR 只是 **呈现层**（双眼渲染、透视背景）与 **输入层**（头部姿态、手柄/手部追踪）
的一种实现。手机端把这两层换成普通实现即可，**四层本质全部原样保留**。

由此得出移植的判定原则：

> **凡是「四层本质」之一的代码 → 原样保留；凡是「VR 呈现/输入实现」→ 替换或关闭；凡是 VR 专属容错缺失 → 补空实现而不是删功能。**

## 1. 现状侦察结论（代码证据）

### 1.1 平台无关核心（无需改动，约 60% 代码）
- `MMD/`：RuntimeMmdModelLoader 运行时 PMX 加载、模型扫描、哈希缓存 —— 纯文件 IO
- `Conversation/`（除麦克风输入封装）：对话状态机、Pcm16StreamAudioPlayer（本次刚修过）、AstrBot 桥 —— 平台无关
- `Backend/`：配对、HTTP/WS 桥 —— 平台无关
- `Core/` 逻辑层：AvatarPresence/NaturalIdlePose/TouchInteraction/HumanInteraction/
  QualitySettings/PerformanceMonitor/DiagnosticReporter —— 不含 XR API 调用（个别仅引用头显节点读取输入，见 1.3）
- 物理：UnityMMDTools 全托管 Burst + Bullet native（已有 ARM64 .so）—— 手机 CPU 同样 ARM64

### 1.2 VR 专属实现（手机端关闭/替换，已隔离良好）
- `MR/` 全目录（6 脚本）：PassthroughFacade / AvatarPlacementService / RoomUnderstandingService / SpatialCapabilityAdapter 等 → 手机端整体禁用
- `Core/QuestVrLocomotion`（摇杆位移）、`QuestTrackedHandVisualizer`、`AvatarMmdPhysicsAdapter`（手部物理接触球）、`QuestXrInputUtility`、`XRInteractionCompatibility`
- `UI/CompanionWorldMenu`（XRNode 左右手指针射线菜单）

### 1.3 需要适配的「边界层」（少量、可枚举）
| 组件 | 现状 | 手机端处理 |
|---|---|---|
| XR loader 启动 | 构建脚本 `ConfigureOpenXr()` 强制挂 OpenXR loader；运行时被接管 | **手机构建变体不挂 loader**（XRGeneralSettings 里 Android 不 assign loader）|
| 相机 | `EnsureCamera()` 已会造普通相机；Quest 上被 XR 子系统接管渲染 | 不接管即自然生效；加**轨道相机手势**（新脚本）|
| 输入 | `QuestXrInputUtility` 读 XRNode（头/双手） | 触屏射线 → 映射到现有触摸事件抽象（AvatarTouchInteraction 已按「点+射线」工作）|
| UI 菜单 | 世界空间射线菜单 | 新增屏幕空间简易诊断面板（复用 DiagnosticReporter 数据）|
| 麦克风权限 | `Permission.RequestUserPermission(RECORD_AUDIO)` Android 通用 | 原样可用 |
| 音频输出 | Quest 走系统音频；PLAY_AUDIO AppOps 是 HorizonOS 特异行为 | 手机无此问题 |
| 刷新率/画质 | 72/90Hz 档位、Quest3 专属 | 手机 60Hz；`QuestQualitySettings` 用其 Inspector 配置即可（已参数化）|
| 后台行为 | 摘头显 = 会话挂起（proximity/presence） | 手机用屏幕常亮 + `Screen.sleepTimeout = Never` |

### 1.4 关键利好
- **Bootstrap 的 `EnsureCamera()` 已内置普通相机器物**（y=1.6 固定机位）——说明架构本来就允许「无 XR 也能渲染」
- 所有 VR 专属组件挂在 Bootstrap 同一个 GameObject 上，**禁用一个字段就能整层关掉**，且有 `if (xxx != null)` 判空调用习惯（已核实 L63-119 的挂装与绑定模式）
- 模型在设备端 `persistentData/MmdModels/` 动态安装——手机端同样 adb push 模型即可，**测试链路（含 M2 修复模型）完全复用**

## 2. 移植目标形态：三种可选深度

| 形态 | 内容 | 工作量 | 何时用 |
|---|---|---|---|
| **A. 最小测试构建**（推荐先做） | 同一 Unity 工程 + 新构建变体：不挂 XR loader、普通相机+轨道手势、关闭 MR/手部/菜单组件、触屏射线接入触摸事件、屏幕常亮。对话/物理/诊断全部原样 | ~1 天 | **立即可测试**：物理抽搐修复、音频、对话在手机上随手验证 |
| B. 可视化诊断面板 | 在 A 之上加屏幕空间 Overlay：实时 fps/pose_src_flip/物理档/心跳字段（把 logcat 变成屏内可见） | +0.5 天 | 调试迭代期 |
| C. 手机端产品形态 | 完整触屏 UI、模型导入 UI、竖屏适配、后台保活策略 | 另立计划 | 手机端成为产品时再定 |
| D. 现实感知通道（可选） | 接入 reality_companion 移动网关：位置/设备状态/遥测上报（3.5 节） | +1 天 | AstrBot 侧装有其插件族时 |

本方案按 **A 为必须、B 为推荐** 设计；C 仅列方向，不展开。

## 3. 实现方案（A+B）

### 3.1 平台开关机制：编译符号而不是运行时 if

新增构建变体符号 `BANXIA_PHONE`（PlayerSettings scripting define）：

- **构建脚本分叉**：`QuestMmdPlayerBuild.BuildAndroidApk` 拆出 `BuildAndroidPhoneApk`：
  - 复用 90% 设置（Linear/Vulkan/IL2CPP/ARM64），**跳过 `ConfigureOpenXr()`**
  - 不同 applicationId 后缀（`.phone`）与 productName（`半夏-Phone`），可与 Quest 版共存于同一手机
  - `ScriptingDefineSymbols` 加入 `BANXIA_PHONE`
- **运行时分叉**：Bootstrap 顶部：
  ```csharp
  #if BANXIA_PHONE
      // 手机：不挂 VR 组件层
      // Passthrough/Placement/RoomUnderstanding/Locomotion/TrackedHands/HandPhysics/Menu 全部跳过
  #endif
  ```
  现有 `if (X != null)` 判空调用习惯使跳过挂装天然安全（已核实调用点模式）

> 备选：纯运行时用 `Application.isMobilePlatform && !XRGeneralSettings...` 判断。
> **否决**：编译符号让 VR 代码整块不参与手机构建（含 using UnityEngine.XR 的文件仍能编译——包仍在 manifest，只是 loader 不启用），且行为明确、不会在手机误启 XR。

### 3.2 各边界层的具体改造

#### (1) 相机与视角 —— 新脚本 `PhoneOrbitCamera.cs`（Assets/Scripts/Core/）
- 挂在 Bootstrap 相机对象上（`#if BANXIA_PHONE` 才挂）
- 单指拖动 = 围绕角色轨道旋转；双指捏合 = 推拉距离（1.2m~4m）；双击 = 复位到正面 1.6m
- 角色位置从 `RuntimeMmdModelLoader.spawnPosition` 读（已存在）
- 注视目标 = 角色胸口（y≈1.2）

#### (2) 触摸交互 —— 接入现有抽象而非新写（已核查 API 形状）

`AvatarTouchInteraction` 结构已核实：`Update()` → `ReadHand(leftHand/rightHand)`
（XRNode 读取）→ `UpdateTouchState(bounds)`（手部位置 vs 角色包围盒/碰撞代理判定）。
另有现成的 `SimulateContactForQa(string source)` —— 但它是**单帧脉冲**
（`UpdateTouchState` 每帧开头清 `IsQaContact`），只够「轻拍反馈」。

手机端方案（`#if BANXIA_PHONE` 分支，改动集中在 Update 头部 ~25 行）：
- 触屏按下/移动 → `Camera.ScreenPointToRay` → 与角色碰撞代理（`EnsureCollisionProxies`
  已建好的 broadphase bounds + 刚体代理）求交 → 命中点写入
  `leftHand.available=true / leftHand.position=命中点`，并**跳过 ReadHand 覆盖**
- 这样**持续按住摸头能真实驱动头发物理接触**（与头显手部同一判定路径），
  而不是只有单帧脉冲——物理手感测试与 VR 完全等价
- 手部物理接触球（AvatarMmdPhysicsAdapter）在手机上**关闭**（Quest 手部追踪专属）

#### (3) 对话 —— 零改动
- `QuestMicrophoneInput` 使用 Android 通用 `Permission`+`Microphone` API，手机上原样工作（仅类名带 Quest，改名留到以后）
- AstrBot 桥/配对：手机与 Quest 同 Wi-Fi 即可连容器/后端

#### (4) UI —— 屏幕空间诊断面板 `PhoneDiagnosticsHud.cs`（B 项）
- IMGUI（`OnGUI`）左上角叠加：fps、physics 档、`pose_src_flip`、心跳关键字段（复用 DiagnosticReporter 已收集的数据，加一个简单的公开快照属性）
- IMGUI 零 UI 资产、零布局工作，专为调试；正式 UI 属于 C 形态

#### (5) 后台与常亮
- `Screen.sleepTimeout = SleepTimeout.NeverSleep`（Bootstrap 手机分支）
- 音频后台：`Application.runInBackground` 保持默认 false（手机测试前台即可）

#### (6) 质量档
- `QuestQualitySettings` 的物理档参数已是 Inspector 可调；手机版预设「balanced 60/2/1」即可（Quest3 能跑的密度，骁龙 8 系手机同级）
- 若实测手机发热/掉帧：下一步把 72Hz 重测（4b）等 Quest 优化结论同步过来，手机与 Quest 共享同一套物理优化代码——这正是先修物理再移植的意义

### 3.3 测试链路（与现有流程对齐）

```
代码/模型修改（本机）→ scp 到构建机 → BuildAndroidPhoneApk →
adb install 到手机（USB 或 5555 无线）→ 手机 adb push 模型到
/sdcard/Android/data/<id>.phone/files/MmdModels/ → 打开即测
```

- 构建机仍是 192.168.5.55（复用现有 Unity 2022.3 环境，零安装成本）
- logcat 标签沿用 `QuestMmdPlayer`（脚本内 Log 前缀不变）→ 现有全部日志采集脚本/命令直接复用
- **M2 修复模型的验证可以首先在手机上完成**（模型文件同一份，sha256 一致）——等头显时手机先行

### 3.4 手机 → AstrBot 的网络路线（参考 reality_companion 配对手册整理）

手机不在家里 Wi-Fi 时（通勤/外出）也要能连 AstrBot，按场景选路线：

| 路线 | 拓扑 | 适用 | 备注 |
|---|---|---|---|
| A 同局域网 | `http://电脑IP:端口` | 家里现测 | 现状即此；防火墙放行端口 |
| B Tailscale/ZeroTier 组网 | `http://100.x.x.x:端口` | **长期最推荐** | 组网链路自带加密，可用 HTTP；零公网暴露 |
| C frp/ngrok 穿透 | `https://隧道域名` → `127.0.0.1` | 无组网工具时 | 需支持 WebSocket（SSE 也别被缓存）|
| D 公网域名+反代 | Caddy/Nginx → `127.0.0.1` | 正式长期 | 只开 HTTPS 入口，转发 WS 头 |
| E Cloudflare Tunnel | 域名 → 本机网关 | 无公网 IP | Quick Tunnel 地址会变，不适合长期 |

要点（沿其手册的安全结论）：公网只走 HTTPS；`/pair` 类接口不加额外登录页；
敏感响应 `no-store`；管理端口（Dashboard 等）绝不同时暴露；令牌不发群聊。
banxia 已有 `allowPrivateHttp` 私网 HTTP 白名单机制，与该手册
`lanRelease`（可信私网允许 HTTP）/`release`（仅 HTTPS）的分包策略同构。

## 3.5 参考 `astrbot_plugin_reality_companion`（menglimi/现实触及插件）

> 该项目是 AstrBot 生态的"现实设备联动"插件（第三方，**只参考、不改其源码**），
> 内含一个**移动端网关**（aiohttp，默认 6322 端口），且明确欢迎自建客户端接入
> （"供你自行维护的客户端接入，不随插件分发 App"）——banxia 手机端正是
> 它欢迎的"自建客户端"形态。

### 它的网关协议（已读源码核实，mobile_gateway.py 2146 行）

```
POST /pair            配对令牌(≥24字符,header) + {user_id, device_name}
                      → session_token(urlsafe 32B) + 过期时间
                      内存态会话(服务端重启即失效) + 限速 + 绑定唯一 allowed_user_id
GET  /status          会话令牌 → 网关与能力就绪状态
POST /location        {latitude,longitude,accuracy_m,altitude_m,speed_mps,
                      bearing,label,place{matched,name,kind,distance_m,...},
                      captured_at}——校验经纬度范围与 10 分钟新鲜度
POST /device/status   {device_name,platform,app_state:foreground|background,
                      battery_percent,charging,captured_at}
POST /telemetry       {source,captured_at,measurements[{type,value,unit}×≤32],
                      activity{...},events[...]}——结构化身体/活动/生活事件
POST /screen/heartbeat  屏幕共享状态
POST /session/close   主动撤销会话
```

### 对本方案的三个层面的价值

**(1) 直接采纳：网络路线手册与安全卫生**（已吸收进 3.4 节）
——五条连接路线、no-store、配对限速、公网 HTTPS-only 等，均为通用工程结论。

**(2) 架构参照：配对/会话生命周期**
它的模型是「长期配对令牌 → 短时会话令牌（内存态、自动重配对）」；
banxia 现状是「6 位码/二维码一次性交换 → 持久双 API 钥（落盘配置）」。
两者是不同权衡：我们的钥**跨服务端重启存活**（服务端无状态，重启免配对），
它的会话**短生命周期**（服务端可控失效）。**保持 banxia 现状不改**——改配对
模型要动临插件，收益不成比例；手机端只是多一个消费同一配置的客户端。

**(3) 可选互操作：手机成为"随身现实感知器"（Phase D，装了该插件才有意义）**

Quest 在家，手机随行——手机是陪伴角色唯一能带出门的"感官"。若 AstrBot
侧安装了 reality_companion，banxia 手机端可加一个小模块作为其客户端：

- 新脚本 `PhoneRealityContextReporter`（`#if BANXIA_PHONE`，~150 行）：
  - `/pair` 一次（存 session_token，失效自动重配）
  - 前台时周期 `POST /device/status`（电量/充电/前后台——健康关怀素材）
  - 位置共享开关打开时 `POST /location`（Android 前台服务 + FINE_LOCATION）
  - 可选 `POST /telemetry`（体重/心率/步数等，接入 Health Connect 时）
- **对话仍走临桥**（不动）：reality_companion 只收"现实上下文"，
  其 Private Companion 侧把位置/设备态注入人格上下文——即两条通道
  各司其职：banxia 手机端 = 表情/动作/语音的具身终端 + 现实感知上报器
- 它网关绑定单一 `allowed_user_id`，与单用户陪伴定位天然一致

**边界**：不修改该插件任何代码；Phase D 独立于 A/B/C，不装插件时
手机端零感知。Phase D 的前置是用户确认其 AstrBot 跑着这套插件族
（我会永远陪着你/现实触及），否则不做。

## 4. 风险与对策

| 风险 | 评估 | 对策 |
|---|---|---|
| XR 包引用编译错误 | 低：manifest 保留包，仅不启用 loader；`using UnityEngine.XR` 编译无碍 | 构建变体跑一次验证 |
| VR 组件跳过挂装后空引用 | 低：调用点均为 `if (X != null)` 模式（已核实） | 构建后冒烟测试一轮 |
| 触屏射线接 TouchInteraction 需要看其 API 形状 | 中 | 第一步就核查；最坏情况加薄封装（<50 行）|
| 手机性能（URP+Bullet 12-15ms 基线在 Quest3 上） | 中：旗舰手机 GPU 弱于 Quest3 但分辨率可调；CPU 同级 | 画质档先降到 balanced；必要时降分辨率缩放 |
| 一部手机同时调试 Wi-Fi adb 与容器网络 | 低 | 已验证过 192.168.5.94 adb 无线链路 |

## 5. 落地顺序（每步可独立验收）

1. **核查 `AvatarTouchInteraction` 公共入口** + 确认 `ConfigureOpenXr` 跳过后 OpenXR 不启动（读构建脚本）——纯读代码，现在就能做
2. `PhoneOrbitCamera` + Bootstrap 手机分支（不挂 VR 层）+ `BuildAndroidPhoneApk` 变体 → 构建机恢复后构建
3. 手机 adb 安装 + push 模型 → 冒烟（角色渲染/物理在跑/心跳日志）
4. 触屏触摸事件接入 + 对话冒烟
5. PhoneDiagnosticsHud（pose_src_flip 屏内实时看）→ **在手机上验证 M1/M2/M3 修复效果**
6. 顺手在手机上验证音频修复（build 17 的静音修复尚未实测——手机端如果同样复现路径则验证更快）

## 6. 不做什么（明确排除）

- 不做 ARCore 平面检测（MR 替代品）——测试不需要；C 形态再议
- 不改任何 AstrBot 远端/容器/**第三方插件（含 reality_companion）**
- 不重构 Quest 版现有代码（除 TouchInteraction 可能需要加一个公共入口方法）
- 不做竖屏 UI/正式产品设计
- 不改 banxia 现有配对模型（6 位码/二维码 → 持久双钥保持不变，理由见 3.5(2)）
