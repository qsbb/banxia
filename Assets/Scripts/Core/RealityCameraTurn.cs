namespace QuestMmdPlayer
{
    /// <summary>
    /// 摄像头单帧（手机独占能力）的平台无关业务层：用途模板、失败如实回执、
    /// 隐私红线文案。Quest 端无随身相机，不产生单帧；本类保持平台无关，
    /// 便于未来共享端（PC 摄像头等）复用同一套治理语义。
    /// </summary>
    public static class RealityCameraTurn
    {
        /// <summary>默认用途：用户未输入文字时随帧上送的请求说明。</summary>
        public const string DefaultPurpose = "看看我今天的穿搭和周围环境";

        /// <summary>
        /// 拍摄失败时随本轮文本上送模型的如实回执指令。复刻
        /// reality_companion 的 must_not_claim_observed + final_response_instruction
        /// 治理设计：角色不得编造画面内容，必须如实说明失败。
        /// </summary>
        public static string ComposeFailureReceipt(string failureReason)
        {
            var reason = string.IsNullOrWhiteSpace(failureReason) ? "未知原因" : failureReason.Trim();
            return "[系统回执：摄像头单帧拍摄失败（" + reason + "）。回复时你必须如实向用户说明本次拍摄失败及其原因；"
                + "不得声称画面黑、被遮挡、看到了任何人物或物品；不得猜测用户的当前状态、位置或穿着。]";
        }

        /// <summary>用户输入为空时的成功帧占位文本（与帧一起上送）。</summary>
        public static string ComposeFrameText(string userInput)
        {
            var text = string.IsNullOrWhiteSpace(userInput) ? string.Empty : userInput.Trim();
            return string.IsNullOrEmpty(text) ? DefaultPurpose : text;
        }
    }
}
