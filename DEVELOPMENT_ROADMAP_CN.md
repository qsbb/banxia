# Quest MMD 真人式互动播放器开发路线

更新日期：2026-08-04

## 1. 产品目标

目标不是做一个只会播放动作的 MMD 查看器，而是在 Quest 3 中让用户感觉角色“在场、能感知、会回应、可持续相处”：

- 用户可以在 VR 或 MR 中靠近、观察、触碰角色。
- 角色会注视用户，具有呼吸、眨眼、待机和情绪变化。
- 握手、摸头、捏脸等接触不是一次性特效，而是连续、可中断、能自然复位的动作。
- 用户可以直接说话，角色通过 AstrBot 理解上下文并用语音、嘴型、表情和动作共同回应。
- 后续可以导入不同 PMX；再增加 VRM 作为第二种标准化角色格式。

这里的“全景透视”按 Quest MR 实现：使用 Passthrough 显示真实环境，并让虚拟角色正确放置在房间坐标中。它不是把普通 360 度视频当背景。

## 2. 当前基线

已经完成：

- Unity 2022.3 LTS、Android ARM64、OpenXR、Meta OpenXR 和 URP 工程。
- 使用 UnityMMDTools 在运行时直接读取 PMX、贴图、骨骼、Morph、IK 和 MMD 物理。
- 统一入口 `RuntimeMmdModelLoader.LoadFromFileAsync`，可供后续文件选择器和下载模块调用。
- 基础移动、旋转、缩放、动作、表情和暂停控制。
- XR Hands 手掌、指尖和捏合数据读取；同时保留控制器输入。
- 握手、摸头、捏脸三种原型交互，以及无头显 HUD 模拟和多角度截图工具。
- APK 自动构建、PMX 冒烟测试和桌面预览。

当前仍在开发或待真机验收：

- AstrBot HTTP/SSE、Quest 麦克风 PCM16、流式 TTS、打断和 `avatar.intent` 已接入；真实文本闭环仍需管理员先选择 Chat Completion Provider。
- 语义接触、自然待机、面对面放置、身高定位和本地 VMD 动作导入已实现；连续 Two Bone IK、动作混合和更细的注视仍在后续阶段。
- Meta Passthrough provider 已配置；房间遮挡、空间锚点、彩透画面和性能仍需 Quest 真机验收。
- Quest 文件选择器、模型兼容性扫描和 VRM provider 仍是后续工作。
## 3. 总体技术架构

```text
Quest 头部 / 双手 / 麦克风
             |
             v
感知层：XR Hands、控制器、接触区、VAD、房间信息
             |
             v
交互协调器：状态、优先级、冷却、打断、接触所有权
       |                         |
       |                         v
       |                 AstrBot HTTP/SSE Bridge
       |                 对话、关系、记忆、语音、工具调用
       v                         |
角色表现意图 <-------------------+
       |
       v
AvatarAdapter
  - PMX：骨骼/Morph/MMD 物理
  - VRM：Humanoid/Expression（后续）
       |
       v
注视 + 动画层 + IK + 表情 + 嘴型 + 音频 + MR 放置
```

职责必须分开：

| 模块 | 负责 | 不负责 |
|---|---|---|
| Quest Unity 前端 | 采集头手和麦克风、上报交互、渲染、音频播放、嘴型、受约束地执行 IK/表情/动作意图 | 决定角色如何回应、长期记忆、人格推理 |
| AstrBot | 对话编排、角色设定、记忆、模型调用、STT/TTS 服务协调、动作意图 | 直接操作 Unity 骨骼或依赖具体 PMX 名称 |
| `AvatarAdapter` | 把“看向用户、微笑、抬手”等语义意图映射到具体模型 | 决定角色说什么 |
| `InteractionCoordinator` | 处理交互冲突、优先级、打断和恢复 | 调用大模型生成内容 |

## 4. 已确认的技术选择

