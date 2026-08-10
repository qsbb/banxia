# 伴夏 (Banxia)

“伴夏”是一个面向 Meta Quest 3 的 Unity 混合现实虚拟陪伴项目，让虚拟角色自然地存在于现实房间中。当前前端负责模型显示、基础交互、彩色 Passthrough、Quest 手追/手柄输入、VMD 动作和 AstrBot HTTP/SSE 对话桥接。

仓库名使用 `banxia`。它不绑定 Quest、MMD 或 AstrBot 品牌，便于未来扩展到其他 XR 设备、模型格式、长期记忆和关系系统。

本仓库不会提交用户提供的 PMX/GLB、贴图或第三方 VMD 动作。它们继续保留在开发机本地；新克隆在没有模型素材时使用内置回退角色完成编译和功能测试。

产品目标、下一阶段和技术选择见 [DEVELOPMENT_ROADMAP_CN.md](DEVELOPMENT_ROADMAP_CN.md)。自然待机、挥手和日常动作的资源筛选见 [NATURAL_MOTION_SOURCES_CN.md](NATURAL_MOTION_SOURCES_CN.md)。真人式触碰见 [HUMAN_INTERACTION_TESTING_CN.md](HUMAN_INTERACTION_TESTING_CN.md)，对话闭环见 [CONVERSATION_TESTING_CN.md](CONVERSATION_TESTING_CN.md)，AstrBot 后端开发任务见 [ASTRBOT_PLUGIN_DEVELOPMENT_PROMPT_CN.md](ASTRBOT_PLUGIN_DEVELOPMENT_PROMPT_CN.md)。

## 当前实现

- 运行时直接读取 PMX，不把模型预转换成 GLB。
- 使用嵌入式 com.candidumgames.unitymmdtools 0.5.0（含伴夏手部外部刚体适配补丁）构建材质、贴图、骨骼、Morph、IK、刚体和关节。
- 启动时把 Assets/StreamingAssets/MmdSamples/ForestBerry 解包到持久化目录，再从磁盘读取 PMX 和同目录贴图。
- RuntimeMmdModelLoader.LoadFromFileAsync(pmxPath, textureBaseDirectory) 已作为后续文件选择器、网络下载或 AstrBot 桥接的统一入口。
- 模型加载失败时显示本地回退人偶，便于继续测试 HUD 和命令流。
- AvatarController 提供移动、旋转、缩放、动作、情绪和播放暂停接口。
- XR Hands 已接入手掌、指尖和捏合数据；握手、摸头、捏脸支持真手、控制器和 HUD 模拟。
- 已实现可取消的对话状态机、Mock 流式事件、PCM16 播放队列、音量驱动 PMX 嘴型、由后端意图驱动的注视和 HUD 开始/打断。
- 触碰只由 Unity 上报；动作、表情和交互后的反应由 AstrBot 返回 `avatar.intent` 决定。当前 Mock 只作为后端测试替身。
- PassthroughFacade 已接入 Meta OpenXR provider；AstrBotBridge 通过 `http://<后端>:8520` 的配对结果建立 HTTP/SSE 会话。桌面端仍使用 Mock provider 做自动测试替身。

## 开发环境

- Unity 2022.3.62f3c1
- Android Build Support、Android SDK/NDK、OpenJDK
- OpenXR、Meta OpenXR、URP
- Unity 内置 Animation 和 ImageConversion 模块
- UMT 包许可证：MIT，来源为 CandidumGames/UnityMMDTools（https://github.com/CandidumGames/UnityMMDTools）

## 编辑器运行

1. 用 Unity Hub 打开 quest_mmd_player。
2. 等待 Package Manager 完成解析。
3. 执行菜单 Quest MMD Player > Create Prototype Scene。
4. 打开 Assets/Scenes/Prototype.unity，点击 Play。
5. 首次运行会复制示例 PMX 和四张贴图，然后在 Console 中看到 PMX avatar ready。

编辑器里可用键盘测试 AvatarController：W/A/S/D 移动，Q/E 旋转，R/F 缩放，1/2/3 切换动作，Space 暂停或继续。

## 电脑端自动检查

项目根目录的 test_frontend.ps1 检查文件、包和源码契约。更完整的 PMX 导入检查可执行菜单 Quest MMD Player > Run Runtime PMX Smoke Test；它会真实解析 PMX、解码贴图并构建一次运行时对象，然后自动清理。

菜单 Quest MMD Player > Render Model Preview 会用同一套 PMX 运行时导入流程生成 Builds/ForestBerryPreview.png。

菜单 Quest MMD Player > Render Human Interaction Previews 会生成握手、摸头和捏脸的多角度预览图。

