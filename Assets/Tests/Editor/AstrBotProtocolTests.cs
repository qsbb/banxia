#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;

namespace QuestMmdPlayer.Tests
{
    public sealed class AstrBotProtocolTests
    {
        [Test]
        public void SseParserHandlesSplitUtf8AndComments()
        {
            var parser = new SseEventStreamParser();
            var frames = new List<SseEventFrame>();
            parser.EventReceived += frames.Add;
            var source = ": connected\n\nevent: reply.text.delta\ndata: {\"text\":\"你好\"}\n\n";
            var bytes = Encoding.UTF8.GetBytes(source);

            for (var index = 0; index < bytes.Length; index++)
            {
                parser.Push(new[] { bytes[index] }, 1);
            }

            Assert.That(frames, Has.Count.EqualTo(1));
            Assert.That(frames[0].EventName, Is.EqualTo("reply.text.delta"));
            Assert.That(frames[0].Data, Does.Contain("你好"));
        }

        [Test]
        public void AvatarIntentIsMappedAndUnknownValuesAreSafe()
        {
            const string json = "{\"type\":\"avatar.intent\",\"protocol_version\":\"1.0\",\"session_id\":\"s1\",\"turn_id\":\"i:e9\",\"in_reply_to_event_id\":\"e9\",\"emotion\":\"angry\",\"gesture\":\"delete_avatar\",\"look_at\":\"camera_object\",\"intensity\":1.4,\"duration_ms\":45000}";

            var mapped = AstrBotProtocol.TryMapSseEvent(
                "s1", "avatar.intent", json, out var message, out var error);

            Assert.That(mapped, Is.True, error);
            Assert.That(message.Type, Is.EqualTo(ConversationEventType.AvatarIntent));
            Assert.That(message.TurnId, Is.EqualTo("i:e9"));
            Assert.That(message.InReplyToEventId, Is.EqualTo("e9"));
            Assert.That(message.Emotion, Is.EqualTo("neutral"));
            Assert.That(message.Gesture, Is.EqualTo("idle"));
            Assert.That(message.LookAt, Is.EqualTo("none"));
            Assert.That(message.Intensity, Is.EqualTo(1f));
            Assert.That(message.DurationMs, Is.EqualTo(30000));
        }

        [Test]
        public void Pcm16AudioIsDecodedLittleEndian()
        {
            const string json = "{\"type\":\"reply.audio.chunk\",\"protocol_version\":\"1.0\",\"session_id\":\"s1\",\"turn_id\":\"t1\",\"format\":\"pcm16\",\"sample_rate\":24000,\"channels\":1,\"data\":\"NBLM/w==\"}";

            var mapped = AstrBotProtocol.TryMapSseEvent(
                "s1", "reply.audio.chunk", json, out var message, out var error);

            Assert.That(mapped, Is.True, error);
            Assert.That(message.Pcm16, Is.EqualTo(new short[] { 0x1234, -52 }));
            Assert.That(message.SampleRate, Is.EqualTo(24000));
        }

        [Test]
        public void StaleSessionEventIsRejected()
        {
            const string json = "{\"type\":\"reply.end\",\"protocol_version\":\"1.0\",\"session_id\":\"old\",\"turn_id\":\"t1\"}";

            Assert.That(
                AstrBotProtocol.TryMapSseEvent("current", "reply.end", json, out _, out var error),
                Is.False);
            Assert.That(error, Does.Contain("stale session"));
        }

        [Test]
        public void RuntimePolicyAllowsOnlyExplicitPrivateLanHttp()
        {
            var settings = ValidSettings();
            settings.base_url = "http://192.168.1.10:6185/api/v1/plugins/extensions/astrbot_plugin_quest_avatar_bridge";

            Assert.That(AstrBotProtocol.TryValidateSettings(settings, out var error), Is.False);
            Assert.That(error, Does.Contain("allow_insecure_http"));

            settings.allow_insecure_http = true;
            Assert.That(AstrBotProtocol.TryValidateSettings(settings, out error), Is.True, error);

            settings.base_url = "http://api.example.com/api/v1/plugins/extensions/astrbot_plugin_quest_avatar_bridge";
            Assert.That(AstrBotProtocol.TryValidateSettings(settings, out error), Is.False);
            Assert.That(error, Does.Contain("private-network IP"));

            settings.base_url = "http://nas.local/api/v1/plugins/extensions/astrbot_plugin_quest_avatar_bridge";
            Assert.That(AstrBotProtocol.TryValidateSettings(settings, out error), Is.False);
        }

        private static AstrBotBridgeSettings ValidSettings()
        {
            return new AstrBotBridgeSettings
            {
                base_url = "https://astrbot.example.com/api/v1/plugins/extensions/astrbot_plugin_quest_avatar_bridge",
                astrbot_api_key = "astrbot-api-key",
                bridge_api_key = "0123456789abcdef0123456789abcdef",
                client_id = "quest3-living-room",
                user_id = "user-1",
                bot_id = "bot-main"
            };
        }
    }
}
#endif