### 4.1 继续使用 Unity，不切换 UE4

UE4/UE5 可以做高质量 VR，但“某款游戏使用 UE4”不能证明它更适合本项目。当前工程已经打通 PMX 运行时导入、Quest APK、XR Hands 和 Unity 侧交互。切换引擎会重新解决 PMX、骨骼/Morph、Android XR、手部追踪和构建问题，对当前目标没有直接收益。

至少在完成可打断对话和连续 IK 前，保持 Unity 2022.3，不迁移 Unity 6，也不切换引擎。Meta 新样例中的架构可以移植，不能整包照搬其 Unity 6 依赖。

### 4.2 PMX 保持原生运行时导入，VRM 作为后续第二格式

- 当前主路线：`PMX -> UnityMMDTools -> AvatarAdapter`。
- 可选离线路线：`PMX/VMD -> glTF`，用于复杂模型预处理、物理烘焙或性能对照，不替代运行时 PMX。
- 后续路线：`VRM -> UniVRM -> AvatarAdapter`，复用标准 Humanoid、表情和注视接口。

不要让交互代码直接到处搜索日文骨骼名。每次模型加载后只解析一次，生成 `AvatarDefinition`：

- 身体锚点：头、眼、胸、左右上臂、肘、手腕、手掌。
- 表情通道：眨眼、微笑、害羞、惊讶、A/I/U/E/O 嘴型。
- 身高、头宽、肩宽、手臂长度和安全接触半径。
- 模型能力标记：是否支持手臂 IK、嘴型、眼睛注视和物理。

### 4.3 接触交互采用“语义接触区 + IK 目标”，不直接把骨骼挂到手上

KK/KKS VR 项目证明了接触吸附、保持偏移、拉伸自动脱离、输入锁和控制器运动平滑是有效模式；但它们依赖 PC VR、游戏注入或旧 SteamVR，不能作为 Quest 代码依赖。

本项目采用以下等价实现：

1. 给头、脸颊、手掌等位置建立语义接触区，不依赖渲染网格碰撞。
2. 识别 `Enter -> Hold -> Exit/Cancel`，而不是每帧重复触发一次动作。
3. 握手时移动手臂 IK 目标；模型根节点保持独立。
4. 超过手臂长度、安全角度或追踪丢失时自动释放。
5. 两只手竞争同一目标时由交互协调器决定所有权。
6. 控制器模式提供震动；裸手模式只能提供视觉和声音反馈。

### 4.4 动画使用分层和约束

角色的最终姿势应按层组合：

```text
基础待机/行走
  + 上半身语义动作（挥手、鞠躬、回应）
  + 手臂 Two Bone IK（握手/牵手）
  + 头眼注视
  + 表情与嘴型 Morph
  + MMD 物理
```

下一阶段引入 Unity Animation Rigging 的 Two Bone IK 和 Multi-Aim 思路。当前直接旋转 PMX 骨骼的代码只作为原型降级路径，避免与 VMD、IK 和物理持续争夺同一 Transform。

### 4.5 对话使用 HTTP/SSE Bridge 和可取消状态机

AstrBot 是服务端“大脑”，Quest 不内置大模型。前端通过 HTTP 发起会话、轮次、音频和交互事件，通过 SSE 接收流式文本、PCM16、结束、错误和 `avatar.intent`；本地 Mock 保留为无设备自动测试替身。

前端状态：

```text
Idle -> Listening -> Thinking -> Speaking -> Idle
  ^         |             |          |
  +---------+-------------+----------+
             Interrupt / Error
```

关键原则：

- 每轮对话有 `turn_id`，旧轮次返回的文字、音频和动作必须丢弃。
- 用户开始说话时立即停止当前 TTS、嘴型和说话动作，再发 `interrupt`。
- 音频、文字和角色动作分通道到达，但通过同一个 `turn_id` 对齐。
- HTTP/SSE 断线后角色回到本地待机，不冻结交互。
- 触碰事件必须上报 AstrBot；Unity 不自行决定接受、拒绝、害羞或回避，只执行后端返回的 `avatar.intent`。断网时可以显示无性格含义的连接状态，但不伪造角色反应。

