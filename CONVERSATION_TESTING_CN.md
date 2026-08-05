# 前端对话闭环测试

当前实现只测试 Unity 前端能力。`MockConversationTransport` 代替尚未接入的 AstrBot 插件，按照未来相同的事件顺序返回识别文字、思考状态、回复文字、PCM 音频和 `avatar.intent`；表情、动作与注视目标都由该意图决定。

## 电脑测试

1. 用 Unity 打开项目并运行 `Assets/Scenes/Prototype.unity`。
2. 等模型显示后，在左上角输入任意文字。
3. 点击 `Start mock conversation`。
4. `Conversation` 应依次显示 `Listening`、`Thinking`、`Speaking`、`Idle`。
5. `Heard` 和 `Reply` 应逐步出现文字，并能听到一段低音量的模拟语音。
6. 如果模型有 A/I/U/E/O 或“あいうえお”嘴型，`Presenter` 的 `visemes` 应大于 0，播放时嘴部会变化。
7. 再次开始对话并在播放中点击 `Interrupt`；声音应立即停止，状态短暂显示 `Interrupted` 后回到 `Idle`。

## 后端驱动交互测试

1. 点击 `Handshake`、`Head pat` 或 `Cheek pinch`。
2. Unity 先产生交互传感事件，不直接决定角色反应。
3. Mock 代替 AstrBot 返回相应的 `avatar.intent`，角色才执行动作和表情。
4. 两秒后 Mock 返回恢复意图，角色回到 `idle/neutral`。

这个 Mock 只是测试替身。接入真实 AstrBot 后，接受、拒绝、害羞、躲避、说话或执行哪种动作，全部由 AstrBot 插件决定；Unity 只执行受约束的结构化意图。

## 当前限制

- 没有接麦克风、VAD、真实 ASR/TTS 或 AstrBot WebSocket。
- 模拟声音是程序生成的语音状波形，不是真正 TTS。
- 当前嘴型是音量驱动降级方案，不是准确的音素识别。
- PMX 缺少标准嘴型时仍可播放声音；只有收到 `look_at` 意图才会注视，不会因缺嘴型报错。
- 以上测试都可在电脑完成，不需要 Quest 3。
