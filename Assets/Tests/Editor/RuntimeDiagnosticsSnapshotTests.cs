using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class RuntimeDiagnosticsSnapshotTests
    {
        [Test]
        public void MissingRuntimeProducesCompleteUnavailableSnapshot()
        {
            var snapshot = RuntimeDiagnosticsBuilder.Capture(null);

            Assert.That(snapshot.SchemaVersion, Is.EqualTo(RuntimeDiagnosticsSnapshot.CurrentSchemaVersion));
            Assert.That(snapshot.Menu.Available, Is.False);
            Assert.That(snapshot.Menu.ActiveLayer, Is.EqualTo(RuntimeMenuLayer.Unavailable));
            Assert.That(snapshot.Interaction.HandVisualizerAvailable, Is.False);
            Assert.That(snapshot.Voice.Available, Is.False);
            Assert.That(snapshot.Conversation.Available, Is.False);
            Assert.That(snapshot.Backend.Available, Is.False);
            Assert.That(snapshot.Backend.ChainStatus, Is.EqualTo("unavailable"));
            Assert.That(snapshot.Audio.Available, Is.False);
            Assert.That(snapshot.Passthrough.Available, Is.False);
            Assert.That(snapshot.Placement.Available, Is.False);
            Assert.That(snapshot.Room.Available, Is.False);
            Assert.That(snapshot.Motion.AvatarAvailable, Is.False);
            Assert.That(snapshot.Conversation.FirstEventMs, Is.EqualTo(-1));
        }

        [Test]
        public void MenuLayerDetectionPrioritizesModalChildren()
        {
            var rootObject = new GameObject("Diagnostics Menu Root");
            try
            {
                CreateLayer(rootObject.transform, "Main Menu Layer", false);
                var action = CreateLayer(rootObject.transform, "Action Presets Layer", true);
                var actionList = CreateLayer(action.transform, "Added Actions List", true);

                Assert.That(
                    RuntimeDiagnosticsBuilder.DetectMenuLayer(rootObject.transform, true),
                    Is.EqualTo(RuntimeMenuLayer.ActionList));

                actionList.SetActive(false);
                Assert.That(
                    RuntimeDiagnosticsBuilder.DetectMenuLayer(rootObject.transform, true),
                    Is.EqualTo(RuntimeMenuLayer.Actions));
                Assert.That(
                    RuntimeDiagnosticsBuilder.DetectMenuLayer(rootObject.transform, false),
                    Is.EqualTo(RuntimeMenuLayer.Closed));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(rootObject);
            }
        }

        [Test]
        public void TimingParserProjectsOnlyWhitelistedNumericMetrics()
        {
            var timing = RuntimeDiagnosticsBuilder.ParseConversationTiming(
                "firstChunk=80ms inputEnd=920ms firstEvent=4300ms firstText=4310ms " +
                "firstAudio=7900ms replyEnd=7980ms audioDone=8220ms chunks=11 " +
                "token=https://private.example/secret");

            Assert.That(timing.FirstInputChunkMs, Is.EqualTo(80));
            Assert.That(timing.InputEndMs, Is.EqualTo(920));
            Assert.That(timing.FirstEventMs, Is.EqualTo(4300));
            Assert.That(timing.FirstTextMs, Is.EqualTo(4310));
            Assert.That(timing.FirstAudioMs, Is.EqualTo(7900));
            Assert.That(timing.ReplyEndMs, Is.EqualTo(7980));
            Assert.That(timing.AudioDoneMs, Is.EqualTo(8220));
            Assert.That(timing.ReplyAudioChunkCount, Is.EqualTo(11));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("no active timing")]
        [TestCase("firstEvent=-ms chunks=not-a-number")]
        public void TimingParserFailsClosed(string value)
        {
            var timing = RuntimeDiagnosticsBuilder.ParseConversationTiming(value);

            Assert.That(timing.FirstInputChunkMs, Is.EqualTo(-1));
            Assert.That(timing.FirstEventMs, Is.EqualTo(-1));
            Assert.That(timing.FirstAudioMs, Is.EqualTo(-1));
            Assert.That(timing.ReplyAudioChunkCount, Is.Zero);
        }

        [Test]
        public void SnapshotSectionsCannotExposeUnboundedRuntimeStrings()
        {
            var sectionTypes = typeof(RuntimeDiagnosticsSnapshot)
                .GetProperties()
                .Select(property => property.PropertyType)
                .Where(type => type.Namespace == typeof(RuntimeDiagnosticsSnapshot).Namespace)
                .Concat(new[] { typeof(RuntimeDiagnosticsSnapshot) })
                .Distinct()
                .ToArray();

            var stringProperties = sectionTypes
                .SelectMany(type => type.GetProperties())
                .Where(property => property.PropertyType == typeof(string))
                .Select(property => property.DeclaringType.Name + "." + property.Name)
                .ToArray();

            Assert.That(
                stringProperties,
                Is.EquivalentTo(new[] { "RuntimeDiagnosticsSnapshot.SchemaVersion" }),
                "Runtime values must remain enums/numbers/booleans so URLs, IDs, keys, file names, and text cannot leak.");
        }

        private static GameObject CreateLayer(Transform parent, string name, bool active)
        {
            var layer = new GameObject(name);
            layer.transform.SetParent(parent, false);
            layer.SetActive(active);
            return layer;
        }
    }
}
