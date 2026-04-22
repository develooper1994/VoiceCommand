using System;
using System.Collections.Generic;
using System.Text.Json;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;

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

        // --- Real-time (chunked) microphone transcription helpers ---
        private static WaveInEvent _waveInRealtime;
        private static MemoryStream _bufferRealtime;
        private static readonly object _bufferLock = new object();
        private static int _bytesPerChunk = 16000 * 2; // default 1s worth (will be overwritten)
        private static bool _realtimeRunning = false;

        // Start real-time (chunked) transcription. onTranscribed is invoked when each chunk is processed.
        public static void StartRealtimeTranscription(string modelDir, Action<string> onTranscribed, int chunkMs = 1000, int deviceNumber = 0)
        {
            if (_realtimeRunning) return;
            _realtimeRunning = true;
            _bufferRealtime = new MemoryStream();
            // bytes per millisecond = sampleRate * channels * bytesPerSample / 1000 = 16000*1*2/1000 = 32
            _bytesPerChunk = 32 * Math.Max(100, chunkMs);

            _waveInRealtime = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                WaveFormat = new WaveFormat(16000, 16, 1)
            };

            _waveInRealtime.DataAvailable += (s, e) =>
            {
                try
                {
                    lock (_bufferLock)
                    {
                        _bufferRealtime.Write(e.Buffer, 0, e.BytesRecorded);
                        while (_bufferRealtime.Length >= _bytesPerChunk)
                        {
                            // extract chunk
                            var chunk = new byte[_bytesPerChunk];
                            _bufferRealtime.Position = 0;
                            _bufferRealtime.Read(chunk, 0, chunk.Length);
                            // copy remaining to new stream
                            var remaining = new MemoryStream();
                            var remBuffer = new byte[_bufferRealtime.Length - _bytesPerChunk];
                            _bufferRealtime.Read(remBuffer, 0, remBuffer.Length);
                            remaining.Write(remBuffer, 0, remBuffer.Length);
                            _bufferRealtime.Dispose();
                            _bufferRealtime = remaining;

                            // write chunk to temp WAV and process async
                            var tmp = Path.Combine(Path.GetTempPath(), $"whisper_chunk_{Guid.NewGuid()}.wav");
                            using (var w = new WaveFileWriter(tmp, _waveInRealtime.WaveFormat))
                            {
                                w.Write(chunk, 0, chunk.Length);
                            }

                            // process in background
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var txt = await TranscribeFileAsync(modelDir, tmp).ConfigureAwait(false);
                                    onTranscribed?.Invoke(txt ?? string.Empty);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Realtime whisper chunk error: {ex.Message}");
                                }
                                finally
                                {
                                    try { File.Delete(tmp); } catch { }
                                }
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Realtime DataAvailable error: {ex.Message}");
                }
            };

            _waveInRealtime.RecordingStopped += (s, e) => { /* noop */ };
            _waveInRealtime.StartRecording();
        }

        public static void StopRealtimeTranscription()
        {
            if (!_realtimeRunning) return;
            try
            {
                _waveInRealtime?.StopRecording();
            }
            catch { }
            try { _waveInRealtime?.Dispose(); } catch { }
            _waveInRealtime = null;
            lock (_bufferLock)
            {
                try { _bufferRealtime?.Dispose(); } catch { }
                _bufferRealtime = null;
            }
            _realtimeRunning = false;
        }

        // Transcribe a WAV file using Whisper.net offline GGML model located in modelDir.
        // This is async because Whisper.net exposes async processing.
        public static async System.Threading.Tasks.Task<string> TranscribeFileAsync(string modelDir, string wavPath)
        {
            if (!System.IO.File.Exists(wavPath)) throw new System.IO.FileNotFoundException("Audio file not found", wavPath);

            // find a model file inside modelDir (common ggml/bin extensions)
            var modelFiles = System.IO.Directory.GetFiles(modelDir, "*.bin");
            if (modelFiles.Length == 0) modelFiles = System.IO.Directory.GetFiles(modelDir, "*.ggml*");
            if (modelFiles.Length == 0) throw new System.IO.FileNotFoundException("No Whisper ggml model file found in " + modelDir);
            var modelPath = modelFiles[0];

            // Use Whisper.net factory to create processor
            try
            {
                // WhisperFactory is in the Whisper namespace (Whisper.net package)
                // ensure the Whisper.net runtime package is referenced in the project
                using var factory = Whisper.net.WhisperFactory.FromPath(modelPath);
                using var processor = factory.CreateBuilder()
                    .WithLanguage("tr")
                    .Build();

                using var fs = System.IO.File.OpenRead(wavPath);
                var sb = new System.Text.StringBuilder();
                await foreach (var result in processor.ProcessAsync(fs))
                {
                    if (!string.IsNullOrWhiteSpace(result.Text))
                    {
                        sb.Append(result.Text);
                        sb.Append(' ');
                    }
                }

                var text = sb.ToString().Trim();
                return text;
            }
            catch (Exception ex)
            {
                // surface the error as empty result
                Console.WriteLine($"Whisper transcribe error: {ex.Message}");
                return string.Empty;
            }
        }
    }
}
