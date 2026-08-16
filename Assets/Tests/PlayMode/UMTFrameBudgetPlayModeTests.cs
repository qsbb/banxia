using System.Collections;
using NUnit.Framework;
using UMT;
using UnityEngine;
using UnityEngine.TestTools;

namespace QuestMmdPlayer.Tests
{
    public sealed class UMTFrameBudgetPlayModeTests
    {
        [UnityTest]
        public IEnumerator YieldIfNeededCrossesARealPlayerLoopFrame()
        {
            var budget = new UMTFrameBudget(0d);
            var startedFrame = Time.frameCount;
            var pending = budget.YieldIfNeeded();

            while (!pending.IsCompleted)
            {
                yield return null;
            }

            Assert.That(pending.IsFaulted, Is.False);
            Assert.That(pending.IsCanceled, Is.False);
            Assert.That(Time.frameCount, Is.GreaterThan(startedFrame));
            Assert.That(budget.YieldCount, Is.EqualTo(1));
        }
    }
}
