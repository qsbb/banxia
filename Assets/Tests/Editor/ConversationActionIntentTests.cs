#if UNITY_EDITOR
using NUnit.Framework;

namespace QuestMmdPlayer.Tests
{
    public sealed class ConversationActionIntentTests
    {
        [TestCase("帮我跳个舞", "dance")]
        [TestCase("请挥手", "wave")]
        [TestCase("鞠躬一下", "bow")]
        [TestCase("点点头", "nod")]
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
    }
}
#endif
