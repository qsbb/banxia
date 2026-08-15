# 伴夏 (Banxia)

伴夏是一个面向 Meta Quest 3 的 Unity 混合现实具身陪伴客户端。它让 PMX 角色进入现实房间，并将手追、控制器、麦克风和空间信息连接到 AstrBot，在保留正式人格、记忆、知识、工具和后处理链路的同时，呈现语音、动作、表情、注视与物理反馈。

伴夏是 [凝心溯溪-临](https://github.com/qsbb/astrbot_plugin_embodiment_bridge) Protocol 1.0 的参考客户端。后端协议不绑定 Quest、Unity 或 MMD，也可以由其他 XR 设备、桌面角色和实体载体实现。

## 参与项目

本项目希望先提供一条可运行、可验证的具身陪伴实现路径，为更多设备、角色和交互方式抛砖引玉，而不是把当前方案视为唯一答案。欢迎通过 [Issues](https://github.com/qsbb/banxia/issues) 反馈兼容性、交互、协议和性能问题，也欢迎提交 Pull Request，共同完善客户端适配、模型支持、物理交互、测试与文档。

遇到问题建议优先提交 Issue；如需进一步沟通，也可以通过 QQ：`1483904397` 联系作者。反馈时请尽量附上版本、设备、运行环境、复现步骤和脱敏日志，请勿发送 API Key、绑定密钥或其他敏感信息。

提交内容请说明测试环境，并确认拥有所附代码和资源的必要授权。

## 核心功能

- Meta OpenXR 彩色 Passthrough、手追和 Touch 控制器输入。
- 运行时加载 PMX、贴图和 VMD，无需预先转换为 Unity Prefab。
- 待机、动作切换、自然过渡、表情、注视、音频驱动嘴型和角色位置调整。
- 手掌与指尖接入角色物理世界，支持握手、摸头、捏脸等接触事实上报。
- 常开麦克风、文字输入、PCM16 流式播放、打断和多轮对话。
- 通过 AstrBot EventBus 使用人格、记忆、知识、工具和后处理插件。
- 房间理解、地面定位、Passthrough 控制、画质和描边设置。
- 在确实观察到房间平面后，向临低频上报脱敏房间语义：仅包含地面、座位、床、桌、墙、门、窗数量及场景能力；相同内容去重，不上传图像、网格、坐标、尺寸或锚点。追踪丢失时停止续租，由后端自动遗忘旧事实，不把“尚未扫描”误报为空房间。
- 设备内中文诊断日志与端到端阶段耗时追踪。

## 运行要求

- Unity `2022.3.62f3c1`
- Android Build Support、Android SDK/NDK、OpenJDK
- Meta Quest 3
- OpenXR、Meta OpenXR、XR Hands、URP
- AstrBot 与 [astrbot_plugin_embodiment_bridge](https://github.com/qsbb/astrbot_plugin_embodiment_bridge)

项目使用内嵌的 [UnityMMDTools](https://github.com/CandidumGames/UnityMMDTools) 0.5.0 运行时加载 PMX/VMD，并包含伴夏所需的物理适配。

## 快速开始

1. 克隆本仓库，用 Unity Hub 打开 `banxia` 项目目录。
2. 等待 Package Manager 完成依赖解析。
3. 执行 `伴夏 > Create Prototype Scene`。
4. 打开 `Assets/Scenes/Prototype.unity`，可在编辑器中直接运行。
5. 执行 `伴夏 > Build Android APK` 构建 Quest 安装包。

APK 输出到 `Builds/Banxia.apk`，Android 包名为 `com.lingxi.banxia`，构建目标为 ARM64、IL2CPP、Vulkan，最低 Android API 29。

项目根目录的 `test_frontend.ps1` 提供无需头显的静态工程检查。完整测试方式见 [TESTING.md](TESTING.md)。

## 后端绑定

### AstrBot 配置

1. 安装并启用 [凝心溯溪-临](https://github.com/qsbb/astrbot_plugin_embodiment_bridge)，在 AstrBot 中准备至少一个 Chat Completion Provider。
2. 在 AstrBot 的 API Key 管理中创建具身客户端专用 Key，并授予 `plugin` scope。
3. 打开“临”的具身服务控制台，选择聊天模型和正式消息平台，填写客户端、Bot、User 和 API Key；需要身份和关系继承时再连接“序”和“情”。
4. 启用内置 listener。默认端口为 `8520`，Docker 部署还需映射同一端口。
5. 在快速绑定页生成 6 位短码。

### 伴夏配对

在应用菜单中打开“绑定后端”，输入服务器域名或 IP、端口和 6 位绑定码。伴夏会自动补全：

```text
/api/v1/plugins/extensions/astrbot_plugin_embodiment_bridge
```

配对结果保存在 `Application.persistentDataPath/embodiment_bridge.json`，其中包含长期密钥，不应出现在日志、截图或版本库中。公网连接必须使用可信 HTTPS；局域网 HTTP 仅允许用户显式启用并使用字面量私网 IP。

从旧版升级时，如果新配置不存在，伴夏会读取 `quest_avatar_bridge.json`，将精确的旧插件路径迁移到新路径，再原子写入新配置。旧文件保留用于降级，已经存在的新配置不会被覆盖。

更完整的网络、安全和手工配置说明见 [ASTRBOT_BRIDGE_SETUP_CN.md](ASTRBOT_BRIDGE_SETUP_CN.md)。

## 模型与动作

在应用的“动作”菜单中选择“导入文件”，可以导入：

- `.pmx` 以及同目录贴图
- `.vmd` 动作
- 包含 PMX、贴图或 VMD 的 ZIP

导入完成后可刷新模型或外部动作列表，并在菜单中切换、播放或删除；最后一次成功加载的模型会在下次启动时自动恢复。PMX 贴图应保持原有相对目录；VMD 动作会经过格式、大小、关键帧和时长校验。第三方模型、贴图和动作仍适用各自作者的授权条款。

## 架构与 API

```mermaid
flowchart LR
    H["手追、控制器、麦克风、房间事实"] --> U["伴夏 Unity 客户端"]
    U -->|"HTTP: 文字、PCM16、交互事实"| B["凝心溯溪-临"]
    B --> E["AstrBot EventBus"]
    E --> S["人格、记忆、知识、工具与后处理"]
    S --> E
    E --> B
    B -->|"SSE: 文字、PCM16、avatar.intent"| U
    U --> A["模型、动作、表情、注视、物理与嘴型"]
```

Unity 只上报已经观测到的事实，并执行模型无关的白名单意图；身份授权、关系、回复和行为决策由后端负责。客户端不能通过配对或 `session/start` 自报管理员身份、平台或自然人。

伴夏对正常接口同时发送：

```http
Authorization: ApiKey <plugin-scope-key>
X-Embodiment-Bridge-Key: <bridge_api_key>
```

后端在 Protocol 1.0 兼容期仍接受旧客户端的 `X-Quest-Avatar-Key`。二维码类型 `astrbot.quest.pair` 也是已经发布的 1.0 兼容字段。

| 方法 | 路径 | 用途 |
|---|---|---|
| POST | `/session/start` | 创建会话 |
| GET | `/events/<session_id>` | 建立 SSE 下行流 |
| POST | `/turn/start` | 开始文字或语音轮次 |
| POST | `/audio/chunk` | 上传 PCM16 mono 16000 Hz 音频 |
| POST | `/audio/end` | 结束录音并启动识别和决策 |
| POST | `/interaction` | 上报触碰与空间交互事实 |
| POST | `/interrupt` | 打断并清理旧轮次 |
| POST | `/session/close` | 关闭会话 |
| GET | `/health` | 读取协议和能力状态 |

SSE 事件包括 `asr.partial`、`asr.final`、`avatar.intent`、`reply.text.delta`、`reply.audio.chunk`、`reply.end` 和 `error`。输入音频为 PCM16 mono 16000 Hz，输出音频为 PCM16 mono 24000 Hz。完整 schema 和错误语义见后端 [API_CN.md](https://github.com/qsbb/astrbot_plugin_embodiment_bridge/blob/main/docs/API_CN.md)。

## 凝心溯溪系列

伴夏只需要连接“临”；“临”通过有版本的公开契约复用其他系列能力。各模块可独立安装，缺失时按边界降级。

| 模块 | 作用 | 仓库 |
|---|---|---|
| 知 | 知识学习、检索与验证 | [astrbot_plugin_active_learner](https://github.com/qsbb/astrbot_plugin_active_learner) |
| 言 | 对话节奏、消息链与表达控制 | [astrbot_plugin_conversation_flow](https://github.com/qsbb/astrbot_plugin_conversation_flow) |
| 序 | 身份、主人和精确授权 | [astrbot_plugin_identity_guardian](https://github.com/qsbb/astrbot_plugin_identity_guardian) |
| 情 | 自然人映射、关系状态与边界 | [astrbot_plugin_relationship](https://github.com/qsbb/astrbot_plugin_relationship) |
| 境 | 环境事实、机会与预警 | [astrbot_plugin_environment_awareness](https://github.com/qsbb/astrbot_plugin_environment_awareness) |
| 声 | TTS、音色与语音输出契约 | [astrbot_plugin_voice_hub](https://github.com/qsbb/astrbot_plugin_voice_hub) |
| 核 | 系列更新、诊断聚合与安全边界 | [astrbot_plugin_update_manager](https://github.com/qsbb/astrbot_plugin_update_manager) |
| 临 | 具身客户端桥接 | [astrbot_plugin_embodiment_bridge](https://github.com/qsbb/astrbot_plugin_embodiment_bridge) |

## 文档

| 文档 | 内容 |
|---|---|
| [ASTRBOT_BRIDGE_SETUP_CN.md](ASTRBOT_BRIDGE_SETUP_CN.md) | 后端接入、配置迁移和网络安全 |
| [CONVERSATION_TESTING_CN.md](CONVERSATION_TESTING_CN.md) | HTTP/SSE 对话闭环 |
| [VOICE_INPUT_TESTING_CN.md](VOICE_INPUT_TESTING_CN.md) | 麦克风、STT/TTS 和常开语音 |
| [HUMAN_INTERACTION_TESTING_CN.md](HUMAN_INTERACTION_TESTING_CN.md) | 握手、摸头、捏脸和物理接触 |
| [NATURAL_MOTION_SOURCES_CN.md](NATURAL_MOTION_SOURCES_CN.md) | 自然动作资源与筛选标准 |
| [TESTING.md](TESTING.md) / [QUICK_TEST.md](QUICK_TEST.md) | 自动化和快速验证 |
| [DEVELOPMENT_ROADMAP_CN.md](DEVELOPMENT_ROADMAP_CN.md) | 后续开发方向 |
| [REFERENCE_AUDIT.md](REFERENCE_AUDIT.md) | 参考项目和许可证审计 |
| [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) | 已引入第三方组件声明 |

## 参考与许可证

本项目实际嵌入并修改 [UnityMMDTools](https://github.com/CandidumGames/UnityMMDTools) 0.5.0（MIT），上游许可证和修改声明已保留。

以下项目用于研究架构和交互方法：

| 项目 | 参考内容 | 许可证或采用边界 |
|---|---|---|
| [Meta Unity-NorthStar](https://github.com/oculus-samples/Unity-NorthStar) | NPC、手追、全身重定向、嘴型和性能分层 | MIT |
| [Meta Unity-Discover](https://github.com/oculus-samples/Unity-Discover) | Passthrough、Scene API、空间锚和 MR 工程组织 | MIT |
| [OpenAI Realtime Console](https://github.com/openai/openai-realtime-console) | 实时音频事件、打断和调试 UI | MIT |
| [Gemini Live API Web Console](https://github.com/google-gemini/live-api-web-console) | PCM 队列、全双工和低频视觉通道 | Apache-2.0 |
| [Pipecat](https://github.com/pipecat-ai/pipecat) | 异步管线、轮次与 barge-in | BSD-2-Clause |
| [Open-LLM-VTuber](https://github.com/Open-LLM-VTuber/Open-LLM-VTuber) | VAD、可取消任务和表情映射 | MIT；样例模型另有条款 |
| [Together Companion](https://github.com/menglimi/astrbot_plugin_together_companion) | AstrBot 消息链、连续识别和房间连接思路 | 仓库未声明许可证，仅作行为参考 |
| [KK_VR](https://github.com/Ermin610/KK_VR) / [KK_SetParentVR](https://github.com/MayouKurayami/KK_SetParentVR) | IK、接触冷却、约束释放和双手操作 | 仓库未声明许可证，仅作概念参考 |

没有明确许可证的参考项目未复制源码或资源。详细审计见 [REFERENCE_AUDIT.md](REFERENCE_AUDIT.md)。

本仓库原创代码采用 [Mozilla Public License 2.0](LICENSE)。第三方组件和用户导入素材继续适用其原有许可证或授权条款，MPL-2.0 不会替它们授予额外权利。
