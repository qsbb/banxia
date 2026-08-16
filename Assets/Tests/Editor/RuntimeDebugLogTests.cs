#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class RuntimeDebugLogTests
    {
        private GameObject owner;
        private RuntimeDebugLog diagnostics;

        [SetUp]
        public void SetUp()
        {
            owner = new GameObject("RuntimeDebugLogTests");
            diagnostics = owner.AddComponent<RuntimeDebugLog>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(owner);
        }

        [Test]
        public void AuthorizationFailureRemainsRootCauseAfterSseConnects()
        {
            diagnostics.RecordStage(
                "authorization",
                "limited",
                "owner_not_configured",
                httpStatus: 201,
                elapsedMs: 72);
            diagnostics.RecordStage("sse", "connected", httpStatus: 200, elapsedMs: 35);

            Assert.That(
                diagnostics.CurrentRootCause,
                Is.EqualTo("身份授权：“序”尚未为这组 Quest 原始身份配置主人"));
            Assert.That(diagnostics.GetRecentTimelineText(5), Does.Contain("[身份授权] 受限"));
            Assert.That(diagnostics.GetRecentTimelineText(5), Does.Contain("HTTP 201"));
            Assert.That(diagnostics.GetRecentTimelineText(5), Does.Contain("[实时事件] 已连接"));
        }

        [Test]
        public void SuccessfulRetryClearsSameStageRootCause()
        {
            diagnostics.RecordStage("stt", "failed", "stt_failed");
            Assert.That(diagnostics.CurrentRootCause, Does.Contain("语音识别失败"));

            diagnostics.RecordStage("stt", "completed", elapsedMs: 480);

            Assert.That(diagnostics.CurrentRootCause, Is.EqualTo("未发现明确的失败阶段"));
        }

        [Test]
        public void ClearRemovesTimelineAndRootCause()
        {
            diagnostics.RecordStage("reply", "failed", "llm_failed");

            diagnostics.Clear();

            Assert.That(diagnostics.GetRecentTimelineText(), Is.Empty);
            Assert.That(diagnostics.CurrentRootCause, Is.EqualTo("未发现明确的失败阶段"));
        }

        [Test]
        public void TimelinePagingCanPauseAwayFromNewestEntries()
        {
            for (var index = 0; index < 6; index++)
            {
                diagnostics.RecordStage("transport", "ok", "page_" + index);
            }

            var newest = diagnostics.GetTimelineText(2, 0);
            var previous = diagnostics.GetTimelineText(2, 2);

            Assert.That(newest, Does.Contain("page_5"));
            Assert.That(newest, Does.Not.Contain("page_3"));
            Assert.That(previous, Does.Contain("page_3"));
            Assert.That(previous, Does.Not.Contain("page_5"));
        }

        [Test]
        public void TraceLabelsAndQueueMetricsStayShortAndVisible()
        {
            const string rawTurnId = "voice-turn-with-sensitive-session-context";
            var trace = RuntimeDebugLog.TraceLabel(rawTurnId);

            diagnostics.RecordStage(
                "sse_dispatch",
                "completed",
                "sse_queue",
                elapsedMs: 12,
                traceId: trace,
                queueDepth: 3,
                bufferedMs: 120);

            Assert.That(trace, Does.Not.Contain(rawTurnId));
            Assert.That(trace, Does.Match("^t[0-9a-f]{8}$"));
            Assert.That(diagnostics.GetRecentTimelineText(), Does.Contain("#" + trace));
            Assert.That(diagnostics.GetRecentTimelineText(), Does.Contain("队列3"));
            Assert.That(diagnostics.GetRecentTimelineText(), Does.Contain("缓冲120ms"));
        }

        [Test]
        public void TraceLabelsAreStableWithinProcessAndSeparateTurns()
        {
            var first = RuntimeDebugLog.TraceLabel("turn-one");
            var repeated = RuntimeDebugLog.TraceLabel("turn-one");
            var second = RuntimeDebugLog.TraceLabel("turn-two");

            Assert.That(repeated, Is.EqualTo(first));
            Assert.That(second, Is.Not.EqualTo(first));
        }
    }
}
#endif
