using System;

namespace QuestMmdPlayer
{
    /// <summary>
    /// 双端共享的运行状态中文本地化（配对 / AstrBot 桥接 / 链路）。
    /// 提取自 CompanionWorldMenu 的映射表，供 UI Toolkit 新壳与旧世界菜单共用。
    /// </summary>
    public static class BanxiaUiText
    {
        public static string LocalizePairingStatus(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "配对状态不可用";
            switch (value)
            {
                case "Enter pairing server and 6-digit code": return "请输入配对服务器和 6 位配对码";
                case "Pairing server ready": return "配对服务器已就绪";
                case "Enter all 6 pairing digits": return "请输入完整的 6 位配对码";
                case "Set the HTTPS pairing server first": return "请先设置 HTTPS 配对服务器";
                case "Set the pairing server first": return "请先设置配对服务器";
                case "Private-LAN HTTP enabled for this pairing session": return "已临时允许私网 IP 的 HTTP 配对";
                case "HTTPS pairing required": return "已恢复为仅允许 HTTPS 配对";
                case "Enable private-LAN HTTP before using a private IP address": return "检测到私网 IP，请先开启局域网 HTTP";
                case "Pairing request is already running": return "配对请求正在进行";
                case "QR scanner is unavailable": return "二维码扫描暂不可用";
                case "Exchanging one-time pairing credential...": return "正在交换一次性配对凭据……";
                case "Pairing response is invalid or incompatible": return "配对响应无效或版本不兼容";
                case "Configuration saved, but AstrBot reconnect could not start": return "配置已保存，但 AstrBot 无法开始重连";
                case "Backend paired; AstrBot is connecting": return "后端绑定成功，AstrBot 正在连接";
                case "Pairing controller offline": return "配对控制器离线";
                case "Meta OpenXR 1.0.2 cannot expose passthrough camera frames; use the 6-digit code.": return "当前 SDK 无法读取相机画面，请使用 6 位配对码";
            }
            if (value.StartsWith("Pairing exchange failed (HTTP ", StringComparison.Ordinal))
                return "配对失败：" + value.Substring("Pairing exchange failed ".Length);
            return value;
        }

        public static string LocalizeBridgeStatus(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "状态未知";
            if (value.StartsWith("AstrBot config missing:", StringComparison.Ordinal)) return "尚未绑定";
            if (value.StartsWith("AstrBot config invalid:", StringComparison.Ordinal)) return "绑定配置无效";
            if (value.IndexOf("bridge_service_disabled", StringComparison.Ordinal) >= 0) return "后端服务已关闭";
            if (value.StartsWith("Health check failed (HTTP 0)", StringComparison.Ordinal)) return "无法连接服务器";
            if (value.StartsWith("Health check failed (HTTP 401)", StringComparison.Ordinal)) return "认证失败";
            if (value.StartsWith("Health check failed", StringComparison.Ordinal)) return "健康检查失败";
            if (value.StartsWith("Connection failed", StringComparison.Ordinal)) return "无法连接服务器";
            const string readyPrefix = "AstrBot session ready (";
            if (value.StartsWith(readyPrefix, StringComparison.Ordinal) &&
                value.EndsWith(")", StringComparison.Ordinal))
            {
                var chain = value.Substring(readyPrefix.Length, value.Length - readyPrefix.Length - 1);
                return "会话已建立 · " + LocalizeBackendChainStatus(chain);
            }
            switch (value)
            {
                case "AstrBot configuration not loaded": return "尚未载入配置";
                case "AstrBot config loaded": return "配置已载入";
                case "AstrBot config could not be read": return "无法读取配置";
                case "Checking AstrBot health": return "正在检查服务";
                case "AstrBot health check ready": return "服务可用";
                case "AstrBot health response is incompatible": return "服务协议不兼容";
                case "Starting AstrBot session": return "正在建立会话";
                case "AstrBot session ready": return "会话已建立";
                case "Connecting AstrBot SSE": return "正在连接实时事件";
                case "AstrBot SSE connected": return "实时连接正常";
                case "AstrBot session expired; recreating": return "会话已过期，正在重建";
                case "AstrBot session closed": return "会话已关闭";
                case "AstrBot SSE disconnected": return "实时连接已断开";
            }
            return value;
        }

        public static string LocalizeBackendChainStatus(string value)
        {
            switch (value)
            {
                case "EventBus ready": return "AstrBot 链路正常";
                case "EventBus eligible": return "AstrBot 链路可用";
                case "direct provider fallback": return "直连模型兼容回退";
                case "owner_not_configured":
                case "quest_identity_not_allowlisted": return "原始账号尚未在“序”中绑定";
                case "invalid_bot_id":
                case "invalid_user_id":
                case "missing_bot_id":
                case "missing_user_id": return "配对中的用户或机器人身份无效";
                case "client_id_mismatch":
                case "invalid_client_id":
                case "missing_client_id":
                case "trusted_client_id_missing": return "Quest 客户端身份不匹配";
                case "missing_platform_id":
                case "trusted_platform_id_missing":
                case "trusted_platform_not_configured":
                case "trusted_platform_unavailable": return "AstrBot 消息平台未配置或不可用";
                case "authorization_timeout": return "身份授权检查超时";
                case "authorization_denied":
                case "authorization_error":
                case "protected_context_denied": return "AstrBot 链路未授权";
                default: return "链路状态未知";
            }
        }

        public static string LocalizeConversationState(ConversationState state)
        {
            switch (state)
            {
                case ConversationState.Idle: return "待命";
                case ConversationState.Listening: return "正在聆听";
                case ConversationState.Thinking: return "思考中";
                case ConversationState.Speaking: return "说话中";
                case ConversationState.Interrupted: return "已打断";
                case ConversationState.Error: return "出错";
                default: return state.ToString();
            }
        }
    }
}
