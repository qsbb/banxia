# 伴夏远程接入（手机外网连后端）架构与运维手册

> 2026-09-07 落地。**敏感信息纪律**：DDNS 域名、阿里云凭据、端口映射明细
> 不写入本文与任何入库文件；域名以 `<DDNS域名>` 代指，实际值由用户持有并
> 仅存于 NAS 运行时配置（`/vol1/@appdata/remote-bridge/`，NAS 本地）。

## 1. 架构

```
手机(蜂窝/任意网络)
   │  https://<DDNS域名>:8443   （Let's Encrypt RSA 2048，系统 CA 信任）
   ▼
路由器：WAN 8443 → NAS 192.168.5.88:8443 （v4 端口映射；v6 不用）
   ▼
NAS nginx 容器 remote-bridge-nginx（8443，仅 v4 绑定 0.0.0.0）
   │  · TLS 终止：仅 TLSv1.2/1.3，ECDHE 现代套件，session tickets 关闭
   │  · 只放行两条路径：pairing/exchange（严限速 10r/m）与插件 API 前缀（30r/s）
   │  · 其余一律 404；429 统一带 Retry-After
   │  · SSE：proxy_buffering off、Connection ''、read_timeout 3600s
   ▼  http://172.17.0.1:8520（docker0 网关，仅 NAS 内部）
临桥内置监听器（astrbot 容器内，8520）→ AstrBot 6185
```

内网路径不变：Quest/手机在家走 `http://192.168.5.88:8520`（客户端按端点
优先级列表自动首选内网入口，见 §5）。

### 为什么必须有 TLS 反代（根因记录）

外网连不上不是服务故障，是三层设计性阻塞：①服务端 `pairing_public_url`
下发的是内网地址；②客户端 `AstrBotProtocol.TryValidateSettings` 对非私网
主机强制 HTTPS（防 DNS rebinding，域名不走明文）；③服务端
`allow_insecure_remote_http=false` 拒绝公网明文配对（422 https_required）。
缺失的唯一组件 = 客户端信任的 HTTPS 入口，本方案补上它，不绕开任何闸门。

## 2. 组件与凭据

| 组件 | 位置 | 说明 |
|---|---|---|
| remote-bridge-nginx | NAS docker，镜像 `nginx:alpine` | TLS 反代，配置 `/vol1/@appdata/remote-bridge/conf/nginx.conf` |
| remote-bridge-acme | NAS docker，镜像 `neilpang/acme.sh` | DNS-01（阿里云）签发/续期证书，daemon 常驻；凭据存于其数据卷 `account.conf` |
| 证书 | `/vol1/@appdata/remote-bridge/certs/{cert.pem,key.pem}` | RSA 2048（兼容 Android 7.1+ 系统 CA），90 天期，自动续期 |
| 运维脚本 | `/vol1/@appdata/remote-bridge/remote-bridge.sh` | issue/start/stop/status/renew/reload/logs |

阿里云 RAM 子账号 key（仅 AliyunDNSFullAccess，建议锁单域名）是**唯一外部
凭据**，仅存在于 acme.sh 容器环境/数据卷，不进任何 git 仓库。

## 3. 运维操作

### 首次部署（已执行过一次，重建时参考）
```sh
# 域名与 key 仅注入命令环境，不落脚本
BX_DOMAIN=<DDNS域名> ALI_KEY=<RAMKey> ALI_SECRET=<RAMSecret> \
  sh /vol1/@appdata/remote-bridge/remote-bridge.sh issue
sh /vol1/@appdata/remote-bridge/remote-bridge.sh start
```

### 日常
- 状态：`sh remote-bridge.sh status`（应见两容器 Up + 本地 TLS 自检 401/404）
- 手动续期：`sh remote-bridge.sh renew`（acme.sh daemon 本会自动续）
- 重载证书/配置：`sh remote-bridge.sh reload`
- 日志：`sh remote-bridge.sh logs`