建议的前端协议边界：

```json
{"type":"session.start","session_id":"..."}
{"type":"audio.chunk","turn_id":"...","format":"pcm16","sample_rate":16000,"data":"..."}
{"type":"audio.end","turn_id":"..."}
{"type":"interaction","name":"head_pat","phase":"start","strength":0.7}
{"type":"interrupt","turn_id":"..."}

{"type":"asr.partial","turn_id":"...","text":"..."}
{"type":"reply.text.delta","turn_id":"...","text":"..."}
{"type":"reply.audio.chunk","turn_id":"...","format":"pcm16","sample_rate":24000,"data":"..."}
{"type":"avatar.intent","turn_id":"...","emotion":"happy","gesture":"wave","look_at":"user"}
{"type":"reply.end","turn_id":"..."}
```

协议是前端约定草案，不代表 AstrBot 已实现这些消息。

### 4.6 流式 TTS 使用运行时嘴型

Meta NorthStar 为固定台词预烘焙嘴型，适合 Timeline；AstrBot 的回复是动态音频，所以本项目优先采用 uLipSync 一类的运行时分析方法：

- 音频块进入环形缓冲区，边播边分析，不在主线程解码完整文件。
- 输出 A/I/U/E/O/N 权重，再由 `MmdVisemeMapper` 映射到 PMX Morph 别名。
- 缺少标准嘴型的模型退化为下颌开合或音量驱动，不报错。
- 角色被打断时清空待播放音频并在短时间内平滑闭嘴。

## 5. 开发阶段

### M0：显示与原型触碰（已完成）

完成标准：PMX 能运行时加载；桌面可模拟；Quest APK 可构建；握手、摸头和捏脸有动作或表情反馈。

### M1：可打断的语音对话表现闭环（基础链路已完成，待模型与真机验收）

前端通过真实 HTTP/SSE Bridge 连接 AstrBot；本地 Mock 仍用于无设备自动测试。管理员必须在“临”配对页面选择 Chat Completion Provider，不能自动选择可能产生费用的模型。

已完成：`ConversationStateMachine`、`turn_id` 失效机制、HTTP/SSE 流式事件、Quest PCM16 麦克风、PCM16 播放队列、立即打断、音量驱动嘴型、对话注视、HUD 测试入口，以及“Unity 上报触碰，AstrBot 决定反应”的边界。真实文本闭环只剩管理员选择模型和 Quest 真机验收。

1. `ConversationController` 状态机和 `turn_id` 取消机制。
2. Android 麦克风权限、录音和音量/VAD 指示。
3. 流式音频播放队列，支持立即停止和清空。
4. 运行时嘴型映射、说话时注视、倾听和思考姿态。
5. `IConversationTransport`、本地 Mock 和真实 HTTP/SSE 实现。
6. Bridge 负责认证、配对、系列插件适配和流式交付；Unity 只消费受限协议。

验收：编辑器 Mock 已验证 Listening -> Thinking -> Speaking -> Idle 和打断；Quest 真机还需验证麦克风权限、PCM16 上传、TTS 播放、SSE 打断延迟。
### M2：连续身体互动### M2：连续身体互动

1. 建立语义身体接触区和接触生命周期。
2. 加入手臂 IK，握手从固定摆臂升级为追随用户手掌。
3. Unity 上报摸头方向、速度和持续时间；AstrBot 决定歪头、闭眼、说话、拒绝或其他反应。
4. Unity 验证捏脸的拇指/食指几何条件；AstrBot 决定反应，Unity 只负责限制头部拉伸。
5. 增加牵手、击掌和招手回应；所有动作可被追踪丢失或用户离开中断。
6. 增加站立/坐姿模式和身高校准。

