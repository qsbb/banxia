# M7 · 用户反馈六条批次（2026-09-08 计划稿）

> 来源：用户真机反馈六条（对话改名/导入无反应/更新入口下沉/公网连通+测试按钮/
> 设置页被 dock 挡/进场景黑屏要加载页）。本文档为实施计划，**批准前不改代码**。
> 铁规提醒：NAS/AstrBot 侧只读诊断随意，任何变更须用户当次授权；版本日期段
> 出包前自动滚到构建日（§5 新口径）。

---

## P0 · 诊断先行（不改代码，产出根因结论）

### P0-1 公网连接中断诊断（反馈 4a，现在进行时）
1. NAS 只读：`ss -ltnp | grep 8520` 确认临桥监听器活着；`curl -s http://127.0.0.1:8520/.../health` 容器内自检。
2. 域名解析检查：`getent hosts <域名>`（注意该域名只有 IPv6 AAAA——若手机蜂窝走 IPv4-only 链路，这本身就是连不上的候选根因）。
3. 路由器 8520 端口转发与 NAT 环回：从 NAS 用公网域名 curl 自检（环回通不代表外网通，但可排除服务侧）。
4. 手机侧（若 adb 在线）：logcat 抓最近一次连接失败原文（SSL/超时/拒绝/解析失败），`settings` 里当前 activeEndpoint 是哪条。
5. 产出：根因一句话 + 是否需要用户侧动作（如重配对/路由器检查）。

### P0-2 导入按钮无反应诊断（反馈 2）
1. 真机 adb 在线时点一次「导入模型/动作文件」，抓 logcat：`BanxiaFilePicker|FlutterUiFacade|ActivityManager`。
2. 三分支判定：
   - 无任何日志 → 手势未到达（布局/浮层问题）；
   - 有 `HandleModelImport` 无 `BanxiaFilePickerActivity` 启动 → OpenPicker 失败路径；
   - Activity 启动即崩溃/被 MIUI 拦 → 看 ActivityManager 拒绝原因。
3. 产出：根因 + 修复方案（可能是 MIUI 后台启动限制、或按钮热区、或选择器 Intent 兼容）。

---

## P1 · 小改批次（一次构建）

### P1-1 文案改名（反馈 1）
- `companion_screen.dart` `_ChatHeroCard`：「和伴夏聊聊」→「和ta聊聊」。
- `chat_page.dart` AppBar 标题：「伴夏」→「ta」（保持两处一致；副标题状态行不动）。
- 验收：flutter analyze + 37 测试过；模拟器截图确认两处文案。

### P1-2 首页「更新」瓦片删除（反馈 3）
- `companion_screen.dart` `_QuickTiles`：删第二行（更新+空白占位），只留「动作/设置」一行。
- 设置页内更新功能（`update.check/install`）不动。
- 验收：首页快捷入口一行两瓦片；设置页更新项仍在。

### P1-3 三标签页底部被胶囊遮挡（反馈 5，中央修法）
- `root_shell.dart` `_MenuShell`：`IndexedStack` 外包
  `Padding(bottom: 64 + 20 + 20 + MediaQuery.viewPaddingOf(context).bottom)`
  （胶囊高 64 + 下 margin 20 + 胶囊自带 margin 20 + 手势区），三页内容统一让位。
- 删除各页零散补偿（`companion_screen.dart` 末尾 `SizedBox(height: 24)` 改为 8 即可；
  settings/actions 页滚动底部自然露出）。
- 验收：模拟器滑到设置页最底，最后一个开关完全露出；首页/动作页同查；
  对话二级页不受影响（路由无胶囊）。

---

## P2 · 入口连通性测试按钮（反馈 4b）

协议新增（一条 Cmd + 结果事件复用 toast/行内态）：
1. `bridge_protocol.dart`：`Cmd.pairingEndpointTest = 'pairing.endpointTest'`，
   payload `{url}`。
