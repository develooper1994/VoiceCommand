using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using Vosk;
using NAudio.Wave;
using NAudio.MediaFoundation;

namespace VoiceCommand.CommandDetection
{
    public enum DetectorBackend { Vosk, Whisper }

    // Facade that delegates to either the Vosk or Whisper detector implementation.
    // Use CommandDetector.ActiveBackend = DetectorBackend.Whisper to switch.
    public static class CommandDetector
    {
        public static DetectorBackend ActiveBackend { get; set; } = DetectorBackend.Vosk;

        public static string[] Commands => ActiveBackend == DetectorBackend.Vosk
            ? CommandDetectorVosk.Commands
            : CommandDetectorWhisper.Commands;

        public static string CleanToken(string s) => ActiveBackend == DetectorBackend.Vosk
            ? CommandDetectorVosk.CleanToken(s)
            : CommandDetectorWhisper.CleanToken(s);

        public static string[] ExtractWordsFromResult(string json) => ActiveBackend == DetectorBackend.Vosk
            ? CommandDetectorVosk.ExtractWordsFromResult(json)
            : CommandDetectorWhisper.ExtractWordsFromResult(json);

        public static string[] ExtractFinalWords(string json) => ActiveBackend == DetectorBackend.Vosk
            ? CommandDetectorVosk.ExtractFinalWords(json)
            : CommandDetectorWhisper.ExtractFinalWords(json);

        public static void SetBackend(DetectorBackend backend) => ActiveBackend = backend;

        public static bool TrySetBackend(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            name = name.Trim();
            if (name.Equals("vosk", StringComparison.OrdinalIgnoreCase)) { ActiveBackend = DetectorBackend.Vosk; return true; }
            if (name.Equals("whisper", StringComparison.OrdinalIgnoreCase)) { ActiveBackend = DetectorBackend.Whisper; return true; }
            return false;
        }
    }
}
namespace VoiceCommand.CommandDetection
{
    // Lightweight module extracted from Program.cs to centralize command normalization
    // and Vosk JSON parsing logic. This makes it easier to replace or add new
    // recognition backends (e.g., Whisper.net) by keeping command logic separate.
    public static class CommandDetectorVosk
    {
        // Canonical commands exposed to callers
        public static readonly string[] Commands = new[] { "başla", "başlat", "kapa", "kapat", "yazdır", "iptal", "yeniden dene", "kart ver", "para yükle", "bakiye kontrol" };

        // Clean token: remove punctuation (including interpunct), handle Turkish dotted/dotless I, lowercase invariant
        public static string CleanToken(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            // Replace Turkish dotted/dotless I explicitly before lowercasing
            s = s.Replace('\u0130', 'i'); // 'İ' -> 'i'
            s = s.Replace('I', '\u0131'); // 'I' -> 'ı'
            // Remove common separator characters like middle dot and any non-letter characters
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (char.IsLetter(ch) || char.IsWhiteSpace(ch)) sb.Append(ch);
            }
            return sb.ToString().Trim().ToLowerInvariant();
        }

        public static string[] ExtractWordsFromResult(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // If 'result' array present, extract 'word' entries
                if (root.TryGetProperty("result", out var resultProp) && resultProp.ValueKind == JsonValueKind.Array && resultProp.GetArrayLength() > 0)
                {
                    var list = new List<string>();
                    foreach (var item in resultProp.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("word", out var w))
                        {
                            var word = w.GetString();
                            if (!string.IsNullOrWhiteSpace(word)) list.Add(CleanToken(word));
                        }
                    }
                    if (list.Count > 0) return list.ToArray();
                }

                // Fallback to 'text' property if non-empty
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

                // Partial results
                if (root.TryGetProperty("partial", out var partialProp))
                {
                    var p = partialProp.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(p))
                    {
                        var parts = p.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 0; i < parts.Length; i++) parts[i] = CleanToken(parts[i]);
                        return parts; // return partial words but caller should ignore if they want finals only
                    }
                }
            }
            catch
            {
                // ignore
            }
            return Array.Empty<string>();
        }

        // Extract only final word tokens from a final result JSON (ignore empty 'text' finals)
        public static string[] ExtractFinalWords(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("result", out var resultProp) && resultProp.ValueKind == JsonValueKind.Array && resultProp.GetArrayLength() > 0)
                {
                    var list = new List<string>();
                    foreach (var item in resultProp.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("word", out var w))
                        {
                            var word = w.GetString();
                            if (!string.IsNullOrWhiteSpace(word)) list.Add(word);
                        }
                    }
                    return list.ToArray();
                }
            }
            catch
            {
            }
            return Array.Empty<string>();
        }

        // Transcribe a WAV file using Vosk model located in modelDir (path to model folder).
        // Returns recognized text (joined words) or empty string.
        public static string TranscribeFile(string modelDir, string wavPath)
        {
            if (!System.IO.File.Exists(wavPath)) throw new System.IO.FileNotFoundException("Audio file not found", wavPath);
            using var model = new Model(modelDir);
            using var recognizer = new VoskRecognizer(model, 16000.0f);

            using var reader = new NAudio.Wave.WaveFileReader(wavPath);
            // Ensure we have 16kHz mono 16-bit PCM bytes via sample providers
            NAudio.Wave.IWaveProvider waveProvider;
            if (reader.WaveFormat.SampleRate == 16000 && reader.WaveFormat.Channels == 1 && reader.WaveFormat.BitsPerSample == 16)
            {
                waveProvider = reader;
            }
            else
            {
                var outFmt = new WaveFormat(16000, 16, 1);
                var conv = new WaveFormatConversionStream(outFmt, reader);
                waveProvider = conv;
            }

            var buffer = new byte[4096];
            int read;
            string lastJson = null;
            while ((read = waveProvider.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (recognizer.AcceptWaveform(buffer, read))
                {
                    lastJson = recognizer.Result();
                }
            }

            try
            {
                var final = recognizer.FinalResult();
                if (!string.IsNullOrWhiteSpace(final)) lastJson = final;
            }
            catch { }

            if (string.IsNullOrWhiteSpace(lastJson)) return string.Empty;
            var words = ExtractWordsFromResult(lastJson);
            return string.Join(' ', words);
        }
    }
}