验收：动作不瞬移、不持续扭曲，超出活动范围自动脱离，VMD/待机与 IK 不互相打架。

### M3：角色“活着”的基础表现

1. 头眼分层注视：用户、用户手、交互物体和说话方向之间切换。
2. 随机眨眼、微呼吸、轻微重心变化和待机微动作。
3. `BehaviorArbiter` 只仲裁 AstrBot 下发意图、正在播放的动作和安全约束，不在 Unity 内生成角色性格反应。
4. 情绪采用平滑权重和衰减，不用瞬时开关 Morph。
5. 空闲时保持合适社交距离，用户靠太近时有退让或视线反应。

验收：角色即使不说话、不被触碰，也不会像静止模型；任何高优先级交互结束后可自然回到待机。

### M4：用户模型直接导入

1. Quest 文件选择器选择 PMX 和资源目录。
2. 导入前扫描贴图、顶点、材质、骨骼、Morph、刚体和预计内存。
3. 自动生成 `AvatarDefinition`；异常命名允许用户选择骨骼和 Morph。
4. 保存每个模型的缩放、位置、别名和嘴型校准。
5. 增加 VRM provider，共用同一 `AvatarAdapter`。
6. 对过大贴图、异常物理或缺失资源提供可读提示和降级选项。

验收：不重新打 APK 就能导入新的合规模型；不兼容项会明确显示，不因单个 Morph 或刚体失败导致闪退。

### M5：Quest MR 在场感

1. 接入真实 Meta Passthrough provider。
2. 使用 MRUK/Scene API 获取地面、墙面和家具边界。
3. 角色落地、朝向用户、保持房间位置并支持重新放置。
4. 增加环境遮挡、接触阴影和空间音频。
5. 将相机原点、玩家头部追踪和角色/Timeline 镜头分开，切换场景时保留头部自由度。

验收：用户绕角色移动时比例和遮挡合理；角色不会随头部转动漂移；重新打开应用可恢复位置。

### M6：人格、记忆和主动行为

由 AstrBot 提供长期记忆、角色设定和工具调用；Unity 只接收结构化意图。增加主动招呼、对触碰的个性化反应、会话恢复和隐私控制。

## 6. 参考项目与可迁移方法

