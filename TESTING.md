# 伴夏测试说明

当前阶段已接入 PMX/动作包格式：Motions/<动作名>/motion.vmd，可选 facial.vmd。
VMD 运行时导入、XR Hands、三种触碰传感、Quest 麦克风、Meta Passthrough 和 AstrBot HTTP/SSE；仍需真机确认房间画面、追踪精度、配对和语音延迟。

## 1. 静态检查

在 `banxia` 项目根目录执行：

~~~powershell
powershell -ExecutionPolicy Bypass -File .\test_frontend.ps1 -Strict
~~~

看到 Automated checks passed. 即表示文件、包声明和关键源码契约通过。

## 2. Unity 编辑器检查

1. 用 Unity 2022.3.62f3c1 打开 banxia。
2. 执行 伴夏 > Create Prototype Scene。
3. 打开 Assets/Scenes/Prototype.unity 并点击 Play。
4. 首次运行会将 PMX 和贴图复制到 Unity 持久化目录，再显示模型和 HUD。
5. Console 应看到 PMX avatar ready；如果导入失败，会显示回退人偶和明确错误。
6. 用 W/A/S/D、Q/E、R/F、1/2/3、Space 验证基础交互。
7. 点击 HUD 的 Handshake、Head pat、Cheek pinch，确认 Unity 先上报传感事件，Mock 返回 `avatar.intent` 后角色才反应。
8. 输入文字并点击 Start mock conversation，确认 Listening -> Thinking -> Speaking -> Idle；播放中点击 Interrupt 应立即停声。
9. 完整步骤见 CONVERSATION_TESTING_CN.md。

在不进入 Play 的情况下，可执行 伴夏 > Run Runtime PMX Smoke Test。通过文件选择器选择本地 PMX 后，该测试会验证网格、材质、纹理、骨骼和刚体数量；文件只用于本机编辑器测试，不进入生产 APK。

伴夏 > Render Model Preview 会用同一条 PMX 运行时导入链生成本地预览图，用于桌面端查看画面。

伴夏 > Render Human Interaction Previews 会生成三种交互的多角度 PNG，用于不戴头显检查骨骼和表情反馈。

## 3. Quest 3 设备检查

APK 位于 Builds/Banxia.apk，包名为 com.lingxi.banxia。只需在设备上确认以下项目：

1. 通过 Meta Quest Developer Hub 或 adb 安装 APK。
2. 启动伴夏。
3. 确认模型能加载、贴图正常、没有黑屏或崩溃。
4. 放下控制器并确认系统显示双手，然后验证握手、摸头和捏脸。
5. 记录是否出现明显卡顿、发热、刚体抖动、误触或纹理缺失。

真手空间精度、追踪丢失、真实 Passthrough 和性能只能在 Quest 3 上确认；当前 APK 已配置 Meta OpenXR Passthrough provider；房间画面、遮挡和光照仍需 Quest 3 真机截图确认。

房间语义通道只在观察到房间平面后工作：内容变化时发送，并每 15 秒低频续租；追踪丢失后停止续租，后端 30 秒未收到更新会丢弃旧事实。协议负载只允许有界计数和能力布尔值，不能包含图像、网格、坐标、尺寸、锚点、文件路径或自由文本。

菜单主界面的“调试”会显示一份脱敏运行快照：手追数量、触碰与骨骼匹配、VAD 电平/阈值、上传队列、首事件/首音频/结束耗时、TTS 缓冲与欠载、彩透相机状态、身高/地面、房间平面计数以及 VMD 保持/回退阶段。快照不包含后端地址、密钥、身份 ID、对话正文或动作文件名。

从 Quest 系统菜单返回应用时，彩透会根据用户离开前的开关状态恢复：原本开启则重启相机 provider，原本关闭则保持关闭。二级菜单和内置键盘切换后必须先松开扳机/捏合，射线只允许命中当前最上层面板。

## 4. 任意 PMX 导入验收

把一个 PMX 和它引用的贴图放在可读目录，调用：

~~~csharp
await loader.LoadFromFileAsync(pmxPath, textureDirectory);
~~~

验收标准：模型根节点生成、网格和材质数量大于零、贴图无缺失、骨骼和 MMD 物理组件存在。头显中文菜单“动作 -> 导入文件”可选择 PMX 与贴图、单个 VMD 或 ZIP；本地 VMD 也可放入 `Application.persistentDataPath/Motions` 顶层目录，在中文动作页刷新、选择、播放和停止。AstrBot 不能传入任意本地路径。