2. 引擎 `AstrBotProtocol.cs` 新增 `TestEndpoint(url)`：GET `<url>` 拼接 health 路径
   （复用配对 health 路径常量），带双认证头，5 秒超时，返回
   `{ok, httpCode, elapsedMs, error}`。
3. `FlutterUiFacade.cs`：`HandlePairingEndpointTest` 转发，结果走
   `FlutterCommandResult` 同步返回（dispatch 的 ack 通道，无需新事件）。
4. `app_state.dart`：`testEndpoint(String url)` → 返回结果字符串
   （`✓ 200 · 87ms` / `✗ 超时` / `✗ SSL` / `✗ 拒绝`）。
5. `settings_screen.dart` `_EndpointSection` 每行右侧加小号「测试」按钮
   （`labelTertiary` 13px），点击后行内副标题位显示结果 5 秒（本地 state）。
6. 测试：EditMode 加 `TestEndpoint` 的 URL 拼接/超时/错误分支单测；
   flutter test 加行内结果显示用例。
7. 验收：模拟器对内网入口测试 ✓；对公网域名测试与 P0-1 诊断结论一致。
8. **安全**：测试走已配对凭据，不在 UI 显示密钥；结果不落盘。

## P3 · 场景加载门（反馈 6）

1. 引擎 `RuntimeMmdModelLoader`：加载管线插 5 个进度点
   （0% 开始/20% 文件读取/50% 解析/80% 网格与物理/100% 就绪），
   经 Facade 发新事件 `Evt.modelLoadProgress {fraction, line}`
   （`line` = 单行日志，如「正在构建网格…」）。
2. `app_state.dart`：`conversation` 同级新增 `modelLoading {active, fraction, line}`；
   `enterScene` 置 active=true，`loadProgress` 事件更新，fraction==1 或失败事件关闭。
3. `root_shell.dart`：`inScene && modelLoading.active` 时 `SceneOverlay` 之上盖
   全屏加载页（`bg` 不透明 + 角色头像 + 进度条 + 单行日志滚动显示最后一条 +
   失败时错误行+「返回」按钮调 `returnToMenu`）——**就绪前不进场景画面**。
4. 失败路径：加载器异常 → `fraction:-1` + 错误行，加载页停留并给返回出口，
   杜绝黑屏干等。
5. 验收：模拟器进入 ForestBerry（2.3MB 秒载，日志行一闪而过——人为往
   MmdModels 塞大文件或 debug 延时验证进度页可见）；真机 kokona 实载看进度条；
   返回键在加载页 = 取消并回首页。
6. 双端：Evt 协议双端共享，Quest 端同壳层自动获得加载门（登记 PHONE_PORT_PLAN
   VR 回归项）。

---

## 构建与发布（每批次）

- P1/P2/P3 各自独立 commit+push；flutter analyze 0 issue + flutter test 全绿才构建。
- 出包前日期段滚到构建当天（§5 新口径），构建机 5.55 逐文件同步（非 git 仓库）。
- 发布到 NAS 文件传输文件夹：`Banxia-Phone-0.3.3.yyyymmdd-<短hash>.apk` +
  .sha256 + .metadata.txt，`sha256sum -c` 核验，本地副本即发即删（tmpfs 256M 坑）。
- 真机验证清单（用户在场）：① 英雄卡文案/进入返回；② 导入按钮（P0-2 修复后）；
  ③ 设置页滑到底无遮挡；④ 入口测试按钮对公网/内网各点一次；⑤ 进场景加载页
  全程可见、就绪才进场。
- Quest 零回归抽查：头显启动一次，确认三标签导航+英雄卡在 VR 壳层不破版。

## 依赖与顺序

P0 无依赖立即跑（4a 需用户授权 NAS 侧只读诊断已含）；P1 不等 P0 结论可直接做；
P2 依赖 P0-1 根因（若公网链路断是路由器/域名层，按钮做出来也测不通，先修链路）；
P3 独立。建议执行序：P0-1 → P1（出包）→ P0-2 → P2 → P3（合并一次出包）。
