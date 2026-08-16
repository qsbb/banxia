using NUnit.Framework;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class RuntimePerformanceMonitorTests
    {
        [Test]
        public void FrameStatisticsIgnoreInvalidSamplesAndCalculatePercentiles()
        {
            var samples = new[] { 10f, 20f, 30f, 40f, 50f, 0f, float.NaN };

            RuntimePerformanceMonitor.CalculateFrameStatistics(
                samples,
                samples.Length,
                out var average,
                out var p50,
                out var p95,
                out var maximum);

            Assert.That(average, Is.EqualTo(30f).Within(0.001f));
            Assert.That(p50, Is.EqualTo(30f).Within(0.001f));
            Assert.That(p95, Is.EqualTo(48f).Within(0.001f));
            Assert.That(maximum, Is.EqualTo(50f).Within(0.001f));
        }

        [Test]
        public void FrameStatisticsReturnZeroForEmptyInput()
        {
            RuntimePerformanceMonitor.CalculateFrameStatistics(
                new float[0],
                0,
                out var average,
                out var p50,
                out var p95,
                out var maximum);

            Assert.That(average, Is.Zero);
            Assert.That(p50, Is.Zero);
            Assert.That(p95, Is.Zero);
            Assert.That(maximum, Is.Zero);
        }

        [TestCase(1, 4)]
        [TestCase(0, 0)]
        [TestCase(-1, 0)]
        public void TextureEstimateIsSafe(int dimension, long expected)
        {
            Assert.That(RuntimePerformanceMonitor.EstimateRgbaTextureBytes(dimension, dimension), Is.EqualTo(expected));
        }

        [Test]
        public void TextureEstimateUsesFourBytesPerPixel()
        {
            Assert.That(RuntimePerformanceMonitor.EstimateRgbaTextureBytes(1920, 1080), Is.EqualTo(1920L * 1080L * 4L));
        }

        [TestCase(-1, DeviceThermalState.Unknown)]
        [TestCase(0, DeviceThermalState.Normal)]
        [TestCase(6, DeviceThermalState.Shutdown)]
        [TestCase(99, DeviceThermalState.Unknown)]
        public void ThermalStatusMappingIsExplicit(int value, DeviceThermalState expected)
        {
            Assert.That(RuntimePerformanceMonitor.MapAndroidThermalStatus(value), Is.EqualTo(expected));
        }

        [Test]
        public void RecentFrameStatisticsDoNotIncludeInvalidLifecycleGap()
        {
            var samples = new[] { 13.8f, 13.9f, 14f, 0f, float.PositiveInfinity };

            RuntimePerformanceMonitor.CalculateFrameStatistics(
                samples,
                samples.Length,
                out var average,
                out _,
                out _,
                out _);

            Assert.That(1000f / average, Is.InRange(71f, 73f));
        }

        [Test]
        public void EnablingDetailedSamplingDoesNotResetWornFrameWindow()
        {
            var owner = new GameObject("Performance Monitor");
            try
            {
                var monitor = owner.AddComponent<RuntimePerformanceMonitor>();
                monitor.RecordFrameDurationMilliseconds(13.8f);

                monitor.SetDetailedSamplingEnabled(true);

                Assert.That(monitor.frameSampleCount, Is.EqualTo(1));
                Assert.That(monitor.currentFps, Is.GreaterThan(70f));
            }
            finally
            {
                Object.DestroyImmediate(owner);
            }
        }
    }
}
