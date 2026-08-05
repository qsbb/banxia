# Quest 3 / MMD 技术路线参考审计

更新日期：2026-08-05

本轮检索关键词包括 Quest、VR、MR、AR、MMD、PMX、VRM、KKS、KK、VR 游戏、手部追踪、IK、虚拟角色、语音对话和嘴型。这里只分析技术结构和可迁移方法，不用许可证作为技术路线筛选条件。

## 结论先行

没有发现能够直接替代本项目的“Quest 3 MR + 任意 PMX 运行时导入 + 真人式手部互动 + AstrBot 对话”完整项目。可行路线是保留现有 Unity/PMX 前端，组合成熟项目中的局部方法：

```text
UnityMMDTools 运行时 PMX
        +
Meta 官方 MR/手部/NPC 样例的组织方式
        +
Animation Rigging 风格的 IK 与注视
        +
uLipSync 风格的动态嘴型
        +
Open-LLM-VTuber 风格的流式会话和打断
        +
KK/KKS/VRGIN 的角色适配、接触约束和相机仲裁思想
        +
AstrBot 对话、人格和记忆
```

现在不应切换 UE4/UE5，也不应立刻把 Unity 2022.3 升到 Unity 6。前者会丢失已经完成的 PMX/XR Hands 链路，后者会把“实现功能”变成“迁移依赖”。Meta Unity 6 样例只用于读架构。

## 1. Quest / MR / NPC

### Meta Unity-NorthStar

