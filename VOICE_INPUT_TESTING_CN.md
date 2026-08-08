# Quest 语音输入测试

## 已实现链路

- 左摇杆按下并保持开始录音，松开后发送。
- 世界菜单中的 `TALK / SEND` 可切换开始和结束录音。
- `INTERRUPT` 立即停止当前回复和本地语音播放。
- 录音转换为 PCM16、小端、单声道、16000 Hz。
- 输入按 80 ms 分块，每块 2560 字节，`sequence` 从 0 严格递增。
- 客户端最多缓存约 10 秒输入；网络跟不上时终止轮次，不无限占用内存。
- 说话前先打断旧轮，后端迟到的旧文字、音频和动作不会继续生效。
- `audio/end` 之后继续异步等待 SSE；35 秒没有首事件会结束故障轮次，已有事件后 30 秒不再推进也会结束，并自动恢复常开监听。
- `reply.end` 会封口当前轮次；结束后迟到的正文和音频不会重新打开播放队列。

## 无人佩戴时的真机部署

设备重新连接后，覆盖安装 APK，再显式授予麦克风权限：

```powershell
adb -s 2G0YC5ZHBF00R0 install -r -d Builds\Banxia.apk
adb -s 2G0YC5ZHBF00R0 shell pm grant com.lingxi.banxia android.permission.RECORD_AUDIO
```

应用内不会包含 AstrBot 密钥。配置仍放在：

```text
/sdcard/Android/data/com.lingxi.banxia/files/quest_avatar_bridge.json
```

Quest Android 构建要求后端使用 HTTPS。

## 后端状态

`health` 返回 `input_audio.stt_available=true` 时，真语音识别链可用。若返回 false，Unity 仍会验证录音、PCM 和上传链，但 `audio/end` 后会收到 `stt_unavailable`，不会伪造识别文本。

`health` 返回 `output_audio.tts_available=true` 时，后端会输出 PCM16、单声道、24000 Hz。Unity 按真实播放缓冲驱动嘴型。

## 离线验证

EditMode 覆盖：

- 48000 Hz 到 16000 Hz 的确定性重采样。
- PCM16 小端编码和幅度夹紧。
- 同一 `turn_id` 的 `start -> chunk -> end` 顺序。
- 后端离线时拒绝开始录音轮次。
- 交互轮次的文字、语音、动作和结束事件。
- 旧交互事件不能污染最新轮次。
- 无首事件与事件流停滞的超时分类。
- `reply.end` 后拒绝迟到正文/音频，重复 `reply.end` 保持幂等。

真机仍需在设备上线后验证：麦克风硬件、权限、回声、实际 STT/TTS 延迟和免耳机打断。
