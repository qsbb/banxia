#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace QuestMmdPlayer.Tests
{
    public sealed class QuestDebugModeTests
    {
        private bool hadPreference;
        private int previousPreference;
        private bool previousEnabled;

        [SetUp]
        public void SetUp()
        {
            hadPreference = PlayerPrefs.HasKey(QuestDebugMode.PrefKey);
            previousPreference = PlayerPrefs.GetInt(QuestDebugMode.PrefKey);
            previousEnabled = QuestDebugMode.Enabled;
            QuestDebugMode.SetEnabled(false);
        }

        [TearDown]
        public void TearDown()
        {
            QuestDebugMode.SetEnabled(previousEnabled);
            if (hadPreference) PlayerPrefs.SetInt(QuestDebugMode.PrefKey, previousPreference);
            else PlayerPrefs.DeleteKey(QuestDebugMode.PrefKey);
            PlayerPrefs.Save();
        }

        [Test]
        public void SetEnabledPersistsPreferenceAndCache()
        {
            QuestDebugMode.SetEnabled(true);
            Assert.That(QuestDebugMode.Enabled, Is.True);
            Assert.That(PlayerPrefs.GetInt(QuestDebugMode.PrefKey, 0), Is.EqualTo(1));
            QuestDebugMode.SetEnabled(false);
            Assert.That(QuestDebugMode.Enabled, Is.False);
            Assert.That(PlayerPrefs.GetInt(QuestDebugMode.PrefKey, 0), Is.EqualTo(0));
        }

        [Test]
        public void ReportIsSilentWhenDisabledAndLogsWhenEnabled()
        {
            Assert.That(QuestDebugMode.Report(new Exception("boom"), "test"), Is.False);
            QuestDebugMode.SetEnabled(true);
            LogAssert.Expect(LogType.Error, new Regex("\\[DebugMode\\]\\[test\\].*boom"));
            Assert.That(QuestDebugMode.Report(new Exception("boom"), "test"), Is.True);
        }

        [Test]
        public void RethrowPreservesOriginalExceptionAndSkipsFallback()
        {
            var exception = new InvalidOperationException("boom");
            Assert.DoesNotThrow(() => QuestDebugMode.RethrowIfEnabled(exception, "test"));
            QuestDebugMode.SetEnabled(true);
            var fallbackRan = false;
            var caught = Assert.Throws<InvalidOperationException>(() =>
            {
                QuestDebugMode.RethrowIfEnabled(exception, "test");
                fallbackRan = true;
            });
            Assert.That(caught, Is.SameAs(exception));
            Assert.That(fallbackRan, Is.False);
        }

        [Test]
        public void AstrBotCommandSubscriberExceptionPreservesModeSemantics()
        {
            var bridgeObject = new GameObject("QuestDebugModeTests.AstrBotBridge");
            try
            {
                var bridge = bridgeObject.AddComponent<AstrBotBridge>();
                var exception = new InvalidOperationException("subscriber failure");
                bridge.CommandReceived += command => throw exception;

                const string json = "{\"command\":\"wave\"}";
                var previousIgnore = LogAssert.ignoreFailingMessages;
                try
                {
                    // The test deliberately exercises both the release warning and
                    // the debug error; Unity's log verifier must not turn either
                    // expected diagnostic into an unrelated test failure.
                    LogAssert.ignoreFailingMessages = true;
                    Assert.That(bridge.TryIngestCommandJson(json), Is.False);

                    QuestDebugMode.SetEnabled(true);
                    var caught = Assert.Throws<InvalidOperationException>(() =>
                        bridge.TryIngestCommandJson(json));
                    Assert.That(caught, Is.SameAs(exception));
                }
                finally
                {
                    LogAssert.ignoreFailingMessages = previousIgnore;
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(bridgeObject);
            }
        }

        [Test]
        public void LogGuardIsGatedByDebugMode()
        {
            QuestDebugMode.LogGuard("test", "reason");
            QuestDebugMode.SetEnabled(true);
            LogAssert.Expect(LogType.Warning, "[DebugMode][test] skip: reason");
            QuestDebugMode.LogGuard("test", "reason");
        }

        [Test]
        public void ForgetCompletedAndCancelledTasksAreSilent()
        {
            Task.CompletedTask.Forget("test.completed");
            Task.FromCanceled(new CancellationToken(true)).Forget("test.cancelled");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ForgetReportsEveryAggregateFailure()
        {
            LogAssert.Expect(LogType.Error,
                new Regex("\\[TaskFault\\]\\[test.aggregate\\].*first.*second", RegexOptions.Singleline));
            Task.WhenAll(
                Task.FromException(new InvalidOperationException("first")),
                Task.FromException(new ArgumentException("second"))).Forget("test.aggregate");
        }

        [Test]
        public void ForgetPostsDebugFailureToCallingContext()
        {
            var previousContext = SynchronizationContext.Current;
            var context = new CapturingContext();
            var exception = new InvalidOperationException("detached failure");
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                QuestDebugMode.SetEnabled(true);
                LogAssert.Expect(LogType.Error,
                    new Regex("\\[TaskFault\\]\\[test.detached\\].*detached failure", RegexOptions.Singleline));
                Task.FromException(exception).Forget("test.detached");
                Assert.That(context.Callbacks.Count, Is.EqualTo(1));
                var callback = context.Callbacks.Dequeue();
                var caught = Assert.Throws<InvalidOperationException>(() => callback.Item1(callback.Item2));
                Assert.That(caught, Is.SameAs(exception));
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }

        private sealed class CapturingContext : SynchronizationContext
        {
            public readonly Queue<Tuple<SendOrPostCallback, object>> Callbacks =
                new Queue<Tuple<SendOrPostCallback, object>>();

            public override void Post(SendOrPostCallback callback, object state)
            {
                Callbacks.Enqueue(Tuple.Create(callback, state));
            }
        }
    }
}
#endif
