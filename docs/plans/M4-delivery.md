# M4 · 交付链与收尾

> 前置：M1–M3 各自验收通过（不变量变绿），才进入本阶段
> 纪律（CLAUDE.md）：每里程碑独立 commit + push；构建验证前不宣称完成；模拟器/构建机用完释放

---

## 1. 交付步骤（按序执行）

### 步骤 1 · 终版构建（若 M3 与 M2 已合并构建，此步可跳过）
```
cd /data/dsh/home/dsh/banxia
SSH="ssh -i /data/dsh/home/.ssh/id_ed25519 -o UserKnownHostsFile=/data/dsh/home/.ssh/known_hosts -o BatchMode=yes lx@192.168.5.55"
tar -cf /tmp/final.tar Assets/Scripts Assets/UI/Resources
$SSH "tar -C D:/banxia_build -xf -" < /tmp/final.tar && echo SYNC_OK; rm -f /tmp/final.tar
$SSH "powershell -NoProfile -Command \"Get-ChildItem D:/banxia_build/Assets/Scripts -Recurse -Filter *.cs | ForEach-Object { \$_.LastWriteTime = Get-Date }; Get-ChildItem D:/banxia_build/Assets/UI/Resources -Recurse | ForEach-Object { \$_.LastWriteTime = Get-Date }\""
$SSH "cmd /c del D:\banxia_build\Builds\Banxia-Phone.apk"
$SSH "powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/lx/banxia_build_phone_wait.ps1"
# sleep 570 → dir 时间戳 = 成功判据
```

### 步骤 2 · 逐里程碑 commit（独立可回滚）
```
git add <该里程碑文件> && git commit -m "fix(phone): M1 闭式构图+QA叠加层"   # M1 单独
git commit -m "fix(phone): M2 弹层状态机"                                    # M2
git commit -m "fix(phone): M3 键盘派生重排+圆角体检"                          # M3
docs/plans/*.md 随最后一批提交（chore(docs): 修复方案文档集）
git push origin main
```
（M1–M3 若同批验证，commit 仍拆三个；push 失败走既有 /tmp/push_retry.sh 后台重试。）

### 步骤 3 · release 资产更新（版本 0.3.2 不变）
```
export PATH=/data/dsh/home/dsh/bin:$PATH
cp <APK> /data/dsh/home/dsh/tmp-stage/Banxia-0.3.2-Phone.apk
gh release delete-asset v0.3.2 Banxia-0.3.2-Phone.apk --yes
gh release upload v0.3.2 /data/dsh/home/dsh/tmp-stage/Banxia-0.3.2-Phone.apk --clobber
gh release view v0.3.2 --json assets -q '.assets[].name'   # 确认两个资产齐全
```

### 步骤 4 · NAS 发布
```
bash /data/dsh/home/dsh/bin/banxia_publish_nas.sh /data/dsh/home/dsh/tmp-stage/Banxia-Phone.apk
# 校验 uploaded ok + md5 一致
```

### 步骤 5 · 双端同步登记（CLAUDE.md 义务）
在 `PHONE_PORT_PLAN_CN.md` 更新「待同步」：
- Quest 端三模式 UI/构图同步（复用 `CallFramingSolver`，闭式解平台无关）
- Quest 等价弹层层级规则（菜单面板所有权规则统一）
- 配对 numpad 手机壳层理由备注（不同步）

### 步骤 6 · 用户真机终验请求
请用户装 NAS 新包后复拍三场景并发回 `\\192.168.5.88\download-WD40EZRZ\文件传输\`：
1. 视频通话（开 HUD 构图网格）
2. 弹层打开态（模式 pill 点开）
3. 对话页配对卡（未连接态）
本机跑 `tools/qa/assert-framing.py / assert-layer.py / assert-shape.py / assert-align.py` 全绿 = 项目闭环。

### 步骤 7 · 资源释放（CLAUDE.md 钦定）
```
模拟器：am force-stop com.lingxi.banxia.phone && pkill -f qemu-system
构建机：告知用户 5.55 可以关机
本机：rm /tmp/*.tar /tmp/*.b64 等临时文件
```

---

## 2. 终局判定表

| 判定 | 标准 |
|------|------|
| 交付完成 | release+NAS 更新 + 用户三场景复拍断言全绿 |
| 遗留登记 | INV-6 真机复验（腿抽搐修复）、HOME 丢 chrome、Quest 同步项 → 下轮 |
| 不可宣称 | 任何「已修复」在断言全绿之前 |

## 3. 回滚策略

release 资产可 `gh release upload --clobber` 换回旧包；代码按里程碑 commit 逐个 revert；NAS 只留最新一版（脚本自动）。