运行时 PMX 加载按纹理、材质和独立网格组让出帧预算；PNG/JPG 使用 Unity 异步纹理解码，TGA 文件读取与像素解码在后台执行。最近两个已解析 PMX 会保留 180 秒，低内存时释放非当前项；成功选择的设备内模型会保存，并在下次启动时自动恢复。PlayMode 可通过 `BANXIA_TEST_PMX` 指向仓库外的真实 PMX，输出 `totalMs`、`longFrames`、`maxFrameMs` 和缓存命中结果。

## VMD 动作与真机门禁

.vmd 文件必须来自用户明确许可的来源；应用扫描 Motions 顶层 .vmd 或动作包目录，并对每个轨道执行 16 MiB、100000 关键帧、120 秒限制；动作包总关键帧数仍受同一上限保护。设备在线且电量足够前不安装 APK、不唤醒、不截图。

动作播放期间由 VMD 独占骨骼最终写入，UMT 实时 IK/物理暂时停用，转换阶段会烘焙 IK 与物理轨道；停止或自然结束后先用约 0.65 秒将骨骼和表情混合回绑定姿势，再恢复原物理设置并回到待机。Quest 菜单的“外观 -> 画质”分别提供渲染画质和 MMD 物理控制并保存到本机。物理性能档为 60Hz/2 子步并降低手部接触频率，平衡档为 60Hz/2 子步并保留完整手部接触，精细档为 120Hz/4 子步并保留完整手部接触；森林莓果等重关节模型只使用一份锁定平移强化。

设备性能页只将“已佩戴且应用有焦点”的帧计入 5 秒、30 秒与本次会话统计。模型切换或重新佩戴会建立新窗口，单纯打开性能页不得重置窗口；离头阶段暂停 MMD 时间推进，不得增加本次佩戴物理丢弃。面板应同时显示 OpenXR App CPU/GPU 帧时、利用率、合成器丢帧，以及 MMD 采样、骨骼/IK、Bullet、回写、SDEF、手部接触和描边提交耗时。

森林莓果性能验收：佩戴后预热 10 秒，连续采样 60 秒，至少 95% 的一秒窗口达到 71 FPS，合成器丢帧低于 1%，CPU/GPU P95 不超过 13.89ms。分别进行手追开/关、描边开/关和物理性能/平衡/精细三档 A/B；物理丢弃应低于 0.1 秒/分钟，头发、裙摆和手部碰撞不得出现爆炸、明显抖动或穿透恶化。

动作协议验收：`session.start` 声明 `supported_actions` 后才能协商新增动作。明确“下蹲/蹲下/crouch/squat”不等待快速动作模型；客户端必须按 `accepted -> started -> completed` 或 `rejected/interrupted` 回报。缺腿骨返回 `asset_missing`，同轮第二个全身动作返回 `superseded`，计划和 accepted 状态不得被回复文本描述为已经完成。

## 后端绑定与文件选择器验收

绑定菜单输入域名/IP:端口和 6 位码，应用自动补全插件路径。当前 Unity 2022 构建不提供头显相机扫码；不要把“SDK 无法获取相机画面”作为可用入口。

Android 构建后必须用 APK/Dex 分析确认 com.lingxi.banxia.filepicker.BanxiaFilePicker 与 BanxiaFilePickerActivity 均存在，并在真机点击“导入文件”确认系统 ACTION_OPEN_DOCUMENT 页面出现。

## 当前状态

