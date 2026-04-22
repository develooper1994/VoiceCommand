using System.Text.Json;
using NAudio.Wave;
using System.Runtime.InteropServices;
using System.Diagnostics;

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

        public static async System.Threading.Tasks.Task TranscribeFileStreamAsync(string modelDir, string wavPath, Action<string> onSegment)
        {
            if (!System.IO.File.Exists(wavPath)) throw new System.IO.FileNotFoundException("Audio file not found", wavPath);

            var modelFiles = System.IO.Directory.GetFiles(modelDir, "*.bin");
            if (modelFiles.Length == 0) modelFiles = System.IO.Directory.GetFiles(modelDir, "*.ggml*");
            if (modelFiles.Length == 0) throw new System.IO.FileNotFoundException("No Whisper ggml model file found in " + modelDir);
            var modelPath = modelFiles[0];

            try
            {
                var fi = new FileInfo(modelPath);
                var factory = GetOrCreateFactory(modelPath);
                Interlocked.Increment(ref _activeProcessorCount);
                try
                {
                    using var processor = factory.CreateBuilder()
                        .WithLanguage("tr")
                        .Build();

                    using var fs = System.IO.File.OpenRead(wavPath);
                    await foreach (var result in processor.ProcessAsync(fs))
                    {
                        if (!string.IsNullOrWhiteSpace(result.Text))
                        {
                            try
                            {
                                onSegment?.Invoke(result.Text);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"onSegment callback error: {ex.Message}");
                            }
                        }
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _activeProcessorCount);
                }
            }
            catch (Exception ex)
            {
                var msg = ex.Message ?? string.Empty;
                Console.WriteLine($"Whisper transcribe stream error: {msg}");
                if (!_nativeRuntimeProblemReported && (ex is DllNotFoundException || msg.Contains("Native Library not found", StringComparison.OrdinalIgnoreCase) || msg.Contains("could not load", StringComparison.OrdinalIgnoreCase) || msg.Contains("BadImageFormatException", StringComparison.OrdinalIgnoreCase)))
                {
                    _nativeRuntimeProblemReported = true;
                    Console.WriteLine();
                    Console.WriteLine("Whisper native runtime/library not found or not loadable.");
                    Console.WriteLine("Recommended fixes:");
                    Console.WriteLine("  - Add the official native runtime package to the project: \"dotnet add package Whisper.net.Runtime\"");
                    Console.WriteLine("    NuGet: https://www.nuget.org/packages/Whisper.net.Runtime");
                    Console.WriteLine("  - Or install the native whisper.cpp libraries for your platform and ensure they are discoverable at runtime.");
                    Console.WriteLine("    Repo: https://github.com/ggerganov/whisper.cpp");
                    Console.WriteLine("  - Ensure the native library architecture (x64/x86/arm) matches your .NET runtime architecture.");
                    try
                    {
                        Console.WriteLine($"    Process is 64-bit: {Environment.Is64BitProcess}");
                        Console.WriteLine($"    OS Arch: {RuntimeInformation.OSArchitecture}");
                        Console.WriteLine($"    Process Arch: {RuntimeInformation.ProcessArchitecture}");
                    }
                    catch { }
                    Console.WriteLine();
                    Console.WriteLine("After installing the runtime, re-run the app. Realtime transcription will be stopped to avoid repeated errors.");
                    try { StopRealtimeTranscription(); } catch { }
                }

                return;
            }
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
        private static WaveInEvent? _waveInRealtime;
        private static MemoryStream? _bufferRealtime;
        private static readonly object _bufferLock = new object();
        private static int _bytesPerChunk = 16000 * 2; // default 1s worth (will be overwritten)
        private static bool _realtimeRunning = false;
        // Semaphore to limit concurrent chunk processing to avoid excessive native memory/CPU pressure
        private static System.Threading.SemaphoreSlim? _processingSemaphore = null;
            // Whether we've already reported a native runtime/library problem to avoid spamming
            private static bool _nativeRuntimeProblemReported = false;

            // Cached Whisper factory and synchronization primitives to avoid recreating the heavy model per chunk.
            private static Whisper.net.WhisperFactory? _cachedFactory;
            private static readonly object _factoryLock = new();
            // Count of active processors (created from the cached factory) to allow graceful disposal.
            private static int _activeProcessorCount = 0;

            // Validate that a plausible ggml model file exists in the specified directory.
            // Returns true and the resolved model path when the file appears acceptable.
            private static bool TryGetValidModelPath(string modelDir, out string? modelPath, long minSizeBytes = 1 * 1024 * 1024)
            {
                modelPath = null;
                try
                {
                    if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir))
                    {
                        Console.WriteLine($"Whisper model directory not found: {modelDir}");
                        return false;
                    }

                    var modelFiles = Directory.GetFiles(modelDir, "*.bin");
                    if (modelFiles.Length == 0) modelFiles = Directory.GetFiles(modelDir, "*.ggml*");
                    if (modelFiles.Length == 0)
                    {
                        Console.WriteLine($"No Whisper ggml model file found in {modelDir}");
                        return false;
                    }

                    var candidate = modelFiles[0];
                    var fi = new FileInfo(candidate);
                    Console.WriteLine($"Whisper model candidate: {candidate} ({fi.Length} bytes)");
                    if (fi.Length < minSizeBytes)
                    {
                        Console.WriteLine($"Whisper model file appears too small ({fi.Length} bytes). Try re-downloading with '--force'.");
                        return false;
                    }

                    modelPath = candidate;
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error while validating Whisper model: {ex.Message}");
                    return false;
                }
            }

            // Helper: create or return a cached WhisperFactory for the provided model path.
            private static Whisper.net.WhisperFactory GetOrCreateFactory(string modelPath)
            {
                        lock (_factoryLock)
                        {
                            if (_cachedFactory != null) return _cachedFactory;
                            var start = DateTime.UtcNow;
                            try
                            {
                                var fi = new FileInfo(modelPath);
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Creating Whisper factory from: {modelPath} ({fi.Length} bytes)");
                                try
                                {
                                    Console.WriteLine($"  Process is 64-bit: {Environment.Is64BitProcess}");
                                }
                                catch { }
                                try
                                {
                                    Console.WriteLine($"  Process private memory: {Process.GetCurrentProcess().PrivateMemorySize64} bytes");
                                }
                                catch { }
                                try
                                {
                                    Console.WriteLine($"  GC total available memory (approx): {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes} bytes");
                                }
                                catch { }

                                _cachedFactory = Whisper.net.WhisperFactory.FromPath(modelPath);
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Whisper factory created and cached (took {(DateTime.UtcNow - start).TotalSeconds:F2}s)");
                                return _cachedFactory;
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Whisper factory creation failed: {ex.Message}");
                                // Attempt a fallback: copy the model to a short temp path and retry, which can
                                // avoid issues with long paths, locking, or platform-specific mmap problems.
                                try
                                {
                                    var tmpName = Path.GetFileName(modelPath);
                                    var tmpPath = Path.Combine(Path.GetTempPath(), tmpName);
                                    Console.WriteLine($"Attempting fallback: copy model to temp path {tmpPath} and retry...");
                                    File.Copy(modelPath, tmpPath, true);
                                    var start2 = DateTime.UtcNow;
                                    _cachedFactory = Whisper.net.WhisperFactory.FromPath(tmpPath);
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Whisper factory created from temp and cached (took {(DateTime.UtcNow - start2).TotalSeconds:F2}s)");
                                    return _cachedFactory;
                                }
                                catch (Exception ex2)
                                {
                                    Console.WriteLine($"Fallback factory creation also failed: {ex2.Message}");
                                    throw;
                                }
                            }
                        }
            }

        // Start real-time (chunked) transcription. onTranscribed is invoked when each chunk is processed.
        public static void StartRealtimeTranscription(string modelDir, Action<string> onTranscribed, int chunkMs = 1000, int deviceNumber = 0)
        {
            if (_realtimeRunning) return;

            // quick validation to avoid starting realtime when the model is missing
            // or appears to be a partial/corrupt download (prevents native ggml asserts)
            if (!TryGetValidModelPath(modelDir, out var validatedModelPath))
            {
                Console.WriteLine("Aborting realtime transcription due to invalid/absent Whisper model.");
                return;
            }

            _realtimeRunning = true;
            _bufferRealtime = new MemoryStream();
            // bytes per millisecond = sampleRate * channels * bytesPerSample / 1000 = 16000*1*2/1000 = 32
            _bytesPerChunk = 32 * Math.Max(100, chunkMs);

            // Choose concurrency based on model size: large models are memory/CPU intensive,
            // prefer single-threaded chunk processing to avoid native allocation races/pressure.
            try
            {
                int concurrency = 2;
                try
                {
                    var fi = new FileInfo(validatedModelPath);
                    // conservative thresholds
                    if (fi.Length > 400L * 1024 * 1024) concurrency = 1; // >400MB -> 1
                    else if (fi.Length > 150L * 1024 * 1024) concurrency = 1; // >150MB -> 1
                    else concurrency = 2;
                }
                catch { concurrency = 1; }
                try { _processingSemaphore?.Dispose(); } catch { }
                _processingSemaphore = new System.Threading.SemaphoreSlim(concurrency, concurrency);
                Console.WriteLine($"Realtime chunk processing concurrency set to {_processingSemaphore.CurrentCount} (max {concurrency})");
            }
            catch { }

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

                            // process in background with concurrency limit to avoid overwhelming native runtime
                            _ = Task.Run(async () =>
                            {
                                var sem = _processingSemaphore;
                                if (sem != null)
                                {
                                    await sem.WaitAsync().ConfigureAwait(false);
                                }
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
                                    try { sem?.Release(); } catch { }
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
            // Dispose cached factory after allowing in-flight processors to finish (short timeout).
            lock (_factoryLock)
            {
                if (_cachedFactory != null)
                {
                    var sw = Stopwatch.StartNew();
                    while (Volatile.Read(ref _activeProcessorCount) > 0 && sw.Elapsed.TotalSeconds < 5)
                    {
                        Thread.Sleep(50);
                    }
                    try { _cachedFactory.Dispose(); } catch { }
                    _cachedFactory = null;
                }
            }

            // Dispose processing semaphore
            try { _processingSemaphore?.Dispose(); } catch { }
            _processingSemaphore = null;
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

            // Use Whisper.net factory to create processor. Log the chosen model path and size
            try
            {
                var fi = new FileInfo(modelPath);
                var startLoad = DateTime.UtcNow;

                var factory = GetOrCreateFactory(modelPath);
                Interlocked.Increment(ref _activeProcessorCount);
                try
                {
                    var startBuild = DateTime.UtcNow;
                    using var processor = factory.CreateBuilder()
                        .WithLanguage("tr")
                        .Build();
                    var afterBuild = DateTime.UtcNow;
                    var buildSeconds = (afterBuild - startBuild).TotalSeconds;
                    if (buildSeconds > 0.05)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Whisper processor built (took {buildSeconds:F2}s)");
                    }

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
                finally
                {
                    Interlocked.Decrement(ref _activeProcessorCount);
                }
            }
            catch (Exception ex)
            {
                // Detect common native runtime errors and print actionable instructions once
                var msg = ex.Message ?? string.Empty;
                Console.WriteLine($"Whisper transcribe error: {msg}");
                if (!_nativeRuntimeProblemReported && (ex is DllNotFoundException || msg.Contains("Native Library not found", StringComparison.OrdinalIgnoreCase) || msg.Contains("could not load", StringComparison.OrdinalIgnoreCase) || msg.Contains("BadImageFormatException", StringComparison.OrdinalIgnoreCase)))
                {
                    _nativeRuntimeProblemReported = true;
                    Console.WriteLine();
                    Console.WriteLine("Whisper native runtime/library not found or not loadable.");
                    Console.WriteLine("Recommended fixes:");
                    Console.WriteLine("  - Add the official native runtime package to the project: \"dotnet add package Whisper.net.Runtime\"");
                    Console.WriteLine("    NuGet: https://www.nuget.org/packages/Whisper.net.Runtime");
                    Console.WriteLine("  - Or install the native whisper.cpp libraries for your platform and ensure they are discoverable at runtime.");
                    Console.WriteLine("    Repo: https://github.com/ggerganov/whisper.cpp");
                    Console.WriteLine("  - Ensure the native library architecture (x64/x86/arm) matches your .NET runtime architecture.");
                    try
                    {
                        Console.WriteLine($"    Process is 64-bit: {Environment.Is64BitProcess}");
                        Console.WriteLine($"    OS Arch: {RuntimeInformation.OSArchitecture}");
                        Console.WriteLine($"    Process Arch: {RuntimeInformation.ProcessArchitecture}");
                    }
                    catch { }
                    Console.WriteLine();
                    Console.WriteLine("After installing the runtime, re-run the app. Realtime transcription will be stopped to avoid repeated errors.");
                    try { StopRealtimeTranscription(); } catch { }
                }

                return string.Empty;
            }
        }
    }
}
