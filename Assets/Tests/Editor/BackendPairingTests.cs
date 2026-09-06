using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class BackendPairingTests
    {
        [TestCase("bot.example.com", "http://bot.example.com/api/v1/plugins/extensions/astrbot_plugin_embodiment_bridge/pairing/exchange")]
        [TestCase("https://bot.example.com:7443", "https://bot.example.com:7443/api/v1/plugins/extensions/astrbot_plugin_embodiment_bridge/pairing/exchange")]
        [TestCase("https://bot.example.com/api/v1/plugins/extensions/astrbot_plugin_embodiment_bridge", "https://bot.example.com/api/v1/plugins/extensions/astrbot_plugin_embodiment_bridge/pairing/exchange")]
        public void PairingEndpointNormalizesHostPluginPathAndPort(string input, string expected)
        {
            Assert.That(
                BackendPairingProtocol.TryBuildExchangeEndpoint(input, out var endpoint, out var reason),
                Is.True,
                reason);
            Assert.That(endpoint, Is.EqualTo(expected));
        }

        [TestCase("https://bot.example.com:7443/api/v1/plugins/extensions/astrbot_plugin_embodiment_bridge/pairing/exchange", "bot.example.com:7443")]
        [TestCase("http://192.168.5.88:8520/api/v1/plugins/extensions/astrbot_plugin_embodiment_bridge/pairing/exchange", "192.168.5.88:8520")]
        public void PairingServerEntryHidesTheGeneratedPluginPath(string endpoint, string expected)
        {
            Assert.That(BackendPairingProtocol.GetServerEntry(endpoint), Is.EqualTo(expected));
        }
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
        public void PairingEndpointRejectsOversizedServerInput()
        {
            var oversized = new string('a', BackendPairingProtocol.MaxServerInputLength + 1);
            Assert.That(
                BackendPairingProtocol.TryBuildExchangeEndpoint(oversized, out _, out var reason),
                Is.False);
            Assert.That(reason, Does.Contain("length limit"));
        }

        [Test]
        public void DefaultServerEntryUsesPlainHttpAndExplicitHttpsRemainsAvailable()
        {
            Assert.That(
                BackendPairingProtocol.TryBuildExchangeEndpoint(
                    "192.168.5.88:8520",
                    out var privateEndpoint,
                    out var privateReason),
                Is.True,
                privateReason);
            Assert.That(privateEndpoint, Is.EqualTo("http://192.168.5.88:8520/api/v1/plugins/extensions/astrbot_plugin_embodiment_bridge/pairing/exchange"));

            Assert.That(
                BackendPairingProtocol.TryBuildExchangeEndpoint(
                    "https://bot.example.com:7443",
                    out var secureEndpoint,
                    out var secureReason),
                Is.True,
                secureReason);
            Assert.That(secureEndpoint, Does.StartWith("https://"));

            Assert.That(
                BackendPairingProtocol.TryBuildExchangeEndpoint(
                    "http://192.168.5.88:8520",
                    out _,
                    out var disabledReason,
                    false),
                Is.False);
            Assert.That(disabledReason, Does.Contain("HTTPS"));
        }

        [Test]
        public void QrPayloadCarriesOnlyOneTimeTokenAndEndpoint()
        {
            var token = new string('x', 43);
            var json = "{\"type\":\"astrbot.quest.pair\",\"version\":\"1.0\","
                + "\"exchange_url\":\"https://bot.example.com/api/v1/plugins/extensions/astrbot_plugin_embodiment_bridge/pairing/exchange\","
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
        public void LegacyPluginEndpointsAreAcceptedAndRewrittenToCurrentPlugin()
        {
            var legacyBaseUrl = "https://bot.example.com/api/v1/plugins/extensions/astrbot_plugin_quest_avatar_bridge";
            Assert.That(
                BackendPairingProtocol.TryBuildExchangeEndpoint(
                    legacyBaseUrl + "/pairing/exchange",
                    out var exchangeEndpoint,
                    out var exchangeReason),
                Is.True,
                exchangeReason);
            Assert.That(exchangeEndpoint, Is.EqualTo(
                "https://bot.example.com/api/v1/plugins/extensions/astrbot_plugin_embodiment_bridge/pairing/exchange"));

            Assert.That(
                BackendPairingProtocol.TryUpgradeLegacyPluginBaseUrl(legacyBaseUrl, out var upgradedBaseUrl),
                Is.True);
            Assert.That(upgradedBaseUrl, Is.EqualTo(
                "https://bot.example.com/api/v1/plugins/extensions/astrbot_plugin_embodiment_bridge"));
        }

        [Test]
        public void ClearPairingServerRemovesCurrentAndLegacyPreferences()
        {
            const string currentKey = "embodiment_bridge_pairing_server_v1";
            const string legacyKey = "quest_avatar_pairing_server_v1";
            var hadCurrent = PlayerPrefs.HasKey(currentKey);
            var hadLegacy = PlayerPrefs.HasKey(legacyKey);
            var previousCurrent = PlayerPrefs.GetString(currentKey, string.Empty);
            var previousLegacy = PlayerPrefs.GetString(legacyKey, string.Empty);
            var owner = new GameObject("Pairing server preference cleanup");
            try
            {
                PlayerPrefs.SetString(legacyKey, "https://legacy.example");
                var controller = owner.AddComponent<BackendPairingController>();
                Assert.That(
                    controller.TrySetPairingServer("https://new.example", out var reason),
                    Is.True,
                    reason);

                controller.ClearPairingServer();

                Assert.That(controller.PairingServerEndpoint, Is.Empty);
                Assert.That(PlayerPrefs.HasKey(currentKey), Is.False);
                Assert.That(PlayerPrefs.HasKey(legacyKey), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
                if (hadCurrent)
                {
                    PlayerPrefs.SetString(currentKey, previousCurrent);
                }
                else
                {
                    PlayerPrefs.DeleteKey(currentKey);
                }
                if (hadLegacy)
                {
                    PlayerPrefs.SetString(legacyKey, previousLegacy);
                }
                else
                {
                    PlayerPrefs.DeleteKey(legacyKey);
                }
                PlayerPrefs.Save();
            }
        }

        [Test]
        public void LegacyConfigurationMigratesOnceAndPreservesSource()
        {
            var directory = Path.Combine(Path.GetTempPath(), "embodiment-migration-" + Guid.NewGuid().ToString("N"));
            var legacyPath = Path.Combine(directory, "quest_avatar_bridge.json");
            var currentPath = Path.Combine(directory, "embodiment_bridge.json");
            Directory.CreateDirectory(directory);
            try
            {
                var legacySettings = ValidSettings("legacy-user");
                legacySettings.base_url =
                    "https://bot.example.com/api/v1/plugins/extensions/astrbot_plugin_quest_avatar_bridge";
                File.WriteAllText(legacyPath, JsonUtility.ToJson(legacySettings));

                Assert.That(
                    BackendPairingProtocol.TryMigrateLegacyConfiguration(
                        legacyPath,
                        currentPath,
                        out var migrated,
                        out var reason),
                    Is.True,
                    reason);
                Assert.That(migrated, Is.True);
                Assert.That(File.Exists(legacyPath), Is.True, "Migration must keep the downgrade copy");
                var currentSettings = JsonUtility.FromJson<AstrBotBridgeSettings>(File.ReadAllText(currentPath));
                Assert.That(currentSettings.user_id, Is.EqualTo("legacy-user"));
                Assert.That(currentSettings.base_url, Is.EqualTo(
                    "https://bot.example.com/api/v1/plugins/extensions/astrbot_plugin_embodiment_bridge"));

                File.WriteAllText(currentPath, "current-wins");
                Assert.That(
                    BackendPairingProtocol.TryMigrateLegacyConfiguration(
                        legacyPath,
                        currentPath,
                        out migrated,
                        out reason),
                    Is.True,
                    reason);
                Assert.That(migrated, Is.False);
                Assert.That(File.ReadAllText(currentPath), Is.EqualTo("current-wins"));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void LegacyConfigurationMigrationRejectsUnrelatedPluginPath()
        {
            var directory = Path.Combine(Path.GetTempPath(), "embodiment-migration-reject-" + Guid.NewGuid().ToString("N"));
            var legacyPath = Path.Combine(directory, "quest_avatar_bridge.json");
            var currentPath = Path.Combine(directory, "embodiment_bridge.json");
            Directory.CreateDirectory(directory);
            try
            {
                var legacySettings = ValidSettings("legacy-user");
                legacySettings.base_url =
                    "https://bot.example.com/api/v1/plugins/extensions/another_plugin";
                File.WriteAllText(legacyPath, JsonUtility.ToJson(legacySettings));

                Assert.That(
                    BackendPairingProtocol.TryMigrateLegacyConfiguration(
                        legacyPath,
                        currentPath,
                        out var migrated,
                        out var reason),
                    Is.False);
                Assert.That(migrated, Is.False);
                Assert.That(reason, Does.Contain("recognized bridge path"));
                Assert.That(File.Exists(currentPath), Is.False);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
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
            var path = Path.Combine(directory, "embodiment_bridge.json");
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
            var path = Path.Combine(directory, "embodiment_bridge.json");
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "original");
            try
            {
                var settings = ValidSettings("user-a");
                settings.base_url = "http://bot.example.com/api/v1/plugins/extensions/astrbot_plugin_embodiment_bridge";
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
            var path = Path.Combine(directory, "embodiment_bridge.json");
            try
            {
                var settings = ValidSettings("user-lan");
                settings.base_url = "http://192.168.5.88:8520/api/v1/plugins/extensions/astrbot_plugin_embodiment_bridge";
                settings.allow_insecure_http = true;
                Assert.That(
                    BackendPairingProtocol.TryWriteSettingsAtomically(path, settings, out _, false),
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
                base_url = "https://bot.example.com/api/v1/plugins/extensions/astrbot_plugin_embodiment_bridge",
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