- 地址：[oculus-samples/Unity-NorthStar](https://github.com/oculus-samples/Unity-NorthStar)
- 价值：当前最接近“Quest 中与有声音、有全身动画的 NPC 互动”的官方样例；以手部追踪为主，也支持控制器。
- 可迁移：Interaction SDK 的交互分层、Movement SDK 的重定向和校准、Audio SDK、NPC 任务状态、追踪失败降级。
- 嘴型：[LipSync 文档](https://github.com/oculus-samples/Unity-NorthStar/blob/main/Documentation/LipSync.md) 使用 uLipSync，并区分实时、预分析数据和动画片段三条路径。固定剧情适合预烘焙；AstrBot 动态回复适合实时分析。
- 全身：[FullBodyTracking 文档](https://github.com/oculus-samples/Unity-NorthStar/blob/main/Documentation/FullBodyTracking.md) 展示重定向、角色缩放、坐姿/站姿、IK 修正和追踪丢失后的替代手。
- 性能：[OptimisingFramerate 文档](https://github.com/oculus-samples/Unity-NorthStar/blob/main/Documentation/OptimisingFramerate.md) 给出 Quest 原生 72 Hz 的约 13.88 ms 帧预算，强调缓存、NonAlloc 查询、池化和预热。
- 采用方式：把 NPC 表现拆层的方法写入本项目，不直接导入 Unity 6 工程。

### Meta Unity-Discover

- 地址：[oculus-samples/Unity-Discover](https://github.com/oculus-samples/Unity-Discover)
- 价值：Passthrough、Scene API、Interaction SDK、Spatial Anchors 和 Shared Anchors 的完整 MR 工程结构。
- 采用方式：未来接真实透视、房间地面/墙体、角色落地和空间锚；忽略当前单角色应用不需要的多人系统。

### Meta Unity-TheWorldBeyond

- 地址：[oculus-samples/Unity-TheWorldBeyond](https://github.com/oculus-samples/Unity-TheWorldBeyond)
- 价值：Passthrough、MRUK、语音、空间音频和交互系统放在同一体验中的组织方式。
- 采用方式：作为比 Discover 更聚焦的 MR/语音参考。

### Unity XR Interaction Toolkit Examples

- 地址：[Unity-Technologies/XR-Interaction-Toolkit-Examples](https://github.com/Unity-Technologies/XR-Interaction-Toolkit-Examples)
- 价值：注视、抓取、Focus、物理交互、XR Origin 和世界空间 UI。
- 限制：当前样例面向较新的 Unity/XRI；本项目已有 Unity 2022.3 + XR Hands 输入，不为这些样例立即整包升级。
- 采用方式：复用交互概念和预制体边界，按需实现，不替换已经工作的直接手关节读取。

## 2. MMD / VRM / 动画

### UnityMMDTools

- 地址：[CandidumGames/UnityMMDTools](https://github.com/CandidumGames/UnityMMDTools)
- 价值：当前工程已实际使用，能在运行时建立 PMX 网格、贴图、骨骼、Morph、IK、刚体和关节。
- 采用方式：继续作为 PMX 主路径，在外层新增 `AvatarAdapter`，避免业务代码绑定工具内部类型。

### UniVRM

- 地址：[vrm-c/UniVRM](https://github.com/vrm-c/UniVRM)
- 价值：VRM 0.x/1.0 和 VRM Animation 的运行时导入，提供更统一的 Humanoid、表情和注视概念。
- 采用方式：后续作为第二种模型 provider；PMX 和 VRM 都映射为相同的 `AvatarDefinition`。

### uLipSync

- 地址：[hecomi/uLipSync](https://github.com/hecomi/uLipSync)
- 价值：基于音频的实时音素分析，支持麦克风、运行时播放、预烘焙、Timeline、AnimationClip 和 VRM。
- 采用方式：M1 使用实时分析，将 A/I/U/E/O/N 权重映射到 PMX Morph；没有对应 Morph 时自动降级。

### mmd2gltf-gui

- 地址：[masaka1024/mmd2gltf-gui](https://github.com/masaka1024/mmd2gltf-gui)
- 价值：PMX/VMD 转 glTF 2.0，并可烘焙动作和物理。
- 采用方式：可选的 PC 离线优化、模型诊断或预制内容管线；不再作为首版唯一主路线，因为当前 PMX 运行时导入已经打通。

### VirtualMotionCapture / VMCProtocol

- 地址：[sh-akira/VirtualMotionCapture](https://github.com/sh-akira/VirtualMotionCapture)、[VMCProtocol](https://protocol.vmc.info/Reference)
- 价值：VRM、3 到 11 点校准、眼神、动作录制/回放，以及用 OSC 把追踪来源和角色渲染解耦。
- 采用方式：未来若接全身追踪、外部面捕或动作源，可增加独立输入 provider；首版 Quest 本地互动不需要 VMC 网络层。

## 3. 对话型虚拟角色

### Open-LLM-VTuber

- 地址：[Open-LLM-VTuber/Open-LLM-VTuber](https://github.com/Open-LLM-VTuber/Open-LLM-VTuber)
- 价值：实时语音、VAD、ASR/TTS 模块化、免耳机语音打断、触碰反馈、后端表情映射和会话持久化。
- 源码模式：WebSocket 消息按类型路由；每个客户端保存当前会话任务；收到 `interrupt-signal` 时取消任务；麦克风原始音频由 VAD 产生暂停/结束控制消息。
- 采用方式：本项目复用“流式事件 + 可取消 turn + 控制消息”的架构，后端仍然使用 AstrBot，前端角色仍然是 3D PMX/VRM。

### Project N.E.K.O

- 地址：[Project-N-E-K-O/N.E.K.O](https://github.com/Project-N-E-K-O/N.E.K.O)
- 价值：公开架构将 Realtime API 对话、ChatCompletion 辅助能力、活动状态、主动话题、五维记忆和 Live2D/VRM/MMD Avatar 分层；强调跨端共享同一人格与记忆，而不是把 Avatar 当作聊天皮肤。
- 可迁移：保留“临”作为 Quest 传输边界，由“知/言/序/情/境/声/核”继续承担现有能力；Unity 只维护可取消的本地 turn、Avatar 表现和传感器事件。主动搭话应由服务端策略产生，不由 Quest 客户端定时硬编码。
- 不迁移：不引入其 Python/Electron 服务栈，也不替换 AstrBot；只借鉴能力分层和主动陪伴状态机。

### Gemini Live 类实时语音示例

- 地址：[AmSh4/gemini-live-app](https://github.com/AmSh4/gemini-live-app)、[Gemini Live API 文档](https://ai.google.dev/gemini-api/docs/live)
- 价值：公开示例采用 WebSocket、AudioWorklet、客户端 VAD、16 kHz PCM 输入、24 kHz PCM 输出，并允许用户在 AI 播放中讲话以立即打断。
- 已采用：Quest 麦克风继续输出 PCM16 mono 16 kHz；TTS 继续接受 24 kHz；开始新语音回合会取消旧 turn 和清空播放；手追长按启动，松开或静音窗到期结束；自动结束后需松手才能再次创建回合。
- 保持差异：现有 Protocol 1.0 使用 HTTP/SSE，已经完成文本、音频和 interrupt 的真实闭环，不为了模仿 Gemini 强制改成 WebSocket。

### LLMUnity

- 地址：[undreamai/LLMUnity](https://github.com/undreamai/LLMUnity)
- 价值：Unity 角色层的异步回复、流式回调、`CancelRequests`、聊天历史和远端服务模式。
- 采用方式：参考 Unity 客户端 API 的易用性和取消语义；不把本地 LLM 模型塞进 Quest APK，避免内存、功耗和重复后端。

## 4. KK / KKS / 通用 PC VR 项目

这些项目的直接依赖通常是 BepInEx、游戏内部对象、SteamVR 或旧输入栈，不能复制到 Quest Android；但其长期使用中沉淀的交互规则很有参考价值。

### KK_VR

- 地址：[Ermin610/KK_VR](https://github.com/Ermin610/KK_VR)
- 角色包装：`KKCharaStudioActor` 把具体游戏角色包装为统一 Actor，并把眼睛/头部注视从主相机重定向到可控目标。
- IK 工具：用可见/不可见球形触发器表示 IK 目标，控制器操作的是目标而不是直接改网格。
- 接触：`OnTriggerStay` 过滤自身碰撞，识别角色渲染器/动态骨骼，设置冷却并触发震动。
- 双手操作：记录初始双手距离和中点，再计算缩放、旋转和位置，避免单帧跳变。
- 采用方式：建立 `AvatarAdapter`、语义接触区、接触冷却和 IK target；控制器可震动，裸手只做视觉/声音反馈。

### KK_SetParentVR

- 地址：[MayouKurayami/KK_SetParentVR](https://github.com/MayouKurayami/KK_SetParentVR)
- 有效方法：触碰手脚后保持位置；双击释放/复位；超出拉伸距离自动脱离；当前控制手输入锁；使用多帧位置池平滑运动。
- 采用方式：握手/牵手采用约束和 IK 目标，加入最大臂长、释放阈值、追踪丢失恢复和显式输入所有权。
- 不采用：把角色根或骨骼直接设为控制器子节点。这会和 MMD 动画、物理、缩放及网络动作竞争。

### KKS VR Timeline Camera Sync

- 地址：[YukyoMoe/KKS_VR_TimelineCameraSync](https://github.com/YukyoMoe/KKS_VR_TimelineCameraSync)
- 有效方法：区分普通镜头、Timeline 镜头、玩家手动移动和外部镜头驱动；在剧情镜头中保留头显相对转动；用位移阈值识别切镜；避免两个系统同时写 XR Origin。
- 采用方式：未来若播放 VMD 镜头或剧情动画，增加 `CameraMotionCoordinator`。MR 默认只让剧情控制世界锚点或水平朝向，绝不逐帧锁死头部。

### VRGIN

- 地址：[Eusth/VRGIN](https://github.com/Eusth/VRGIN)
- 有效方法：`IActor` 和 `GameInterpreter` 将角色/相机识别与通用 VR 管理分离；`LookTargetController` 为每个角色维护独立注视目标；站立和坐姿是显式模式。
- 采用方式：本项目使用同类适配层和模式状态；不使用其旧 SteamVR、Leap Motion 或游戏注入代码。

## 5. 公开免费待机姿势与动作参考

### Quaternius Universal Animation Library

- 地址：[Universal Animation Library](https://quaternius.com/packs/universalanimationlibrary.html)、[Universal Animation Library 2](https://quaternius.com/packs/universalanimationlibrary2.html)
- 许可：作者官方页明确标注 CC0，可用于个人、教育和商业项目。
- 内容：两套通用 Humanoid 动画库分别包含 120+ 和 130+ 动作，提供 FBX、GLB、Blend，并明确支持 Unity 重定向。
- 采用方式：优先作为自然 Idle、呼吸、重心切换和动作过渡的姿态参考；若后续下载动画，只导入所需片段并记录原始包版本，不把整包无差别塞进 Quest APK。

### Carnegie Mellon University Motion Capture Database

- 地址：[CMU Motion Capture Database](https://mocap.cs.cmu.edu/)
- 许可：CMU 官方页声明动作数据可免费用于所有用途，也可包含在商业产品中；不能直接转售原始数据或转换后的数据。
- 内容：可按 motion category 搜索站立、手势和日常动作，原始格式以 ASF/AMC 为主。
- 采用方式：用于分析自然站姿的肩臂角度、呼吸节奏和重心变化；进入 Unity 前需离线转换和 Humanoid 重定向，不直接作为 MMD VMD 分发。

### 当前选择

- 当前 APK 继续使用项目自有的程序化自然站姿，不依赖第三方文件。
- 暂不采用许可不清楚、只有转载页或禁止再分发的 VMD 姿势包。
- 下一步若引入动画片段，首选 Quaternius CC0，并为每个片段保留来源、许可和转换记录。

## 6. 最终路线

近期顺序：

1. 保持现有 PMX/XR Hands，不做引擎和大版本迁移。
2. 已完成前端可打断对话状态机、PCM 流式音频、降级嘴型和注视，并用 Mock 验证。
3. 已完成 AstrBot HTTP/SSE Protocol 1.0 的文本、PCM 音频、打断和 `avatar.intent` 闭环；继续保持 Unity 只上报感知事件并执行受限意图。
4. 已实现握手/摸头/捏脸的语义接触区、连续 IK 和离线物理回退；下一步完善手掌/指尖接触可视化与追踪丢失恢复。
5. 增加 `AvatarDefinition`、模型扫描和 Quest 文件选择器。
6. 最后接真 Passthrough、MRUK、空间锚、遮挡和房间级体验。

详细里程碑和验收标准见 [DEVELOPMENT_ROADMAP_CN.md](DEVELOPMENT_ROADMAP_CN.md)。
