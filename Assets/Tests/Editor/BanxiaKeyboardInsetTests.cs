﻿#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class BanxiaKeyboardInsetTests
    {
        [Test]
        public void KeyboardAreaHeightProvidesInsetForTopOrigin()
        {
            var area = new Rect(0f, 1498f, 1080f, 842f);
            Assert.That(ComputePixels(area, 2340f), Is.EqualTo(842f).Within(0.1f));
        }

        [Test]
        public void KeyboardAreaHeightProvidesInsetForBottomOrigin()
        {
            var area = new Rect(0f, 0f, 1080f, 842f);
            Assert.That(ComputePixels(area, 2340f), Is.EqualTo(842f).Within(0.1f));
        }

        [Test]
        public void EmptyKeyboardAreaClearsInset()
        {
            Assert.That(
                ComputePanelUnits(new Rect(0f, 0f, 0f, 0f), 2340f, 2340f),
                Is.EqualTo(0f));
        }

        [Test]
        public void PanelInsetKeepsBreathingRoom()
        {
            var area = new Rect(0f, 1498f, 1080f, 842f);
            Assert.That(ComputePanelUnits(area, 2340f, 2340f),
                Is.EqualTo(866f).Within(0.1f));
        }

        private static float ComputePixels(Rect area, float screenHeight)
        {
            var method = typeof(BanxiaUiShell).GetMethod(
                "ComputeKeyboardInsetPixels",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return (float)method.Invoke(null, new object[] { area, screenHeight });
        }

        private static float ComputePanelUnits(Rect area, float screenHeight, float panelHeight)
        {
            var method = typeof(BanxiaUiShell).GetMethod(
                "ComputeKeyboardInsetPanelUnits",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return (float)method.Invoke(null, new object[] { area, screenHeight, panelHeight });
        }
    }
}
#endif
