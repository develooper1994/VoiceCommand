using System;
using System.Collections.Generic;
using System.Text.Json;

// Minimal Whisper-backed command detector skeleton. Mirrors the Vosk detector API
// so it can be swapped in for comparison. This implementation does not yet call
// Whisper.net — it provides the same helpers and command list so you can
// integrate Whisper recognition and reuse the matching logic.

namespace VoiceCommand.CommandDetection
{
    public static class CommandDetectorWhisper
    {
        public static readonly string[] Commands = new[] { "başla", "başlat", "kapa", "kapat", "yazdır", "iptal", "yeniden dene", "kart ver", "para yükle", "bakiye kontrol" };

        public static string CleanToken(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            s = s.Replace('\u0130', 'i'); // 'İ' -> 'i'
            s = s.Replace('I', '\u0131'); // 'I' -> 'ı'
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (char.IsLetter(ch) || char.IsWhiteSpace(ch)) sb.Append(ch);
            }
            return sb.ToString().Trim().ToLowerInvariant();
        }

        // For parity with Vosk JSON helpers, provide the same signature. Whisper
        // outputs will differ; when you integrate Whisper.net, produce JSON or
        // call these helpers with similar data.
        public static string[] ExtractWordsFromResult(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("text", out var textProp))
                {
                    var t = textProp.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(t))
                    {
                        var parts = t.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 0; i < parts.Length; i++) parts[i] = CleanToken(parts[i]);
                        return parts;
                    }
                }
            }
            catch
            {
            }
            return Array.Empty<string>();
        }

        // Whisper doesn't provide the same 'result' array; provide a compatible
        // method that returns final tokens when you adapt Whisper's output.
        public static string[] ExtractFinalWords(string json)
        {
            // Default to ExtractWordsFromResult which checks 'text'
            return ExtractWordsFromResult(json);
        }
    }
}
