using System;
using System.Security.Cryptography;
using System.Text;
using UnityEngine.Networking;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Per-request certificate validator for the configured leaf certificate
    /// fingerprint. Unity supplies the DER-encoded leaf certificate to
    /// ValidateCertificate; no CA/global callback is installed here.
    /// </summary>
    internal sealed class CertificatePinningHandler : CertificateHandler
    {
        public const int MaxCertificateBytes = 512 * 1024;
        private readonly byte[] expected;

        public bool Matched { get; private set; }
        public bool ValidationAttempted { get; private set; }

        /// <summary>
        /// True only when Unity called the pin validator and the supplied leaf
        /// certificate did not match. A TLS/DNS/reachability failure that never
        /// reaches the callback remains an ordinary transport failure.
        /// </summary>
        internal bool IsPinMismatch => ValidationAttempted && !Matched;

        internal bool IsTerminalFailure => IsPinMismatch;

        public CertificatePinningHandler(string hex)
        {
            expected = Parse(hex);
            if (expected == null)
            {
                // Do not include the supplied value in the exception or any
                // log path: fingerprints are configuration secrets.
                throw new ArgumentException(
                    "certificate_pin_sha256 must be exactly 64 hexadecimal characters");
            }
        }

        protected override bool ValidateCertificate(byte[] certificateData)
        {
            ValidationAttempted = true;
            Matched = MatchesExpected(certificateData);
            return Matched;
        }

        internal static bool IsValidPin(string value)
        {
            return Parse(value) != null;
        }

        /// <summary>Public, pure format check for pairing/settings UIs.</summary>
        public static bool IsValidCertificatePin(string value)
        {
            return IsValidPin(value);
        }

        internal static string NormalizePin(string value)
        {
            if (string.IsNullOrEmpty(value) || !IsValidPin(value))
            {
                return string.Empty;
            }
            return value.ToLowerInvariant();
        }

        /// <summary>
        /// Pure helper used by transport tests and validation code. The input
        /// is the raw leaf DER bytes, not PEM text, a certificate chain, or a
        /// textual/base64 representation.
        /// </summary>
        internal static bool MatchesCertificate(byte[] certificateData, string pin)
        {
            var parsed = Parse(pin);
            return parsed != null && MatchesCertificate(certificateData, parsed);
        }

        /// <summary>Public, pure leaf-DER SHA-256 check for editor tests.</summary>
        public static bool MatchesLeafCertificate(byte[] certificateDer, string pin)
        {
            return MatchesCertificate(certificateDer, pin);
        }

        private bool MatchesExpected(byte[] certificateData)
        {
            return MatchesCertificate(certificateData, expected);
        }

        private static bool MatchesCertificate(byte[] certificateData, byte[] expected)
        {
            if (expected == null || !TryNormalizeCertificateData(certificateData, out var der))
            {
                return false;
            }

            using (var sha = SHA256.Create())
            {
                var actual = sha.ComputeHash(der);
                if (actual.Length != expected.Length)
                {
                    return false;
                }

                // Avoid an early exit while comparing the digest.
                var difference = 0;
                for (var index = 0; index < actual.Length; index++)
                {
                    difference |= actual[index] ^ expected[index];
                }
                return difference == 0;
            }
        }

        private static bool TryNormalizeCertificateData(byte[] certificateData, out byte[] der)
        {
            der = null;
            if (certificateData == null || certificateData.Length == 0 ||
                certificateData.Length > MaxCertificateBytes)
            {
                return false;
            }

            // Unity normally supplies leaf DER. Accepting PEM here is useful for
            // deterministic editor/native tests and remains bounded; never hash
            // the PEM armor or whitespace itself.
            var text = Encoding.ASCII.GetString(certificateData);
            const string begin = "-----BEGIN CERTIFICATE-----";
            const string end = "-----END CERTIFICATE-----";
            if (!text.StartsWith(begin, StringComparison.Ordinal))
            {
                der = certificateData;
                return true;
            }

            var bodyStart = begin.Length;
            var bodyEnd = text.IndexOf(end, bodyStart, StringComparison.Ordinal);
            if (bodyEnd < 0 || text.Substring(bodyEnd + end.Length).Trim().Length != 0)
            {
                return false;
            }

            var encoded = new StringBuilder(bodyEnd - bodyStart);
            for (var index = bodyStart; index < bodyEnd; index++)
            {
                var value = text[index];
                if (char.IsWhiteSpace(value))
                {
                    continue;
                }
                if (!IsBase64Character(value))
                {
                    return false;
                }
                encoded.Append(value);
                if (encoded.Length > ((MaxCertificateBytes + 2) / 3) * 4)
                {
                    return false;
                }
            }

            if (encoded.Length == 0 || encoded.Length % 4 != 0)
            {
                return false;
            }
            try
            {
                var decoded = Convert.FromBase64String(encoded.ToString());
                if (decoded.Length == 0 || decoded.Length > MaxCertificateBytes)
                {
                    return false;
                }
                der = decoded;
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static bool IsBase64Character(char value)
        {
            return (value >= 'A' && value <= 'Z') ||
                (value >= 'a' && value <= 'z') ||
                (value >= '0' && value <= '9') ||
                value == '+' || value == '/' || value == '=';
        }

        private static int Hex(char value)
        {
            if (value >= '0' && value <= '9') return value - '0';
            if (value >= 'a' && value <= 'f') return value - 'a' + 10;
            if (value >= 'A' && value <= 'F') return value - 'A' + 10;
            return -1;
        }

        private static byte[] Parse(string value)
        {
            // Deliberately do not Trim: the wire/configuration form is a bare,
            // exactly-64-character hexadecimal SHA-256 digest.
            if (value == null || value.Length != 64)
            {
                return null;
            }

            var bytes = new byte[32];
            for (var index = 0; index < bytes.Length; index++)
            {
                var high = Hex(value[index * 2]);
                var low = Hex(value[index * 2 + 1]);
                if (high < 0 || low < 0)
                {
                    return null;
                }
                bytes[index] = (byte)((high << 4) | low);
            }
            return bytes;
        }
    }
}