## 构建 Quest APK

执行菜单 Quest MMD Player > Build Android APK，产物为 Builds/Banxia.apk，Android 包名为 com.lingxi.banxia。当前构建使用 ARM64、IL2CPP、Vulkan，最低 Android API 29。

设备上仍需人工确认：APK 安装、Quest 视野中的模型显示、真实房间 Passthrough、手势/手柄输入、帧率和发热。设备离线或低电量时只运行编辑器和构建检查。

## 后端绑定

在头显菜单“绑定后端”中只输入域名或 IP、端口和 6 位绑定码；应用会自动补全 `/api/v1/plugins/extensions/astrbot_plugin_quest_avatar_bridge`，无需输入或粘贴长路径。

当前项目仍使用 Unity 2022 与 Meta OpenXR 1.x，没有可用的头显相机帧 API，因此不显示不可工作的扫码按钮。二维码相机绑定需要后续整体迁移到 Unity 6、MRUK 81+ 和 Meta Passthrough Camera API；在迁移完成前以手动短码绑定为正式流程。

## 导入任意用户 PMX

调用：

~~~csharp
await loader.LoadFromFileAsync(pmxPath, textureDirectory);
~~~

pmxPath 必须是本地可读的 .pmx 文件，textureDirectory 通常就是 PMX 所在目录。PMX 引用的 PNG、JPG、TGA、BMP 等贴图应保留在该目录或其子目录。头显内可从中文菜单“动作 -> 导入文件”打开 Android 文件选择器，支持一次选择 PMX 与贴图；应用会把选中文件复制到自身持久化目录后再导入。

## 导入本地 VMD 动作

把许可明确的 `.vmd` 放入 `Application.persistentDataPath/Motions` 顶层目录，也可以在头显中文菜单的“动作”页点击“导入文件”，选择单个 VMD 或包含 PMX/VMD 的 ZIP；导入完成后点击“刷新外部动作”。动作 ID 来自文件名；应用不接受 AstrBot 传入任意路径，也不递归扫描子目录。

单文件上限 16 MiB、100000 个关键帧、120 秒。文件会在 UMT 解析分配前校验完整段结构；不合规文件被忽略，内置待机、挥手和鞠躬继续可用。

公开动作常见“允许作品使用但禁止二次分发”的条款，因此项目默认不把第三方 VMD 打包进 APK。下载后应遵守作者发布页和压缩包内 README。

## 资源说明

Assets/StreamingAssets/MmdSamples/ForestBerry 只用于本地冒烟测试，模型和贴图的再分发必须遵守原作者授权。旧的 Assets/Models/Imported/ForestBerry.glb 仅作为历史对照文件，原型场景和 APK 不依赖它。
## Quest 交互

- 右手 A：挥手；右手 B：播放/暂停。
- 左手 A：鞠躬；左手 B：重置位置、旋转和缩放。
- “语音 -> 文字对话”可打开系统键盘，或直接选择中文测试短句；发送始终使用当前真实 AstrBot transport，不会自动切换到 Mock。
- 诊断日志按短 trace 标签关联单轮：会记录 PCM 编码、上传 HTTP 分段、SSE 网络线程到 Unity 主线程排队、首段音频缓冲、实际音频回调开播和播放耗尽；若后端提供可选 `server_timing.schema_version=1` 或 `server_timing.contract=server_timing@1.0`，还会显示后端 STT、AstrBot 决策链、TTS 和整轮耗时。
- 手掌靠近角色手部：握手；张开手掌靠近头顶：摸头；在脸旁捏合：捏脸。
- 手掌和五个指尖会作为运动学球体加入角色现有的 UMT/Bullet 世界，能够推动模型自带的动态头发和衣物刚体；语义接触仍由独立代理判定。
- 控制器模式下使用 Grip/Trigger 触发接触和拖动。
- 单手拖动角色；双手移动、水平旋转并按双手距离缩放。
- 编辑器左上角 HUD 可直接模拟三种真人式交互，不戴头显也能验证。

当前接触传感会在本地立即给出物理和表情反馈，同时把 `handshake/head_pat/cheek_pinch` 的开始和结束上报给后端；AstrBot 返回的结构化意图可以继续补充动作。PMX 动态头发和衣物由同一个 Bullet 求解器响应，手臂仍是原型骨骼反馈，不是连续 Two Bone IK。

开发机可在冷启动时通过 Android Intent 模拟一条客户端文字输入：`quest_debug_command=send_text` 与 `quest_debug_text=<测试文本>`。该入口等待真实 SSE 会话就绪后调用 `ConversationController.StartConversation`，不读取或打印正文，也不会绕过身份授权。
