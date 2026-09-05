#if UNITY_EDITOR
using NUnit.Framework;

namespace QuestMmdPlayer.Tests
{
    public sealed class CallFramingSolverTests
    {
        private static CallFramingSolver.Inputs TypicalInputs()
        {
            return new CallFramingSolver.Inputs
            {
                S = 3200f,
                ThetaDeg = 60f,
                TopPx = 330f,
                BottomPx = 2640f,
                EyeY = 1.63f,
                HeadTopY = 1.73f,
                FootY = 0f,
                LowCutY = 1.19f,
            };
        }

        [Test]
        public void BustSolutionPlacesEyeAtUpperThirdAndWaistAtBottom()
        {
            var input = TypicalInputs();
            var result = CallFramingSolver.SolveBust(input);
            var k = 3200f * 0.5f / 0.577350269f;
            var eyeScreen = 3200f * 0.5f +
                (result.CameraY - input.EyeY) * k / result.Distance;
            var waistScreen = 3200f * 0.5f +
                (result.CameraY - input.LowCutY) * k / result.Distance;
            var expectedEye = input.TopPx +
                (input.BottomPx - input.TopPx) * CallFramingSolver.EyeLineRatio;

            Assert.That(result.Degraded, Is.False);
            Assert.That(result.Distance, Is.InRange(0.55f, 2.4f));
            Assert.That(result.Distance, Is.EqualTo(.791f).Within(.01f));
            Assert.That(eyeScreen, Is.EqualTo(expectedEye).Within(.5f));
            Assert.That(waistScreen, Is.EqualTo(input.BottomPx).Within(.5f));
        }

        [Test]
        public void BustSolutionSupportsLowerPhoneEyeLine()
        {
            var input = TypicalInputs();
            const float phoneEyeLineRatio = 0.42f;
            var result = CallFramingSolver.SolveBust(
                input,
                CallFramingSolver.DistanceMax,
                phoneEyeLineRatio);
            var k = 3200f * 0.5f / 0.577350269f;
            var eyeScreen = 3200f * 0.5f +
                (result.CameraY - input.EyeY) * k / result.Distance;
            var expectedEye = input.TopPx +
                (input.BottomPx - input.TopPx) * phoneEyeLineRatio;

            Assert.That(result.Degraded, Is.False);
            Assert.That(eyeScreen, Is.EqualTo(expectedEye).Within(.5f));
        }

        [Test]
        public void BustSolutionMarksDistanceLowerClampAsDegraded()
        {
            var input = TypicalInputs();
            input.LowCutY = input.EyeY - 0.01f;
            var result = CallFramingSolver.SolveBust(input);

            Assert.That(result.Distance, Is.EqualTo(CallFramingSolver.DistanceMin));
            Assert.That(result.Degraded, Is.True);
        }

        [Test]
        public void BustSolutionUsesUpperClampAndWaistAnchoredFallback()
        {
            var input = TypicalInputs();
            input.LowCutY = input.EyeY - 8f;
            var result = CallFramingSolver.SolveBust(input, 1.6f);
            var k = 3200f * 0.5f / 0.577350269f;
            var waistScreen = 3200f * 0.5f +
                (result.CameraY - input.LowCutY) * k / result.Distance;

            Assert.That(result.Distance, Is.EqualTo(1.6f));
            Assert.That(result.Degraded, Is.True);
            Assert.That(waistScreen, Is.EqualTo(input.BottomPx).Within(.5f));
        }

        [Test]
        public void FullBodySolutionUsesLowerTwelveToNinetySixPercentBand()
        {
            var input = TypicalInputs();
            var result = CallFramingSolver.SolveFullBody(input);
            var k = 3200f * 0.5f / 0.577350269f;
            var headScreen = 3200f * 0.5f +
                (result.CameraY - input.HeadTopY) * k / result.Distance;
            var footScreen = 3200f * 0.5f +
                (result.CameraY - input.FootY) * k / result.Distance;

            Assert.That(result.Degraded, Is.False);
            Assert.That(result.Distance, Is.EqualTo(1.78f).Within(.03f));
            Assert.That(headScreen, Is.EqualTo(3200f * CallFramingSolver.FrameBandTop).Within(.5f));
            Assert.That(footScreen, Is.EqualTo(3200f * CallFramingSolver.FrameBandBottom).Within(.5f));
            Assert.That((headScreen + footScreen) * 0.5f,
                Is.EqualTo(3200f * 0.54f).Within(.5f));
        }

        [Test]
        public void InvalidMeasurementsReturnFiniteFallback()
        {
            var input = TypicalInputs();
            input.S = 0f;
            var bust = CallFramingSolver.SolveBust(input);
            var full = CallFramingSolver.SolveFullBody(input);

            Assert.That(bust.Degraded, Is.True);
            Assert.That(bust.Distance, Is.EqualTo(CallFramingSolver.FallbackDistance));
            Assert.That(full.Degraded, Is.True);
            Assert.That(full.Distance, Is.EqualTo(CallFramingSolver.FallbackDistance));
        }
    }
}
#endif
