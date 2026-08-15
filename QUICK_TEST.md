# 伴夏一键测试

这套测试不需要 Unity 编辑器或 Quest 设备，先检查项目文件和 PMX 导入契约：

~~~powershell
powershell -ExecutionPolicy Bypass -File .\test_frontend.ps1 -Strict
~~~

看到 Automated checks passed. 后，电脑端只剩 Unity 导入冒烟：

1. 打开 Unity 项目 banxia。
2. 执行 伴夏 > Run Runtime PMX Smoke Test。
3. Console 出现 [Runtime PMX Smoke Test] PASS 即表示 PMX、贴图、骨骼和刚体已经在电脑上真实构建过。
4. 执行 伴夏 > Build Android APK 生成 Builds/Banxia.apk。

只有下面几项必须戴 Quest 3 才能确认：

- APK 能否安装和启动
- 视野中的模型、贴图和帧率
- 真 Passthrough 画面
- 手势/手柄输入
- 长时间运行的发热和稳定性

生产 APK 不内置角色模型。请在设备端导入并选择 PMX；编辑器冒烟测试通过文件选择器读取本地 PMX，模型和贴图不会进入版本库或 APK。
## Quest 当前交互

- 左手菜单键：在面前打开中文 World Space 菜单。
- 手掌/指尖或控制器靠近角色：握手、摸头和捏脸；断网时启用中性的本地物理回退。
- 动作页：刷新、选择、播放和删除 `Motions` 中的合规 VMD；挥手、鞠躬等语义动作不再提供菜单按钮，由后端 `avatar.intent` 或断网回退驱动。
- 绑定页：首次私网配对显式打开“局域网 HTTP”，服务端使用 `192.168.5.88:8520`，再输入六位配对码。
- 高度定位：按头显水平前向和地面检测重新放置角色，默认面对用户。

真机门禁：设备必须在线且电量足够；离线或低电量只运行静态检查、Unity EditMode 和 Android 构建。
