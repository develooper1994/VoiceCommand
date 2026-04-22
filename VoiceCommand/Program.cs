using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Globalization;

using NAudio.Wave;

using Vosk;

class Program
{
    static readonly ManualResetEventSlim StopEvent = new(false);

    static void Main(string[] args)
    {
        Console.WriteLine("Sesli komut dinleyici başlatılıyor...");
        var modelPath = Path.Combine(AppContext.BaseDirectory, "model");
        if (!Directory.Exists(modelPath))
        {
            Console.WriteLine($"Model klasörü bulunamadı: {modelPath}");
            Console.WriteLine("Lütfen Vosk Türkçe modelini indirin ve 'model' klasörüne koyun.");
            return;
        }

        Vosk.Vosk.SetLogLevel(0);
        using var model = new Model(modelPath);

        // No grammar -> recognize free-form (all detected words)
        using var recognizer = new VoskRecognizer(model, 16000.0f);

        // Commands to detect (canonical forms)
        var commands = new[] { "başla", "başlat", "kapa", "kapat", "yazdır", "iptal", "yeniden dene", "kart ver", "para yükle", "bakiye kontrol" };

        var waveIn = new WaveInEvent
        {
            DeviceNumber = 0,
            WaveFormat = new WaveFormat(16000, 16, 1)
        };

        waveIn.DataAvailable += (s, e) =>
        {
            try
            {
                // Feed audio to recognizer
                if (recognizer.AcceptWaveform(e.Buffer, e.BytesRecorded))
                {
                    var resultJson = recognizer.Result();
                    var text = PrintJsonResult("Final", resultJson);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var cmd = DetectCommand(text, commands);
                        if (cmd != null)
                        {
                            Console.WriteLine($"Detected command: {cmd}");
                        }
                    }
                }
                else
                {
                    var partialJson = recognizer.PartialResult();
                    PrintJsonResult("Partial", partialJson);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DataAvailable handler error: {ex}");
            }
        };

        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("Stopping...");
            waveIn.StopRecording();
            StopEvent.Set();
        };

        waveIn.StartRecording();
        Console.WriteLine("Dinleniyor (tüm kelimeler algılanır). Kapatmak için Ctrl+C basın.");
        StopEvent.Wait();
        waveIn.Dispose();
    }

    static string PrintJsonResult(string kind, string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        Console.WriteLine($"[{kind}] {json}");

        // Try to extract the textual transcription (Vosk returns {"text":"..."}).
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("text", out var text))
            {
                var t = text.GetString();
                if (!string.IsNullOrWhiteSpace(t))
                {
                    Console.WriteLine($"Recognized text: {t}");
                    return t;
                }
            }
        }
        catch
        {
            // ignore parse errors
        }
        return null;
    }

    // Full free-form recognition in a separate function.
    // This does not change Main; call this manually for a dedicated full-recognition test.
    public static void FullRecognize()
    {
        Console.WriteLine("Starting full recognition session...");
        var modelPath = Path.Combine(AppContext.BaseDirectory, "model");
        if (!Directory.Exists(modelPath))
        {
            Console.WriteLine($"Model folder not found: {modelPath}");
            return;
        }

        try
        {
            Vosk.Vosk.SetLogLevel(0);
            using var model = new Model(modelPath);
            using var recognizer = new VoskRecognizer(model, 16000.0f);

            using var waveIn = new WaveInEvent
            {
                DeviceNumber = 0,
                WaveFormat = new WaveFormat(16000, 16, 1)
            };

            waveIn.DataAvailable += (s, e) =>
            {
                try
                {
                    if (recognizer.AcceptWaveform(e.Buffer, e.BytesRecorded))
                    {
                        var json = recognizer.Result();
                        PrintJsonResult("FullFinal", json);
                    }
                    else
                    {
                        var json = recognizer.PartialResult();
                        PrintJsonResult("FullPartial", json);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FullRecognize DataAvailable error: {ex}");
                }
            };

            Console.WriteLine("Full recognition: listening for all words. Press Ctrl+C to stop.");
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("Stopping full recognition...");
                waveIn.StopRecording();
                StopEvent.Set();
            };

            StopEvent.Reset();
            waveIn.StartRecording();
            StopEvent.Wait();
            waveIn.StopRecording();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FullRecognize error: {ex}");
        }
    }

    static string DetectCommand(string recognizedText, string[] commands)
    {
        if (string.IsNullOrWhiteSpace(recognizedText)) return null;

        // Globalization-invariant mode doesn't support tr-TR. Use invariant lowercasing
        // but apply Turkish-specific character replacements first so dotted/dotless I are handled.
        static string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            // Handle Turkish dotted/dotless I explicitly: 'İ'->'i', 'I'->'ı'
            s = s.Replace('\u0130', 'i'); // 'İ' -> 'i'
            s = s.Replace('I', '\u0131'); // 'I' -> 'ı'
            // Normalize whitespace and lowercase invariant
            return s.Trim().ToLowerInvariant();
        }

        var norm = Normalize(recognizedText);

        // Exact or starts-with matches
        foreach (var c in commands)
        {
            var cn = Normalize(c);
            if (norm == cn || norm.StartsWith(cn + " ") || norm.StartsWith(cn + ",") || norm.StartsWith(cn + "."))
                return c;
        }

        // Fallback: simple contains
        foreach (var c in commands)
        {
            if (norm.Contains(Normalize(c))) return c;
        }

        return null;
    }
}