### 吊销/轮换阿里云 key（修正 #1 定稿流程）
1. 阿里云 RAM 控制台禁用/删除旧 key；新建同权限 key
2. NAS 上编辑 `/vol1/@appdata/remote-bridge/acme/account.conf`，
   替换 `SAVED_Ali_Key` / `SAVED_Ali_Secret` 两行的值
3. `sh remote-bridge.sh renew` 验证续期链路正常

### 回滚（全程可逆）
1. `sh remote-bridge.sh stop`（再 `docker rm remote-bridge-nginx remote-bridge-acme` 可彻底删除）
2. 路由器删除 `8443→192.168.5.88:8443` 映射
3. 临桥配置 `pairing_listener_public_url` / `pairing_public_url` 改回
   `http://192.168.5.88:8520`（AstrBot 控制台 → 插件配置）
4. 手机/Quest 已绑定配置不受影响（内网入口仍在端点列表中）

## 4. 安全属性（对照安全基线逐条）

- **公网面**：唯一入口 8443/TLS；只暴露 pairing/exchange + 桥接会话 API 前缀；
  dashboard 管理端点（/pairing/create 等）公网 404。8520 不再经路由器映射
  暴露（原 8520→8520 映射已改指 8443）
- **鉴权**：配对 6 位码 + 会话双头（Authorization: ApiKey + X-Embodiment-Bridge-Key）
  全部保留，反代不终结鉴权，只终结 TLS
- **限速双保险**：nginx（10r/m 严 / 30r/s 宽）+ 插件（12 次/分/IP、120 次/分全局），
  均带 Retry-After；实测错误码连打 16 次 → 12×401 后 429（2026-09-07）
- **防枚举**：配对错误统一 401，无"存在与否"差异；非白名单路径统一 404
- **凭据卫生**：key 只在 NAS 容器环境/数据卷；客户端配置存设备私有目录；
  不入 git/日志/截屏
- **TLS 参数**：TLSv1.2/1.3 only、ECDHE 套件白名单、session tickets off、
  HSTS 7 天、server_tokens off、Host 头守卫（非本域名直接 444）

### IPv6 残余面（已知、记录在案）

家宽有公网 IPv6，NAS 容器端口（含 8520）绑 `[::]`，理论上不经 v4 映射直接
可达——取决于路由器 IPv6 防火墙。**本期 v4-only 方案不依赖也不新增 v6 路径**。
建议（用户择机核实）：路由器 IPv6 防火墙保持"默认拒绝入站"；如需彻底收敛，
可将 astrbot 容器 8520 发布改绑 192.168.5.88（需重建容器，未动）。

## 5. 客户端：端点优先级列表（双端）

- 设置 → 连接后端 → 入口优先级列表：有序候选，排最上的优先；配对下发的
  绑定地址自动置顶；可手动添加内网/公网/兜底（如穿透组网地址）入口
- 故障转移：仅网络层不可达（ConnectionError）才冷却当前入口 120s 并顺延；
  401/4xx/5xx 不转移（防掩盖配置错误、防降级攻击）；切网后连接循环自然重选
- 同一套配对凭据对所有入口通用，换入口无需重新配对
- 重新配对时保留用户维护的候选列表，新配对地址置顶
- **公网地址必须输入完整 `https://` 前缀**（引擎对裸输入默认补 http://，
  公网 HTTP 会被客户端与服务端双重拒绝——这是设计，不是故障）
- Quest 端：面板显示生效入口与候选数、自动故障转移同在（共享引擎层）；
  列表管理 UI 暂以手机端为准（登记 PHONE_PORT_PLAN_CN.md 待同步）

## 6. NAT 回流说明

`pairing_public_url` 是全局下发值（所有客户端共享）。若路由器不支持 NAT
回流，内网设备重新配对后会拿到公网域名地址而内网访问不通——端点优先级列表
已根治此场景（内网入口作为候选自动兜底）。Quest 存量绑定不受任何影响。
