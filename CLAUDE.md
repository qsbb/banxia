# banxia 项目记忆（AI 协作规范）

## 硬约束（最高优先级，任何任务前重读）

1. **只改凝心溯溪系列仓库**：banxia（本仓库，含 vendored
   `Packages/com.candidumgames.unitymmdtools/`）与
   `astrbot_plugin_embodiment_bridge`（临桥）。**绝不**直接改 AstrBot 核心、
   第三方插件、远端容器。交付方式：提交推送 GitHub → 用户手动更新远端。
2. vendored 包改动：**单独 commit** 并同步更新 `BANXIA-PATCHES.md`。
3. banxia 的 `Assets/Scripts/**` .cs 文件必须带 UTF-8 BOM（编辑后用脚本补）；
   vendored 包文件保持无 BOM 纯 ASCII。

## 双端同步原则（用户钦定，2026-08）

> **无论开发什么功能，只要不是设备生态独占的，VR 端和手机端总是同步。**

- 新功能立项即按双端交付设计；可先在便于测试的一端验证，但不得长期单端
- 独占判定：本质依赖某端独有硬件才算——MR 透视/房间理解/手部追踪
  （Quest 独占）、随身摄像头单帧（手机独占）；对话/物理/动画/诊断/设置
  **永不独占**
- 技术保障：业务逻辑写平台无关层，`#if BANXIA_PHONE` 只隔离平台壳层；
  业务逻辑出现平台分支 = 需要重构的信号
- 单端先行必须在 `PHONE_PORT_PLAN_CN.md` 登记"待同步"
- 手机端移植方案见 `PHONE_PORT_PLAN_CN.md`（含 reality_companion 功能审计
  结论：只采纳摄像头单帧，其余不做）

## 关键事实速查

- Unity 2022.3.62f3c1 / Quest 3 / Vulkan / IL2CPP ARM64；构建机
  192.168.5.55（用户 Windows，可能随出门关机）；构建方法
  `QuestMmdPlayerBuild.BuildAndroidApk`；手机端变体
  `BuildAndroidPhoneApk`（规划中）
- 模型 kokona/ForestBerry 不打包进 APK，用户装于设备
  `persistentDataPath/MmdModels/`；加载器不校验模型哈希
- MMD 物理：30/60Hz 物理 vs 72Hz 显示；0 子步帧曾回跳动画姿态
  （M1 修复，指标 `pose_src_flip`）；Quest deltaTime bug #7410 防御已加
- 已修：音频线程静音（v0.2.3 回归，build 17 待实测）；物理抽搐 M1/M2/M3
  已推送 GitHub，构建装机验证待构建机
- 测试基线：临桥 `pytest -q` 543 通过 / 27 项既有失败

## 协作习惯

- 用户要根因分析 + 具体修复，不满足于"清理重启"式答案
- 审计/分析结论必须本地复算验证（曾有第三方审计错误结论：披风↔四肢穿插
  实为不存在；复算后真实问题是绒球/头发 vs 躯干）
- 每里程碑独立 commit + push；构建验证前不宣称完成
- 时间线：Quest logcat 为 CST，容器为 UTC-8

## 设备与测试资源使用纪律（用户钦定，2026-08-29）

- **模拟器（192.168.5.21）**：用完立即 `pkill -f qemu-system` 释放内存
  （该机常驻他人服务，内存紧张）；需要时才用 `~/banxia-emu/start-emu.sh`
  重启（约 20-35 秒开机，含 available≥1800M 内存护栏）
- **Quest 头显**：需要测试时才启动伴夏（monkey/am start）；测试完
  `am force-stop com.lingxi.banxia` 退出应用，**绝不关机/重启设备**
- **构建机（192.168.5.55）**：需要编译 APK 时才请用户开机；构建完即告知
  用户可关机（本次会话已验证：用户离开时会主动关 5.55）
