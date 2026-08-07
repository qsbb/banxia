using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class BackendPairingTests
    {
        [TestCase("bot.example.com", "https://bot.example.com/api/v1/plugins/extensions/astrbot_plugin_quest_avatar_bridge/pairing/exchange")]
        [TestCase("https://bot.example.com:7443", "https://bot.example.com:7443/api/v1/plugins/extensions/astrbot_plugin_quest_avatar_bridge/pairing/exchange")]
        [TestCase("https://bot.example.com/api/v1/plugins/extensions/astrbot_plugin_quest_avatar_bridge", "https://bot.example.com/api/v1/plugins/extensions/astrbot_plugin_quest_avatar_bridge/pairing/exchange")]
        public void PairingEndpointNormalizesHostPluginPathAndPort(string input, string expected)
        {
            Assert.That(
                BackendPairingProtocol.TryBuildExchangeEndpoint(input, out var endpoint, out var reason),
                Is.True,
                reason);
            Assert.That(endpoint, Is.EqualTo(expected));
        }

        [TestCase("https://bot.example.com:7443/api/v1/plugins/extensions/astrbot_plugin_quest_avatar_bridge/pairing/exchange", "bot.example.com:7443")]
        [TestCase("http://192.168.5.88:8520/api/v1/plugins/extensions/astrbot_plugin_quest_avatar_bridge/pairing/exchange", "192.168.5.88:8520")]
        public void PairingServerEntryHidesTheGeneratedPluginPath(string endpoint, string expected)
        {
            Assert.That(BackendPairingProtocol.GetServerEntry(endpoint), Is.EqualTo(expected));
        }
        [TestCase("http://bot.example.com")]
        [TestCase("https://user:pass@bot.example.com")]
        [TestCase("https://bot.example.com/dashboard")]
        [TestCase("https://bot.example.com?secret=value")]
        public void PairingEndpointRejectsUnsafeOrWrongUrls(string input)
        {
            Assert.That(
                BackendPairingProtocol.TryBuildExchangeEndpoint(input, out _, out _),
                Is.False);
        }

        [Test]
        public void ExplicitPrivateHttpAcceptsOnlyLiteralLanIp()
        {
            Assert.That(
                BackendPairingProtocol.TryBuildExchangeEndpoint("192.168.5.88:8520", out _, out var privateIpReason),
                Is.False);
            Assert.That(privateIpReason, Does.Contain("private-LAN HTTP"));

            Assert.That(
                BackendPairingProtocol.TryBuildExchangeEndpoint(
                    "http://192.168.5.88:8520/api/v1/plugins/extensions/astrbot_plugin_quest_avatar_bridge/pairing/exchange",
                    out _,
                    out _),
                Is.False,
                "Private-LAN HTTP must remain disabled until the operator explicitly opts in");

            Assert.That(
                BackendPairingProtocol.TryBuildExchangeEndpoint(
                    "192.168.5.88:8520",
                    out var endpoint,
                    out var reason,
                    true),
                Is.True,
                reason);
            Assert.That(endpoint, Is.EqualTo("http://192.168.5.88:8520/api/v1/plugins/extensions/astrbot_plugin_quest_avatar_bridge/pairing/exchange"));

            Assert.That(
                BackendPairingProtocol.TryBuildExchangeEndpoint("http://api.example.com", out _, out _, true),
                Is.False);
            Assert.That(
                BackendPairingProtocol.TryBuildExchangeEndpoint("http://nas.local", out _, out _, true),
                Is.False);
        }

        [Test]
        public void QrPayloadCarriesOnlyOneTimeTokenAndEndpoint()
        {
            var token = new string('x', 43);
            var json = "{\"type\":\"astrbot.quest.pair\",\"version\":\"1.0\","
                + "\"exchange_url\":\"https://bot.example.com/api/v1/plugins/extensions/astrbot_plugin_quest_avatar_bridge/pairing/exchange\","
                + "\"token\":\"" + token + "\"}";

            Assert.That(
                BackendPairingProtocol.TryParseQrPayload(
                    json,
                    out var endpoint,
                    out var parsedToken,
                    out var reason),
                Is.True,
                reason);
            Assert.That(endpoint, Does.EndWith("/pairing/exchange"));
            Assert.That(parsedToken, Is.EqualTo(token));
            Assert.That(json, Does.Not.Contain("astrbot_api_key"));
            Assert.That(json, Does.Not.Contain("bridge_api_key"));
        }

        [Test]
        public void ShortCodeKeepsOnlyFirstSixDigits()
        {
            Assert.That(BackendPairingProtocol.NormalizeShortCode("12 3a45-678"), Is.EqualTo("123456"));
            Assert.That(BackendPairingProtocol.NormalizeShortCode(null), Is.Empty);
        }

        [Test]
        public void PairedSettingsAreValidatedAndReplacedAtomically()
        {
            var directory = Path.Combine(Path.GetTempPath(), "quest-pairing-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "quest_avatar_bridge.json");
            try
            {
                var first = ValidSettings("user-a");
                Assert.That(
                    BackendPairingProtocol.TryWriteSettingsAtomically(path, first, out var firstReason),
                    Is.True,
                    firstReason);
                Assert.That(File.Exists(path), Is.True);

                var second = ValidSettings("user-b");
                Assert.That(
                    BackendPairingProtocol.TryWriteSettingsAtomically(path, second, out var secondReason),
                    Is.True,
                    secondReason);
                var loaded = JsonUtility.FromJson<AstrBotBridgeSettings>(File.ReadAllText(path));
                Assert.That(loaded.user_id, Is.EqualTo("user-b"));
                Assert.That(loaded.allow_insecure_http, Is.False);
                Assert.That(File.Exists(path + ".pairing.tmp"), Is.False);
                Assert.That(File.Exists(path + ".pairing.bak"), Is.False);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void AtomicWriteRejectsHttpWithoutTouchingExistingConfig()
        {
            var directory = Path.Combine(Path.GetTempPath(), "quest-pairing-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "quest_avatar_bridge.json");
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "original");
            try
            {
                var settings = ValidSettings("user-a");
                settings.base_url = "http://bot.example.com/api/v1/plugins/extensions/astrbot_plugin_quest_avatar_bridge";
                settings.allow_insecure_http = true;
                Assert.That(
                    BackendPairingProtocol.TryWriteSettingsAtomically(path, settings, out _),
                    Is.False);
                Assert.That(File.ReadAllText(path), Is.EqualTo("original"));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void AtomicWriteAllowsExplicitPrivateLanHttp()
        {
            var directory = Path.Combine(Path.GetTempPath(), "quest-pairing-lan-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "quest_avatar_bridge.json");
            try
            {
                var settings = ValidSettings("user-lan");
                settings.base_url = "http://192.168.5.88:8520/api/v1/plugins/extensions/astrbot_plugin_quest_avatar_bridge";
                settings.allow_insecure_http = true;
                Assert.That(
                    BackendPairingProtocol.TryWriteSettingsAtomically(path, settings, out _),
                    Is.False,
                    "A paired payload cannot enable private-LAN HTTP without the local operator opt-in");
                Assert.That(File.Exists(path), Is.False);

                Assert.That(
                    BackendPairingProtocol.TryWriteSettingsAtomically(path, settings, out var reason, true),
                    Is.True,
                    reason);
                var loaded = JsonUtility.FromJson<AstrBotBridgeSettings>(File.ReadAllText(path));
                Assert.That(loaded.allow_insecure_http, Is.True);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        private static AstrBotBridgeSettings ValidSettings(string userId)
        {
            return new AstrBotBridgeSettings
            {
                base_url = "https://bot.example.com/api/v1/plugins/extensions/astrbot_plugin_quest_avatar_bridge",
                astrbot_api_key = "plugin-scope-key",
                bridge_api_key = "bridge-key-000000000000000000000000",
                client_id = "quest-living-room",
                user_id = userId,
                bot_id = "bot-id",
                group_id = string.Empty,
                relationship_profile_id = string.Empty,
                allow_insecure_http = false
            };
        }
    }
}
