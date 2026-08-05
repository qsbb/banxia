using NUnit.Framework;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class AvatarCommandTests
    {
        [Test]
        public void JsonCommandIsAcceptedByBridge()
        {
            var host = new GameObject("AstrBotBridgeTest");
            var bridge = host.AddComponent<AstrBotBridge>();
            AvatarCommand received = null;
            bridge.CommandReceived += command => received = command;

            var accepted = bridge.TryIngestCommandJson("{\"command\":\"play_motion\",\"motionId\":\"wave\"}");

            Assert.That(accepted, Is.True);
            Assert.That(received, Is.Not.Null);
            Assert.That(received.name, Is.EqualTo("play_motion"));
            Assert.That(received.motionId, Is.EqualTo("wave"));
            Object.DestroyImmediate(host);
        }

        [Test]
        public void InvalidJsonIsRejected()
        {
            var host = new GameObject("AstrBotBridgeTest");
            var bridge = host.AddComponent<AstrBotBridge>();

            Assert.That(bridge.TryIngestCommandJson("not-json"), Is.False);
            Object.DestroyImmediate(host);
        }
    }
}
