# Quest MMD Player 测试说明

当前阶段已接入 PMX/动作包格式：Motions/<动作名>/motion.vmd，可选 facial.vmd。
VMD 运行时导入、XR Hands、三种触碰传感、Quest 麦克风、Meta Passthrough 和 AstrBot HTTP/SSE；仍需真机确认房间画面、追踪精度、配对和语音延迟。

## 1. 静态检查

在 `quest_mmd_player` 项目根目录执行：

~~~powershell
powershell -ExecutionPolicy Bypass -File .\test_frontend.ps1 -Strict
~~~

看到 Automated checks passed. 即表示文件、包声明和关键源码契约通过。

## 2. Unity 编辑器检查

1. 用 Unity 2022.3.62f3c1 打开 quest_mmd_player。
2. 执行 Quest MMD Player > Create Prototype Scene。
3. 打开 Assets/Scenes/Prototype.unity 并点击 Play。
4. 首次运行会将 PMX 和贴图复制到 Unity 持久化目录，再显示模型和 HUD。
5. Console 应看到 PMX avatar ready；如果导入失败，会显示回退人偶和明确错误。
6. 用 W/A/S/D、Q/E、R/F、1/2/3、Space 验证基础交互。
7. 点击 HUD 的 Handshake、Head pat、Cheek pinch，确认 Unity 先上报传感事件，Mock 返回 `avatar.intent` 后角色才反应。
8. 输入文字并点击 Start mock conversation，确认 Listening -> Thinking -> Speaking -> Idle；播放中点击 Interrupt 应立即停声。
9. 完整步骤见 CONVERSATION_TESTING_CN.md。

在不进入 Play 的情况下，可执行 Quest MMD Player > Run Runtime PMX Smoke Test。该测试直接读取 Assets/StreamingAssets/MmdSamples/ForestBerry/ForestBerry.pmx，验证网格、材质、纹理、骨骼和刚体数量。

Quest MMD Player > Render Model Preview 会用同一条 PMX 运行时导入链生成 Builds/ForestBerryPreview.png，用于桌面端查看画面。

Quest MMD Player > Render Human Interaction Previews 会生成三种交互的多角度 PNG，用于不戴头显检查骨骼和表情反馈。

## 3. Quest 3 设备检查

APK 位于 Builds/Banxia.apk，包名为 com.lingxi.banxia。只需在设备上确认以下项目：

1. 通过 Meta Quest Developer Hub 或 adb 安装 APK。
2. 启动伴夏。
3. 确认模型能加载、贴图正常、没有黑屏或崩溃。
4. 放下控制器并确认系统显示双手，然后验证握手、摸头和捏脸。
5. 记录是否出现明显卡顿、发热、刚体抖动、误触或纹理缺失。

真手空间精度、追踪丢失、真实 Passthrough 和性能只能在 Quest 3 上确认；当前 APK 已配置 Meta OpenXR Passthrough provider；房间画面、遮挡和光照仍需 Quest 3 真机截图确认。

## 4. 任意 PMX 导入验收

把一个 PMX 和它引用的贴图放在可读目录，调用：

~~~csharp
await loader.LoadFromFileAsync(pmxPath, textureDirectory);
~~~

验收标准：模型根节点生成、网格和材质数量大于零、贴图无缺失、骨骼和 MMD 物理组件存在。头显中文菜单“动作 -> 导入文件”可选择 PMX 与贴图、单个 VMD 或 ZIP；本地 VMD 也可放入 `Application.persistentDataPath/Motions` 顶层目录，在中文动作页刷新、选择、播放和停止。AstrBot 不能传入任意本地路径。

## VMD 动作与真机门禁

.vmd 文件必须来自用户明确许可的来源；应用扫描 Motions 顶层 .vmd 或动作包目录，并对每个轨道执行 16 MiB、100000 关键帧、120 秒限制；动作包总关键帧数仍受同一上限保护。设备在线且电量足够前不安装 APK、不唤醒、不截图。

动作播放期间由 VMD 独占骨骼最终写入，UMT 实时 IK/物理暂时停用，转换阶段会烘焙 IK 与物理轨道；停止或自然结束后恢复原物理设置并回到待机。Quest 菜单的“外观 -> 画质”提供性能、平衡、清晰和恢复默认四档，立即调整 XR 眼纹理比例与 URP MSAA，并保存到本机。

## 当前状态

| 项目 | 状态 | 证据 |
|---|---|---|
| Unity 项目和 UMT 包解析 | 已完成 | Packages/com.candidumgames.unitymmdtools/package.json |
| 运行时 PMX 导入 | 已完成 | RuntimeMmdModelLoader.cs、PMX 冒烟测试 |
| 桌面端预览 | 已完成 | Builds/ForestBerryPreview.png |
| Quest APK 构建 | 已完成 | Builds/Banxia.apk |
| XR Hands/控制器输入 | 已完成，待真机体验确认 | AvatarHumanInteraction.cs、AvatarTouchInteraction.cs |
| 握手/摸头/捏脸传感 | 已完成；默认本地即时反应，语义上报显式开启 | HUD 模拟、Mock avatar.intent、编辑器测试 |
| 真 Passthrough | 已配置，待真机验收 | Meta OpenXR provider 与 AR Camera |
| Mock 可打断对话、PCM 流播放 | 已完成第一切片 | ConversationController、ConversationStateMachine |
| 音量驱动嘴型和对话注视 | 已完成降级实现 | AvatarConversationPresenter |
| AstrBot HTTP/SSE、8520 配对、Quest 麦克风/VAD | 已接入，模型已选，待真机闭环 | AstrBotBridge、BackendPairingController、QuestMicrophoneInput |
| 连续手臂 IK 与自然注视 | 已实现，待真机调参 | AvatarPresence、AvatarController、AvatarTouchInteraction |
| Quest 3 真机显示与性能 | 待测试 | 需要设备 |