| 项目 | 可参考方法 | 本项目处理方式 |
|---|---|---|
| [Meta Unity-NorthStar](https://github.com/oculus-samples/Unity-NorthStar) | 手部优先 NPC 交互、Movement SDK 重定向、语音/嘴型、任务状态、Quest 优化 | 迁移架构和性能规则；不整体升级 Unity 6 |
| [NorthStar LipSync](https://github.com/oculus-samples/Unity-NorthStar/blob/main/Documentation/LipSync.md) | uLipSync 校准、运行时与预烘焙两种嘴型路径 | 动态 AstrBot 音频使用运行时路径 |
| [NorthStar FullBodyTracking](https://github.com/oculus-samples/Unity-NorthStar/blob/main/Documentation/FullBodyTracking.md) | 重定向、校准、IK、追踪丢失降级 | 用于 M2 的 IK 和跟踪恢复设计 |
| [Meta Unity-Discover](https://github.com/oculus-samples/Unity-Discover) | Passthrough、Scene API、空间锚和 Interaction SDK | 用于 M5 的 MR 工程结构 |
| [Meta Unity-TheWorldBeyond](https://github.com/oculus-samples/Unity-TheWorldBeyond) | MRUK、语音、空间音频、交互组织 | 用于较轻量的 MR/音频参考 |
| [Unity XRI Examples](https://github.com/Unity-Technologies/XR-Interaction-Toolkit-Examples) | 注视、抓取、物理交互和 XR Origin | 参考交互模式，不在当前版本整包迁移 |
| [UnityMMDTools](https://github.com/CandidumGames/UnityMMDTools) | PMX/VMD、骨骼、Morph、IK 和物理 | 当前 PMX 主加载器 |
| [UniVRM](https://github.com/vrm-c/UniVRM) | 运行时 VRM、Humanoid、Expression | M4 的第二模型 provider |
| [uLipSync](https://github.com/hecomi/uLipSync) | 音频实时音素分析、麦克风、预烘焙、VRM | M1 的嘴型分析参考 |
| [mmd2gltf-gui](https://github.com/masaka1024/mmd2gltf-gui) | PMX/VMD 转 glTF、动作和物理烘焙 | 可选离线优化和故障排查工具 |
| [Open-LLM-VTuber](https://github.com/Open-LLM-VTuber/Open-LLM-VTuber) | VAD、流式 ASR/TTS、免耳机打断、触碰和表情意图 | 参考会话任务取消与消息分流；后端仍用 AstrBot |
| [LLMUnity](https://github.com/undreamai/LLMUnity) | Unity 角色 API、流式回调、取消请求、远端服务边界 | 参考 Unity 客户端接口，不在 Quest 内运行 LLM |
| [VirtualMotionCapture](https://github.com/sh-akira/VirtualMotionCapture) | VRM、眼神、校准、动作记录和 VMC/OSC 解耦 | 后续外部追踪/动作输入扩展，不是首版依赖 |
| [VRGIN](https://github.com/Eusth/VRGIN) | `IActor`/解释器抽象、注视目标、站立/坐姿模式 | 采用适配层思想，放弃旧 SteamVR/Leap 实现 |
| [KK_VR](https://github.com/Ermin610/KK_VR) | 角色包装、语义碰撞、冷却震动、IK 标记和双手缩放 | 转化为 Quest 原生 InteractionCoordinator |
| [KK_SetParentVR](https://github.com/MayouKurayami/KK_SetParentVR) | 手脚吸附、保持偏移、输入锁、运动平滑、拉伸脱离 | 只迁移约束思想，不直接父子绑定角色骨骼 |
| [KKS VR Timeline Camera Sync](https://github.com/YukyoMoe/KKS_VR_TimelineCameraSync) | 相机驱动仲裁、保留头部跟踪、切镜阈值、人工移动不被覆盖 | 用于 M5 的 XR Origin/剧情镜头协调 |

结论：没有一个现成项目同时解决“Quest 3 MR + 任意 PMX + 手部身体互动 + AstrBot 对话”。最省事的路线是组合成熟模块，并用本项目的 `AvatarAdapter` 和 `InteractionCoordinator` 把它们隔离起来。

## 7. Quest 性能红线

目标先按 72 Hz 设计，每帧总预算约 13.9 ms：

- 不在 `Update` 中反复 `FindObjectsOfType` 或 `GetComponentsInChildren`；模型加载时缓存骨骼和 Morph。
- 接触检测使用少量语义球体/胶囊和 NonAlloc 查询，不给整张 SkinnedMesh 建复杂碰撞。
- 音频解码、网络和模型文件读取不得阻塞主线程。
- 贴图、材质和动作按模型释放；切换模型后检查原资源是否仍被引用。
- MMD 物理、IK 和 Morph 分级开关，低性能模型可以自动降级。
- 真机记录 CPU/GPU 帧时、内存、温度和追踪丢失，不只看编辑器帧率。

## 8. 测试责任边界

由电脑自动完成：源码编译、PMX 解析、贴图解码、骨骼/Morph 别名、对话状态机、协议序列、音频队列、打断、截图和 APK 构建。

只有以下项目需要用户戴 Quest 3 测试：

- 真手在真实空间中的接触距离、手势误判和追踪丢失。
- 麦克风权限、扬声器回声、免耳机打断和实际延迟。
- 真 Passthrough、房间识别、空间锚、遮挡和光照观感。
- 72 Hz 稳定性、发热、眩晕和长时间内存表现。

设备测试前，电脑端检查和可模拟行为应由开发侧先全部跑完。

## 9. 2026-08-05 实现状态与下一轮待办

本轮已完成：

- AvatarPresence 已加入持续注视、头部平滑回正、自然眨眼/呼吸，以及用户偏航超过阈值后的平滑转身；挥手、鞠躬、点头、轻摆、VMD 和触碰期间不会抢占身体控制权。
- AvatarOutlineController 已加入运行时描边壳层、开关、粗细限制和本地保存；Quest URP 描边 Shader 已随 APK 编译。
- AvatarPlacementService 已加入地面优先、高度校准、tracking-floor fallback 和面对面重放置。
- 动作菜单已加入自然待机、挥手、鞠躬、点头、轻摆、停止、刷新、选择和播放。
- VMD 动作库现在支持两种安全格式：
  - Application.persistentDataPath/Motions/<动作名>.vmd
  - Application.persistentDataPath/Motions/<动作名>/motion.vmd，可选同目录 facial.vmd。
- 动作包会在刷新时分别检查文件签名、大小、关键帧数和时长；身体轨道来自 motion.vmd，表情轨道来自 facial.vmd，重复表情轨道以表情文件为准，且总关键帧数仍受上限保护。
- 用户指定的“クリームソーダとシャンデリア”原始文件仍未复制进仓库或 APK。获得明确授权且设备具备文件导入能力后，可将其整理为动作包目录并放入上述 Quest 持久化目录；当前不读取其附带视频或说明文件。
- VMD 播放已改为 11000 执行序的 LateUpdate 最终写入；播放期间暂停 UMT 的实时 IK/物理求解，动作结束时恢复原设置并重新初始化物理，避免骨骼双重写入造成抽动。
- VMD 转换已启用 IK/物理烘焙和 0.4 秒预热，动态头发、裙摆等轨道不再与实时物理同时抢占。
- 中文菜单新增“画质”页面：性能、平衡、清晰、恢复默认；使用 XR 眼纹理比例、视口比例和 URP MSAA，设置即时生效并保存到 Quest 本机。
- 本轮 EditMode 回归测试 74/74 通过；Android APK 已从 ASCII 映射路径构建并通过 APK v2 签名校验。

当前待完成：

1. Quest 重新在线且电量足够后，进行一次受控实机验收：手追菜单、控制器菜单键、描边、彩色 Passthrough、高度重置、面对面放置、持续注视/转身、后端配对、麦克风 PCM16、流式 TTS、打断、动作抽动/穿模和三档画质。结束时只 force-stop 应用，不关闭或重启头显。
2. 增加 Quest 文件选择器/导入器，让用户可以把本地 PMX、VMD 和动作包复制到持久化目录，而不是依赖 ADB 手工放置。
3. 在真机记录 72 Hz 下的 CPU/GPU 帧时、内存和追踪丢失情况，再调整描边壳层和动作采样预算。
4. 继续完善自然待机和动作混合：将头部注视、呼吸、手臂 IK 与 VMD 局部轨道按优先级混合，避免整身动作期间出现僵硬。
5. 完成真实后端模型已选择、关系候选已绑定时的 Quest 对话回归；未绑定时继续保持本地物理交互降级。
6. 后续再评估 VRM provider、MMD 动作兼容性扫描、长期记忆和主动行为；不在本轮扩大 AstrBot 插件协议。

真机前置：

- 设备离线或充电不足时不安装、不启动、不截图、不重启；只在 adb devices -l 确认在线后测试。
- 测试完成执行 adb shell am force-stop com.QuestMMDPlayer.QuestMMDPlayerPrototype，不执行关机、重启或 USB 断开命令。
## 10. 2026-08-05 Quest 3 实机首轮记录

- 最新 APK 已安装成功，应用冷启动成功，前台 Unity Activity 正常运行。
- 用户动作包中的 motion.vmd 与 facial.vmd 已私有复制到 Quest 的 Motions/<动作名>/目录；camera.vmd、视频和说明文件未复制。
- Passthrough compositor 状态为 ON；Unity 日志持续报告 subsystem=running、cameraManager=True、alpha=0.00。
- 角色放置走 tracking-floor fallback，未发生崩溃或黑屏退出。
- Quest Passthrough 30 秒遥测：渲染 72/72 Hz，彩色图像 72/72 Hz，dropped_draw_rate=0.0%，连续丢帧 0/0/0。
- 本轮未通过 ADB 模拟控制器/手追，因此菜单键、手追射线、触碰、动作菜单播放、描边粗细和后端连接仍需在头显内实际操作确认。
- 测试结束只执行 force-stop，头显保持开机和在线。

## 11. 2026-08-06 收口状态与下一阶段

本轮已完成：

- 语音采集初始等待改为 4 秒；只有检测到语音后才使用 1.15 秒尾静音结束，避免用户还没开口就自动结束。
- 80 ms PCM 采集块在 HTTP 上传层合并为最多 16000 字节批次，降低请求数量；上传队列扩大到约 30 秒 PCM16。
- 触碰默认只做本地即时反馈，不再让 start/update/end 自动创建 LLM 回合；这切断了触碰导致“境”反复进入回复链路的前端来源。
- VMD 播放期间暂停待机、呼吸、注视和触碰骨骼写入；物理预热提高到 1 秒。
- 呼吸不再平移胸骨或上下移动整个模型，改为 0.28 度胸部旋转，脚保持落地。
- 新增 Quest Space Setup 房间语义读取和“扫描房间”，统计地面、座位、桌子、墙、门、窗；地面放置排除 Table 与 Seat。
- “高度定位”改名为“站立校准”，明确其测量前提；高度仍由头显眼高、真实 Floor 和 0.11 m 眼顶估算得到。
- APK 输出名改为 Builds/Banxia.apk，应用标签为“伴夏”。Android ID 是否从旧原型 ID 切换为 com.qsbb.banxia，等待用户明确接受新应用身份与重新绑定。
- 严格静态门禁通过，Unity EditMode 83/83 通过，Android/IL2CPP 构建成功并通过 APK v2 签名校验。

下一阶段按以下顺序进行：

1. 真机只做一次受控验收并记录四段时间：停止录音到 asr.final、asr.final 到首个文字、首个文字到首段音频、reply.end 到实际播完。测试后只 force-stop 应用，不关闭头显。
2. 为“临”设计 Protocol 2 草案，不直接修改 AstrBot：持久 WebSocket、100-160 ms PCM、server VAD、partial/final ASR、utterance_id、生成取消、流式文本、分句 TTS、barge-in、断线恢复。
3. 对现有 Protocol 1.0 保持兼容；若后端没有 Protocol 2，只回退到当前整句 STT，不伪装实时。
4. 增加 room.context@1.0 隐私安全契约草案。前端只发送语义面，不发送相机帧；后端不得把房间事实作为身份或权限依据。
5. 完成 Floor 站立位置与空间锚持久化，再做 Seat 选择。坐姿必须经过模型骨骼/腿长/脚底偏移校准和 IK，不能只把站立角色移到椅面。
6. 建立每模型 MotionCompatibilityProfile，记录动作根位移、脚底偏移、IK、物理预热、碰撞组和禁用动作；优先解决指定舞蹈穿模，再推广到其他 VMD。
7. 如果 ARPlane 无法满足座位边界、遮挡和可行走区域，再引入 MRUK 的 Floor Zone、Scene Query、Environment Raycast 与 NavMesh 结构，不为单一功能升级整套 Unity。
8. 在后端身份授权问题解决前，前端不自报自然人或人格；当前 protected_context_authorized=false 的根因是 trusted_platform_id_missing，与 STT 或 Unity 渲染无关。