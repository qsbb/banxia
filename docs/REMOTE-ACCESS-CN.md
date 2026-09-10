# 伴夏远程接入（手机/Quest 外网连后端）架构与运维手册

> **敏感信息纪律**：DDNS 域名、凭据、真实端口映射明细不写入本文或任何入库文件；
> 域名以 `<DDNS域名>` 代指，实际值由部署者持有。

## 0. 现状定稿：可选 HTTP / HTTPS

伴夏与临桥支持用户选择传输方式：**HTTPS 是默认与推荐方案**；受控局域网可以
显式选择 HTTP；公网 HTTP 仅作为明确的高风险 opt-in。

- 裸地址或裸 `host:port` 按 `https://` 解释。
- 明文必须在地址中显式写 `http://`。
- HTTPS 失败不会自动降级到 HTTP。
- 私网 HTTP 与公网 HTTP 使用两个独立开关，默认均关闭。
- 证书、CA、主机名、TLS 握手或 pin 失败均为终止错误，不会借 endpoint failover
  绕过信任策略。

> ⚠️ **公网 HTTP 风险**：明文链路上的配对密钥、API key、Bridge key、聊天内容和
> 音频可能被中间节点读取或篡改。只有同时打开服务端
> `allow_remote_http_pairing` 与客户端“允许公网 HTTP（高风险明文）”，且地址明确
> 使用 `http://` 时，才会接受公网明文。

## 1. 推荐 HTTPS 架构

```text
手机/Quest ──https://<DDNS域名>:18443──> 路由器 TCP 映射 18443→Bridge:18443
                                          │
                                          ▼
              临桥内置 TLS listener → loopback HTTP → AstrBot 6185
```

HTTPS 可由临桥内置 listener 在任意合适的高端口终止 TLS，也可使用受信任的外部
HTTPS 反代。路由器只转发 TCP listener 端口；不要把 AstrBot Dashboard、NAS、
SSH、SMB、Docker socket 或其他管理端口暴露到公网。

自签证书场景应在伴夏端填写叶子证书 DER SHA-256 pin。pin 绑定精确的
`scheme + host + 有效端口`，不匹配时不会切换 authority 或降级到 HTTP。使用公共
CA 且不配置 pin 时，伴夏仍使用平台正常的证书链和主机名校验。

## 2. 服务端配置

### 2.1 内置 TLS listener 示例

```json
{
  "pairing_listener_enabled": true,
  "pairing_listener_host": "0.0.0.0",
  "pairing_listener_port": 18443,
  "pairing_listener_tls_enabled": true,
  "pairing_listener_tls_cert_path": "/data/certs/bridge.crt",
  "pairing_listener_tls_key_path": "/data/certs/bridge.key",
  "pairing_listener_public_url": "https://<DDNS域名>:18443",
  "pairing_public_url": "https://<DDNS域名>:18443",
  "allow_remote_http_pairing": false
}
```

启用 TLS 时必须提供可读、匹配的 PEM 证书与私钥。证书/私钥缺失、格式错误或配置
pin 与叶子证书不一致时，TLS listener fail-closed，不会在同一端口回退成 HTTP。
TLS 最低版本为 1.2。

若另有合法的外部 HTTPS `pairing_exchange_proxy_url`，非 TLS listener 绑定故障可使
已认证服务保持 degraded 并继续使用该 fallback；active TLS listener 故障不会回退
到未知 authority。

### 2.2 显式私网 HTTP

地址必须写 `http://`，并同时打开服务端私网 HTTP 配置和伴夏端“允许内网 HTTP
（仅私有地址）”。私网判断只接受明确的私网/loopback 地址范围，不以任意公网域名
冒充私网。

### 2.3 显式公网 HTTP

地址必须写 `http://`，并同时打开服务端 `allow_remote_http_pairing` 与伴夏端
“允许公网 HTTP（高风险明文）”。不要为 HTTP 配置证书 pin；pin 只对 HTTPS 生效。

## 3. 配对与证书指纹

现有 v1 配对协议继续兼容。6 位短码保持短 TTL 和服务端限速；QR 是可选快捷入口，
不会携带长期 API key。HTTPS 自签场景可由 QR、兑换响应或手动输入携带
`certificate_pin_sha256`。

指纹是**完整叶子证书 DER** 的 SHA-256，不是 PEM 文本、整条证书链或公钥摘要：

```bash
openssl x509 -in bridge.crt -outform DER | sha256sum
# macOS：
openssl x509 -in bridge.crt -outform DER | shasum -a 256
```

将输出的 64 位十六进制值原样填写；不要加 `sha256:` 前缀、冒号或空白。证书续期
通常会改变叶子指纹，必须在服务端和伴夏端同步更新。pin-only 不能单独替代域名/CA
校验和可信的首次指纹核验。

## 4. 客户端双端行为

Quest 原生设置与手机 Flutter 设置共享同一引擎状态：

- 分别显示和切换“内网 HTTP”与“公网 HTTP（高风险）”；
- 可填写、保存、清除 HTTPS 证书指纹，并仅展示脱敏摘要；
- QR/手动配对严格拒绝带空白、非 64 位或非十六进制 pin；
- endpoint 改变、解除绑定、配置清空或终止 TLS/pin 错误会清除不再适用的 pin；
- 所有带凭据请求拒绝重定向；
- HTTPS 不自动降级，TLS/CA/主机名/pin 错误不触发 failover。

入口优先级列表只会在普通网络不可达（`ConnectionError`）时冷却当前入口并顺延；
401、4xx/5xx、TLS 和信任错误均不转移，以免掩盖配置错误或形成降级路径。

## 5. 日常运维

- 检查 listener 状态和对外 HTTPS URL 是否一致。
- 证书轮换后重新计算叶子 DER SHA-256，并同步更新 pin。
- 手机/Quest 换网络或入口时，在连接设置中更新 endpoint；HTTPS 权威发生变化时重新
  核验并设置 pin。
- 重新配对时生成新的 6 位短码；旧的一次性 token 不可复用。
- 解除绑定会清除绑定、pin、配对码和 endpoint；HTTP 偏好开关保留，它们不是凭据。
- 需要公网 HTTP 时先确认风险，再显式写 `http://` 并打开服务端和客户端两个公网
  HTTP 闸门；回到 HTTPS 后关闭公网 HTTP 闸门。
- 上游固定为 loopback HTTP；公网只转发 TLS listener 或明确选择的 HTTP listener
  端口，不暴露任何管理面。

## 6. IPv6 与 NAT 回流

若主机监听 `[::]` 且网络分配公网 IPv6，listener 可能绕过 IPv4 端口映射直接可达；
应在路由器/主机 IPv6 防火墙中保持默认拒绝，只放行计划中的 listener 端口。

`pairing_public_url` 是服务端下发的公开入口。路由器不支持 NAT 回流时，内网客户端
可能无法访问公网域名；可为客户端配置经过独立核验的内网 HTTPS endpoint。配置了
pin 时，候选 endpoint 必须与 pin 的精确 authority 一致，不能把同一 pin 复用于
不同 host 或端口。
