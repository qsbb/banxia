#if UNITY_EDITOR
using NUnit.Framework;

namespace QuestMmdPlayer.Tests
{
    public sealed class FlutterMessageProtocolTests
    {
        private const string PayloadJson = "{\"path\":\"/MmdModels/kokona\"}";

        [Test]
        public void CommandEnvelopeSerializesVersionAndType()
        {
            var json = FlutterMessageProtocol.Serialize(
                FlutterMessageProtocol.Command(12, FlutterCommands.ModelLoad, PayloadJson));

            Assert.That(json, Does.Contain("\"v\":1"));
            Assert.That(json, Does.Contain("\"type\":\"cmd\""));
            Assert.That(json, Does.Contain("\"name\":\"model.load\""));
        }

        [Test]
        public void RoundTripsCommandEnvelope()
        {
            var envelope = FlutterMessageProtocol.Command(12, FlutterCommands.ModelLoad, PayloadJson);
            var json = FlutterMessageProtocol.Serialize(envelope);

            Assert.That(FlutterMessageProtocol.TryParse(json, out var parsed, out var error), Is.True, error);

            Assert.That(parsed.v, Is.EqualTo(1));
            Assert.That(parsed.id, Is.EqualTo(12));
            Assert.That(parsed.IsCommand, Is.True);
            Assert.That(parsed.IsReply, Is.False);
            Assert.That(parsed.IsEvent, Is.False);
            Assert.That(parsed.name, Is.EqualTo(FlutterCommands.ModelLoad));
            Assert.That(parsed.payload, Is.EqualTo(PayloadJson));
        }

        [Test]
        public void RoundTripsEventEnvelope()
        {
            var envelope = FlutterMessageProtocol.Event(FlutterEvents.Toast, "{\"message\":\"hi\"}");
            var json = FlutterMessageProtocol.Serialize(envelope);

            Assert.That(FlutterMessageProtocol.TryParse(json, out var parsed, out var error), Is.True, error);

            Assert.That(parsed.IsEvent, Is.True);
            Assert.That(parsed.name, Is.EqualTo(FlutterEvents.Toast));
            Assert.That(parsed.id, Is.EqualTo(0));
        }

        [Test]
        public void ReplyIsOkExactlyWhenErrorIsEmpty()
        {
            var ok = FlutterMessageProtocol.Reply(7, FlutterCommands.ModelDiscover, "[]", string.Empty);
            var failed = FlutterMessageProtocol.Reply(7, FlutterCommands.ModelDiscover, null, "boom");

            Assert.That(ok.IsOk, Is.True);
            Assert.That(failed.IsOk, Is.False);
        }

        [Test]
        public void RoundTripsErrorReply()
        {
            var envelope = FlutterMessageProtocol.Reply(7, FlutterCommands.ModelLoad, null, "unknown command");
            var json = FlutterMessageProtocol.Serialize(envelope);

            Assert.That(FlutterMessageProtocol.TryParse(json, out var parsed, out var error), Is.True, error);
            Assert.That(parsed.IsReply, Is.True);
            Assert.That(parsed.id, Is.EqualTo(7));
            Assert.That(parsed.error, Is.EqualTo("unknown command"));
            Assert.That(parsed.IsOk, Is.False);
        }

        [Test]
        public void RejectsEmptyJson()
        {
            Assert.That(FlutterMessageProtocol.TryParse("", out _, out var error), Is.False);
            Assert.That(error, Does.Contain("empty"));
            Assert.That(FlutterMessageProtocol.TryParse(null, out _, out error), Is.False);
            Assert.That(error, Does.Contain("empty"));
        }

        [Test]
        public void RejectsMalformedJson()
        {
            Assert.That(FlutterMessageProtocol.TryParse("{not json", out _, out var error), Is.False);
            Assert.That(error, Does.Contain("malformed"));
        }

