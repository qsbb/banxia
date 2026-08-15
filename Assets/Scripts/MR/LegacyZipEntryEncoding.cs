using System;
using System.Text;
using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Decodes legacy ZIP entry names without relying on code-page assemblies on
    /// IL2CPP. ZipArchive only uses this encoding when the UTF-8 flag is absent.
    /// </summary>
    public sealed class LegacyZipEntryEncoding : Encoding
    {
        internal static readonly LegacyZipEntryEncoding Instance = new LegacyZipEntryEncoding();

        private LegacyZipEntryEncoding()
        {
        }

        public override int GetCharCount(byte[] bytes, int index, int count)
        {
            return Decode(bytes, index, count).Length;
        }

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            if (chars == null) throw new ArgumentNullException(nameof(chars));
            var decoded = Decode(bytes, byteIndex, byteCount);
            if (charIndex < 0 || charIndex + decoded.Length > chars.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(charIndex));
            }

            decoded.CopyTo(0, chars, charIndex, decoded.Length);
            return decoded.Length;
        }

        public override int GetByteCount(char[] chars, int index, int count)
        {
            return Encoding.UTF8.GetByteCount(chars, index, count);
        }

        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            return Encoding.UTF8.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
        }

        public override int GetMaxByteCount(int charCount)
        {
            return Encoding.UTF8.GetMaxByteCount(charCount);
        }

        public override int GetMaxCharCount(int byteCount)
        {
            return byteCount;
        }

        public static string Decode(byte[] bytes, int index, int count)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (index < 0 || count < 0 || index + count > bytes.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            var allAscii = true;
            for (var offset = 0; offset < count; offset++)
            {
                if (bytes[index + offset] >= 0x80)
                {
                    allAscii = false;
                    break;
                }
            }
            if (allAscii)
            {
                return Encoding.ASCII.GetString(bytes, index, count);
            }

            var gbk = DecodeCodePage(bytes, index, count, 936, "GBK");
            var shiftJis = DecodeCodePage(bytes, index, count, 932, "windows-31j");
            return Score(gbk) >= Score(shiftJis) ? gbk : shiftJis;
        }

        private static string DecodeCodePage(
            byte[] bytes,
            int index,
            int count,
            int codePage,
            string androidCharset)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var slice = new byte[count];
            Buffer.BlockCopy(bytes, index, slice, 0, count);
            try
            {
                using (var value = new AndroidJavaObject("java.lang.String", slice, androidCharset))
                {
                    return value.Call<string>("toString");
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[FileImport] Legacy ZIP filename decoder unavailable: " + exception.Message);
                return string.Empty;
            }
#else
            try
            {
                return Encoding.GetEncoding(
                    codePage,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback).GetString(bytes, index, count);
            }
            catch (Exception exception) when (
                exception is ArgumentException ||
                exception is DecoderFallbackException ||
                exception is NotSupportedException)
            {
                return string.Empty;
            }
#endif
        }

        private static int Score(string value)
        {
            if (string.IsNullOrEmpty(value)) return int.MinValue / 2;
            var score = 0;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (character == '\uFFFD' || char.IsControl(character))
                {
                    score -= 100;
                }
                else if (character >= '\u3040' && character <= '\u30FF')
                {
                    score += 5;
                }
                else if (character >= '\u4E00' && character <= '\u9FFF')
                {
                    score += 3;
                }
                else if (character < 0x80)
                {
                    score += 1;
                }
            }
            return score;
        }
    }
}
