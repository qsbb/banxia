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
            Assert.That(snapshot.Backend.ChainStatus, Is.EqualTo(BackendChainState.Unavailable));
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
        public void MenuLayerDetectionRecognizesInstalledModelModal()
        {
            var rootObject = new GameObject("Diagnostics Model Menu Root");
            try
            {
                var models = CreateLayer(rootObject.transform, "Model Library Layer", true);
                CreateLayer(models.transform, "Installed Model List", true);

                Assert.That(
                    RuntimeDiagnosticsBuilder.DetectMenuLayer(rootObject.transform, true),
                    Is.EqualTo(RuntimeMenuLayer.ModelList));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(rootObject);
            }
        }

        [Test]
        public void MenuLayerDetectionRecognizesDevicePerformancePanel()
        {
            var rootObject = new GameObject("Diagnostics Performance Menu Root");
            try
            {
                CreateLayer(rootObject.transform, "Device Performance Layer", true);

                Assert.That(
                    RuntimeDiagnosticsBuilder.DetectMenuLayer(rootObject.transform, true),
                    Is.EqualTo(RuntimeMenuLayer.Performance));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(rootObject);
            }
        }

        [Test]
        public void PerformanceMonitorCalculatesFramePercentilesAndIgnoresInvalidSamples()
        {
            RuntimePerformanceMonitor.CalculateFrameStatistics(
                new[] { 10f, 20f, 30f, 40f, 0f, float.NaN },
                6,
                out var average,
                out var p50,
                out var p95,
                out var maximum);

            Assert.That(average, Is.EqualTo(25f).Within(.001f));
            Assert.That(p50, Is.EqualTo(25f).Within(.001f));
            Assert.That(p95, Is.EqualTo(38.5f).Within(.001f));
            Assert.That(maximum, Is.EqualTo(40f).Within(.001f));
            Assert.That(RuntimePerformanceMonitor.EstimateRgbaTextureBytes(2048, 1024), Is.EqualTo(8L * 1024 * 1024));
            Assert.That(RuntimePerformanceMonitor.MapAndroidThermalStatus(99), Is.EqualTo(DeviceThermalState.Unknown));
        }

        [Test]
        public void PerformanceMonitorKeepsRollingFrameSummaryBounded()
        {
            var rootObject = new GameObject("Performance Monitor Test");
            try
            {
                var monitor = rootObject.AddComponent<RuntimePerformanceMonitor>();
                for (var index = 0; index < 300; index++)
                {
                    monitor.RecordFrameDurationMilliseconds(10f + (index % 4));
                }

                Assert.That(monitor.frameSampleCount, Is.EqualTo(240));
                Assert.That(monitor.currentFps, Is.GreaterThan(0f));
                Assert.That(monitor.frameTimeP95Ms, Is.GreaterThanOrEqualTo(monitor.frameTimeP50Ms));
                Assert.That(monitor.frameTimeMaxMs, Is.EqualTo(13f).Within(.001f));
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
        public void DiagnosticsFormatterBuildsBoundedPanelText()
        {
            var snapshot = RuntimeDiagnosticsBuilder.Capture(null);
            var timeline = "stage=voice status=processing trace=secret-token";
            var text = RuntimeDiagnosticsFormatter.BuildPanelText(snapshot, "模型加载：超时", timeline, 3);

            Assert.That(text, Does.Contain("状态：模型加载：超时"));
            Assert.That(text, Does.Contain("链路：不可用"));
            Assert.That(text, Does.Contain("手追：0只"));
            Assert.That(text, Does.Contain("空间：未扫描"));
            Assert.That(text, Does.Contain("动作：空闲"));
            Assert.That(text, Does.Contain("来源未知"));
            Assert.That(text, Does.Contain("最近阶段："));
            Assert.That(text, Does.Not.Contain("trace=secret-token"));
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