        [Test]
        public void RejectsUnsupportedVersion()
        {
            var envelope = FlutterMessageProtocol.Command(1, FlutterCommands.ModelDiscover, null);
            envelope.v = 2;
            var json = FlutterMessageProtocol.Serialize(envelope);

            Assert.That(FlutterMessageProtocol.TryParse(json, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("version"));
        }

        [Test]
        public void RejectsUnknownType()
        {
            var envelope = FlutterMessageProtocol.Command(1, FlutterCommands.ModelDiscover, null);
            envelope.type = "wat";
            var json = FlutterMessageProtocol.Serialize(envelope);

            Assert.That(FlutterMessageProtocol.TryParse(json, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("type"));
        }

        [Test]
        public void RejectsMissingName()
        {
            var envelope = FlutterMessageProtocol.Command(1, string.Empty, null);
            var json = FlutterMessageProtocol.Serialize(envelope);

            Assert.That(FlutterMessageProtocol.TryParse(json, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("name"));
        }

        [Test]
        public void RejectsNegativeId()
        {
            var envelope = FlutterMessageProtocol.Command(-5L, FlutterCommands.ModelDiscover, null);
            var json = FlutterMessageProtocol.Serialize(envelope);

            Assert.That(FlutterMessageProtocol.TryParse(json, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("non-negative"));
        }

        [Test]
        public void AcceptsUnknownFutureCommandName()
        {
            var envelope = FlutterMessageProtocol.Command(1, "future.command", null);
            var json = FlutterMessageProtocol.Serialize(envelope);

            Assert.That(FlutterMessageProtocol.TryParse(json, out var parsed, out var error), Is.True, error);
            Assert.That(FlutterMessageProtocol.IsRecognizedCommand("future.command"), Is.False);
            Assert.That(parsed.name, Is.EqualTo("future.command"));
        }

        [Test]
        public void SerializesPayloadAsEmbeddedString()
        {
            var json = FlutterMessageProtocol.Serialize(
                FlutterMessageProtocol.Command(1, FlutterCommands.ModelLoad, PayloadJson));

            // The payload object is embedded as an escaped JSON string, the
            // JsonUtility/IL2CPP-compatible representation documented in the
            // protocol header.
            Assert.That(json, Does.Contain("\\\"path\\\":\\\"/MmdModels/kokona\\\""));
        }

        [Test]
        public void TypedPayloadRoundTripsThroughJsonUtility()
        {
            var payload = new FlutterModelInfoDto
            {
                displayName = "Kokona",
                path = "/MmdModels/kokona",
                packageRoot = "root"
            };

            Assert.That(FlutterMessageProtocol.TrySerializePayload(payload, out var json, out var serializeError), Is.True, serializeError);
            Assert.That(FlutterMessageProtocol.TryDeserializePayload<FlutterModelInfoDto>(json, out var value, out var deserializeError), Is.True, deserializeError);

            Assert.That(value.displayName, Is.EqualTo("Kokona"));
            Assert.That(value.path, Is.EqualTo("/MmdModels/kokona"));
            Assert.That(value.packageRoot, Is.EqualTo("root"));
        }

        [Test]
        public void EmptyPayloadDeserializesToDefaultWithoutError()
        {
            Assert.That(FlutterMessageProtocol.TryDeserializePayload<FlutterModelInfoDto>(null, out var value, out var error), Is.True);
            Assert.That(error, Is.Null);
            Assert.That(value, Is.Null);
        }

        [Test]
        public void RecognizedCommandAndEventSetsAreConsistent()
        {
            Assert.That(FlutterMessageProtocol.IsRecognizedCommand(FlutterCommands.ModelLoad), Is.True);
            Assert.That(FlutterMessageProtocol.IsRecognizedCommand(FlutterCommands.QaCommand), Is.True);
            Assert.That(FlutterMessageProtocol.IsRecognizedCommand("nope"), Is.False);
            Assert.That(FlutterMessageProtocol.IsRecognizedCommand(string.Empty), Is.False);

            Assert.That(FlutterMessageProtocol.IsRecognizedEvent(FlutterEvents.Toast), Is.True);
            Assert.That(FlutterMessageProtocol.IsRecognizedEvent(FlutterEvents.PerformanceSnapshot), Is.True);
            Assert.That(FlutterMessageProtocol.IsRecognizedEvent("nope"), Is.False);
        }

        [Test]
        public void EnvelopeFieldOrderMatchesSchema()
        {
            var json = FlutterMessageProtocol.Serialize(
                FlutterMessageProtocol.Command(3, FlutterCommands.ModelDiscover, null));

            // v, id, type, name must come before payload/error so hand-written
            // QA assertions can locate the header deterministically.
            Assert.That(json.IndexOf("\"v\":1", System.StringComparison.Ordinal), Is.LessThan(json.IndexOf("\"id\":3", System.StringComparison.Ordinal)));
            Assert.That(json.IndexOf("\"id\":3", System.StringComparison.Ordinal), Is.LessThan(json.IndexOf("\"type\":\"cmd\"", System.StringComparison.Ordinal)));
        }
    }
}
#endif