| 项目 | 状态 | 证据 |
|---|---|---|
| Unity 项目和 UMT 包解析 | 已完成 | Packages/com.candidumgames.unitymmdtools/package.json |
| 运行时 PMX 导入 | 已完成 | RuntimeMmdModelLoader.cs、PMX 冒烟测试 |
| 桌面端预览 | 已完成 | 伴夏 > Render Model Preview |
| Quest APK 构建 | 已完成 | Builds/Banxia.apk |
| XR Hands/控制器输入 | 已完成，待真机体验确认 | AvatarHumanInteraction.cs、AvatarTouchInteraction.cs |
| 握手/摸头/捏脸传感 | 已完成；默认本地即时反应，语义上报显式开启 | HUD 模拟、Mock avatar.intent、编辑器测试 |
| 真 Passthrough | 已配置，待真机验收 | Meta OpenXR provider 与 AR Camera |
| Mock 可打断对话、PCM 流播放 | 已完成第一切片 | ConversationController、ConversationStateMachine |
| 音量驱动嘴型和对话注视 | 已完成降级实现 | AvatarConversationPresenter |
| AstrBot HTTP/SSE、8520 配对、Quest 麦克风/VAD | 已接入，模型已选，待真机闭环 | AstrBotBridge、BackendPairingController、QuestMicrophoneInput |
| 语音无事件/事件停滞恢复 | 已实现，待真实断网与后端超时验收 | ConversationController、ConversationStateMachine |
| 脱敏运行诊断快照与阶段时间线 | 已实现 | RuntimeDiagnosticsSnapshot、RuntimeDebugLog、菜单左侧调试区；覆盖配置、授权、SSE、麦克风、上传、STT、EventBus、LLM、TTS、播放和结束阶段 |
| 连续手臂 IK 与自然注视 | 已实现，待真机调参 | AvatarPresence、AvatarController、AvatarTouchInteraction |
| Quest 3 真机显示与性能 | 待测试 | 需要设备 |
# Device VMD QA

`run_vmd_qa` is an ADB-only bounded scenario for investigating first-play stalls. It accepts installed model and action indices, runs one cold and one cached preparation, logs only indices and numeric timing/physics data, restores the previously selected model, and exits by default:

```powershell
adb shell am force-stop com.lingxi.banxia
adb shell am start -n com.lingxi.banxia/com.unity3d.player.UnityPlayerActivity --ei quest_debug_model_index 3 --ei quest_debug_action_index 0 --ez quest_debug_exit true --es quest_debug_command run_vmd_qa
```

Use `logcat` to collect `[BanxiaQA] vmd_pass` entries. Index values are clamped to the current on-device catalog. This command never accepts a filesystem path and does not expose pairing credentials.

跨重启缓存验收需连续运行上面的完整命令两次。每轮先检查 `vmd_catalog`：目录扫描应给出 `elapsed_ms/action_count/physics_drop_delta_s`，且扫描期间物理丢弃应为零。第一次 `cold` 应显示 `disk_cache=False` 并完成曲线写入；应用退出后第二次 `cold` 应显示 `disk_cache=True`、`motion_ms=-1`，且 `disk_read_ms + disk_rebuild_ms` 明显小于第一次转换耗时。两次都必须保持 `physics_drop_delta_s=0.0000`，损坏或删除 `VmdActionCache/v1` 后应安全回退首次转换。

# Device Performance QA

`run_performance_qa` 是仅通过 Android Intent 触发的有界采样场景。它按模型索引加载角色，默认预热 10 秒、采样 30 秒，输出一条 `[BanxiaQA] performance_result`，随后恢复原模型选择和临时性能设置并退出 App：

```powershell
adb shell am force-stop com.lingxi.banxia
adb shell am start -n com.lingxi.banxia/com.unity3d.player.UnityPlayerActivity --es quest_debug_command run_performance_qa --ei quest_debug_model_index 3 --ei quest_debug_warmup_seconds 10 --ei quest_debug_sample_seconds 30 --es quest_debug_physics_profile balanced --es quest_debug_hand_contact on --es quest_debug_outline on
```

可选物理档严格限制为 `performance|balanced|precise`，手部接触和描边严格限制为 `on|off`。`hand_contact=off` 保留视觉手模型，但完整停用精确接触、`Physics.SyncTransforms()` 和外部 Bullet 手部探针，不能理解成低频接触。预热最多 30 秒，采样最多 120 秒；不接受文件路径。头显未佩戴或 App 未获得焦点时不计入有效帧，整个窗口没有有效帧会报告失败而不是输出全零的成功结果。

森林莓果 A/B 先固定 `balanced/on/on` 作为基线，再一次只改一个变量：`performance/on/on`、`precise/on/on`、`balanced/off/on`、`balanced/on/off`。至少比较 `frame_p95_ms`、`xr_cpu_p95_ms`、`xr_gpu_p95_ms`、`physics_drop_s`、`mmd_physics_p95_ms`、`mmd_bone_ik_p95_ms`、`hand_contact_p95_ms` 和 `outline_submit_p95_ms`，不要把未佩戴、模型加载或预热数据混入结论。旧的 `xr_cpu_ms`、`bullet_ms` 等字段只是采样结束瞬时值，只用于兼容诊断，不用于归因。
