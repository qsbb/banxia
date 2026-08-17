#if UNITY_EDITOR
using NUnit.Framework;

namespace QuestMmdPlayer.Tests
{
    public sealed class ConversationActionIntentTests
    {
        [TestCase("帮我跳个舞", "dance")]
        [TestCase("随便跳个舞", "dance")]
        [TestCase("请挥手", "wave")]
        [TestCase("鞠躬一下", "bow")]
        [TestCase("点点头", "nod")]
        [TestCase("换个舞蹈吧", "dance_next")]
        [TestCase("把右手抬起来", "raise_hand")]
        [TestCase("请抬起单腿", "raise_leg")]
        [TestCase("转半圈看看", "turn_half")]
        [TestCase("帮我跳个舞", "dance")]
        [TestCase("让她随便跳个舞", "dance")]
        [TestCase("让角色换个舞蹈", "dance_next")]
        public void DetectsExplicitActionRequest(string text, string expected)
        {
            Assert.That(ConversationActionIntent.TryDetect(text, out var action), Is.True);
            Assert.That(action, Is.EqualTo(expected));
        }

        [Test]
        public void DoesNotTriggerExplicitNegativeDanceRequest()
        {
            Assert.That(ConversationActionIntent.TryDetect("不要跳舞", out _), Is.False);
        }

        [Test]
        public void DoesNotTreatNormalConversationAsAnAction()
        {
            Assert.That(ConversationActionIntent.TryDetect("今天天气怎么样", out _), Is.False);
        }
        [TestCase("\u8bf7\u5750\u4e0b\u6765", "sit")]
        [TestCase("\u4f60\u53ef\u4ee5\u8eba\u5230\u5e8a\u4e0a", "lie_down")]
        public void DetectsExplicitRestingRequest(string text, string expected)
        {
            Assert.That(ConversationActionIntent.TryDetect(text, out var action), Is.True);
            Assert.That(action, Is.EqualTo(expected));
        }
    }
}
#endif
