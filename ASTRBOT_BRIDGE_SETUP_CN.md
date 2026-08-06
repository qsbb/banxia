# Quest Avatar Bridge Unity 接入

Unity 客户端实现 Quest Avatar Bridge 协议 `1.0`，使用 HTTP POST 上行和 SSE 下行。配置不会写入 APK 或 Unity 场景。

## 配置位置

Quest 包名为 `com.qsbb.banxia`。配置文件路径：

```text
/sdcard/Android/data/com.qsbb.banxia/files/quest_avatar_bridge.json
```

模板位于 `Builds/quest_avatar_bridge.example.json`。完整 `base_url` 必须包含插件路径：

```text
https://<astrbot-host>/api/v1/plugins/extensions/astrbot_plugin_quest_avatar_bridge
```

通过 ADB 安装配置：

```powershell
adb -s 2G0YC5ZHBF00R0 push quest_avatar_bridge.json /sdcard/Android/data/com.qsbb.banxia/files/quest_avatar_bridge.json
```

重启应用后，`AstrBotBridge` 会执行 `health -> session/start -> events/<session_id>`。SSE 断开时复用现有会话重连；服务端返回 `404` 时创建新会话。

## 网络安全

Quest APK 只接受 HTTPS。`allow_insecure_http=true` 仅供 Unity 编辑器中的受控协议测试使用，不能放宽 Android 构建。AstrBot 在局域网只提供 HTTP 时，应先通过可信反向代理提供 HTTPS；两个认证头不得明文传输。

## 前端职责

- 交互只上报 `start/update/end/cancel` 事实，不本地决定开心、害羞或拒绝。
- 交互期间每秒上报一次 `update` 和累计 `duration_ms`。
- 仅执行协议白名单内的语义意图；当前模型不支持的 `refuse`、`step_back` 和 `look_at=hand` 降级为 `idle/none`。
- 旧会话事件、未知协议版本、错误 PCM16 和事件名/类型不匹配的数据会被丢弃。
- Android 未配置或断网时保持中性，不启用编辑器 Mock 人格反应。

STT/TTS 是否可用由后端 `/health` 决定。当前 Unity 已支持后端输出的 PCM16 24000 Hz 音频播放和真实播放电平嘴型；麦克风上传将在后续切片实现。
