# AstrBot Embodiment Bridge Unity 接入

Unity 客户端实现 Embodiment Bridge 协议 `1.0`，使用 HTTP POST 上行和 SSE 下行。协议本身不限定设备；伴夏当前以 Meta Quest 为首个参考客户端。配置不会写入 APK 或 Unity 场景。

## 配置位置

Quest 包名为 `com.lingxi.banxia`。配置文件路径：

```text
/sdcard/Android/data/com.lingxi.banxia/files/embodiment_bridge.json
```

推荐通过应用内 6 位短码绑定生成配置。需要受控调试时，完整 `base_url` 必须包含插件路径：

```text
https://<astrbot-host>/api/v1/plugins/extensions/astrbot_plugin_embodiment_bridge
```

通过 ADB 安装配置：

```powershell
adb -s <serial> push embodiment_bridge.json /sdcard/Android/data/com.lingxi.banxia/files/embodiment_bridge.json
```

重启应用后，`AstrBotBridge` 会执行 `health -> session/start -> events/<session_id>`。SSE 断开时复用现有会话重连；服务端返回 `404` 时创建新会话。

## 旧版迁移

若新配置尚不存在，客户端会读取旧的 `quest_avatar_bridge.json`，将精确的旧插件路径迁移到 `astrbot_plugin_embodiment_bridge`，然后原子写入新文件。旧文件保留用于降级，不会覆盖已经存在的新配置。旧配对服务器偏好和精确旧二维码路径也会迁移到新路径。

新客户端使用 `X-Embodiment-Bridge-Key`。后端在 1.0 兼容期仍接受旧 `X-Quest-Avatar-Key`；二维码类型 `astrbot.quest.pair` 暂时保持不变，它是已发布的线上载荷字段，不代表当前插件仍绑定 Quest。

## 网络安全

公网必须使用可信 HTTPS。私网调试可由用户在配对界面显式允许 HTTP，但只接受字面量私网 IP，不接受域名，避免 DNS 重绑定；两个认证头会以明文经过该局域网链路，因此不得在不可信网络启用。8520 内置 listener 只匿名开放精确的配对交换路径，其他协议请求仍需要 AstrBot API Key 与 Bridge Key。

## 前端职责

- 交互只上报 `start/update/end/cancel` 事实，不本地决定开心、害羞或拒绝。
- 交互期间每秒上报一次 `update` 和累计 `duration_ms`。
- 仅执行协议白名单内的语义意图；当前模型不支持的 `refuse`、`step_back` 和 `look_at=hand` 降级为 `idle/none`。
- 旧会话事件、未知协议版本、错误 PCM16 和事件名/类型不匹配的数据会被丢弃。
- Android 未配置或断网时保持中性，不启用编辑器 Mock 人格反应。

STT/TTS 是否可用由后端 `/health` 决定。当前 Unity 已支持后端输出的 PCM16 24000 Hz 音频播放和真实播放电平嘴型；麦克风上传将在后续切片实现。
