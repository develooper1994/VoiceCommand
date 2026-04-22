using NAudio.Wave;
using System.Runtime.InteropServices;
using Vosk;
using VoiceCommand.CommandDetection;
using System.Threading.Tasks;
using VoiceCommand.Model;

class Program
{
    static readonly ManualResetEventSlim StopEvent = new(false);
    // Shared canonical commands list (provided by selected command detection backend)
    static readonly string[] Commands = VoiceCommand.CommandDetection.CommandDetector.Commands;
    // Prevent printing the same command repeatedly
    static string? _lastPrintedCommand = null;
    static DateTime _lastPrintedTime = DateTime.MinValue;
    static readonly object _printLock = new();
    // Minimum milliseconds between repeated prints of the same command
    const int RepeatSuppressMs = 2000;
    // Recording shutdown coordination used by modes to avoid native crashes
    static readonly ManualResetEventSlim _recordingStopped = new(false);
    static volatile bool _stopping = false;
    // Recognizer resources and lock for safe P/Invoke access
    static Vosk.Model? _modelInstance = null;
    static VoskRecognizer? _recognizerInstance = null;
    static WaveInEvent? _waveInInstance = null;
    static readonly object _recognizerLock = new();

    static async Task Main(string[] args)
    {
        // Parse arguments: allow backend selection and input mode
        var backendArg = "vosk"; // default
        var inputMode = "mic"; // mic or file
        string? inputFile = null;
        var autoInstall = false;
        var forceInstall = false;
        string? modelUrl = null;
        string? modelDirOverride = null;
        string? modelSize = null;
        var listModels = false;
        var backendSpecified = false;
        var modelUrlSpecified = false;
        var modelSizeSpecified = false;
        string? whisperNativePath = null;
        var whisperAccel = "auto"; // values: auto, cpu, cuda, vulkan
        var whisperAccelSpecified = false;

        if (args == null || args.Length == 0)
        {
            PrintHelp();
            return;
        }

        // Helper: generate a short silent WAV for testing: `--gen-test-wav`
        for (int ai = 0; ai < args.Length; ai++)
        {
            if (args[ai].Equals("--gen-test-wav", StringComparison.OrdinalIgnoreCase))
            {
                var wav = CreateTestWav(1);
                Console.WriteLine($"Generated test WAV: {wav}");
                return;
            }
        }

        string? runCommand = null;
        if (args != null)
        {
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.Equals("-h", StringComparison.OrdinalIgnoreCase) || a.Equals("--help", StringComparison.OrdinalIgnoreCase))
                {
                    PrintHelp();
                    return;
                }
                else if (a.Equals("--backend", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    backendArg = args[++i];
                }
                else if (a.Equals("--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    inputMode = args[++i];
                }
                else if (a.Equals("--file", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    inputFile = args[++i];
                }
                else if (a.Equals("full", StringComparison.OrdinalIgnoreCase))
                {
                    runCommand = "full";
                }
                else if (a.Equals("partial", StringComparison.OrdinalIgnoreCase))
                {
                    runCommand = "partial";
                }
                else if (a.Equals("detect", StringComparison.OrdinalIgnoreCase))
                {
                    runCommand = "detect";
                }
                else if (a.Equals("--download-models", StringComparison.OrdinalIgnoreCase) || a.Equals("--auto-install", StringComparison.OrdinalIgnoreCase) || a.Equals("--autoinstall", StringComparison.OrdinalIgnoreCase))
                {
                    autoInstall = true;
                }
                else if (a.Equals("--model-size", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    modelSize = args[++i];
                    modelSizeSpecified = true;
                }
                else if (a.Equals("--force", StringComparison.OrdinalIgnoreCase))
                {
                    forceInstall = true;
                }
                else if (a.Equals("--model-url", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    modelUrl = args[++i];
                    modelUrlSpecified = true;
                }
                else if (a.Equals("--model-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    modelDirOverride = args[++i];
                }
                else if (a.Equals("--whisper-native-path", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    whisperNativePath = args[++i];
                }
                else if (a.Equals("--whisper-accel", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    whisperAccel = args[++i];
                    whisperAccelSpecified = true;
                }
                else if (a.Equals("--list-models", StringComparison.OrdinalIgnoreCase))
                {
                    listModels = true;
                }
                else if (a.Equals("--model", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    backendArg = args[++i];
                    backendSpecified = true;
                }
                else if (a.Equals("--backend", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    backendArg = args[++i];
                    backendSpecified = true;
                }
            }
        }

        // select backend
        if (!VoiceCommand.CommandDetection.CommandDetector.TrySetBackend(backendArg))
        {
            Console.WriteLine($"Bilinmeyen backend '{backendArg}', varsayılan 'vosk' kullanılacak.");
            VoiceCommand.CommandDetection.CommandDetector.SetBackend(VoiceCommand.CommandDetection.DetectorBackend.Vosk);
        }

        // Validate selected model options against chosen backend to avoid mismatches
        // If model URL or size implies a different backend, either error (if user explicitly chose backend)
        // or auto-switch the backend when the user didn't explicitly set it.
        if (modelUrlSpecified && !string.IsNullOrEmpty(modelUrl))
        {
            var detected = DetectBackendFromUrl(modelUrl!);
            if (detected != null)
            {
                var active = VoiceCommand.CommandDetection.CommandDetector.ActiveBackend;
                if (backendSpecified && active != detected.Value)
                {
                    Console.WriteLine($"Hata: Sağlanan model URL'si {detected} backend'ine işaret ediyor, fakat seçilen backend {active}. Lütfen --model veya --model-url argümanlarını uyumlu hale getirin.");
                    return;
                }
                else if (!backendSpecified)
                {
                    VoiceCommand.CommandDetection.CommandDetector.SetBackend(detected.Value);
                    Console.WriteLine($"Model URL'sine göre backend otomatik olarak '{detected}' seçildi.");
                }
            }
        }

        if (modelSizeSpecified && !string.IsNullOrEmpty(modelSize))
        {
            // model-size applies only to Whisper
            var active = VoiceCommand.CommandDetection.CommandDetector.ActiveBackend;
            if (backendSpecified && active == VoiceCommand.CommandDetection.DetectorBackend.Vosk)
            {
                Console.WriteLine("Hata: --model-size yalnızca Whisper backend'i için geçerlidir. Lütfen backend olarak 'whisper' seçin veya --model-size kullanmayın.");
                return;
            }
            else if (!backendSpecified && active != VoiceCommand.CommandDetection.DetectorBackend.Whisper)
            {
                VoiceCommand.CommandDetection.CommandDetector.SetBackend(VoiceCommand.CommandDetection.DetectorBackend.Whisper);
                Console.WriteLine("--model-size kullanıldığı için backend otomatik olarak 'whisper' olarak ayarlandı.");
            }
        }

        // If user only wants to list available models, print them and exit.
        if (listModels)
        {
            PrintAvailableModels();
            return;
        }

        Console.WriteLine("Sesli komut dinleyici başlatılıyor...");
        // Show possible commands at startup
        Console.WriteLine("Algılanabilecek komutlar: " + string.Join(", ", Commands));
        // Print available audio input devices
        PrintInputDevices();

    static void PrintInputDevices()
    {
        try
        {
            Console.WriteLine("Available input devices:");
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var caps = WaveInEvent.GetCapabilities(i);
                Console.WriteLine($"  {i}: {caps.ProductName}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not enumerate input devices: {ex.Message}");
        }
    }

    static void PrintRuntimeDiagnostics()
    {
        try
        {
            Console.WriteLine();
            Console.WriteLine("Runtime diagnostics:");
            Console.WriteLine($"  Process is 64-bit: {Environment.Is64BitProcess}");
            Console.WriteLine($"  OS Architecture: {RuntimeInformation.OSArchitecture}");
            Console.WriteLine($"  Process Architecture: {RuntimeInformation.ProcessArchitecture}");
            Console.WriteLine($"  Framework: {RuntimeInformation.FrameworkDescription}");
            Console.WriteLine($"  OS: {RuntimeInformation.OSDescription}");
            Console.WriteLine();
        }
        catch { }
    }

    static void PrintAvailableModels()
    {
        Console.WriteLine("Available models:");
        Console.WriteLine();
        Console.WriteLine("Vosk models (fallback page): https://alphacephei.com/vosk/models");
        Console.WriteLine("  tr-tr                 - https://alphacephei.com/vosk/models/vosk-model-small-tr-0.3.zip");
        Console.WriteLine("  en-us                 - https://alphacephei.com/vosk/models/vosk-model-en-us-0.22.zip");
        Console.WriteLine("  small-en-us-0.15      - https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip");
        Console.WriteLine("  en-us-0.22-lgraph     - https://alphacephei.com/vosk/models/vosk-model-en-us-0.22-lgraph.zip");
        Console.WriteLine("  en-us-0.42-gigaspeech - https://alphacephei.com/vosk/models/vosk-model-en-us-0.42-gigaspeech.zip");
        Console.WriteLine("  small-cn-0.22         - https://alphacephei.com/vosk/models/vosk-model-small-cn-0.22.zip");
        Console.WriteLine();
        Console.WriteLine("Whisper ggml models (select size with --model-size):");
        Console.WriteLine("  tiny   - https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin");
        Console.WriteLine("  base   - https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin");
        Console.WriteLine("  small  - https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin");
        Console.WriteLine("  medium - https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin");
        Console.WriteLine("  large  - https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin");
        Console.WriteLine();
        Console.WriteLine("Example: dotnet run --project VoiceCommand -- --download-models --model whisper --model-size tiny");
    }

    static VoiceCommand.CommandDetection.DetectorBackend? DetectBackendFromUrl(string url)
    {
        try
        {
            var u = url.ToLowerInvariant();
            if (u.Contains("alphacephei.com/vosk") || u.Contains("vosk-model") || u.EndsWith(".zip"))
                return VoiceCommand.CommandDetection.DetectorBackend.Vosk;
            if (u.Contains("ggml") || u.Contains("whisper.cpp") || u.Contains("ggerganov") || u.EndsWith(".bin") || u.Contains("huggingface.co"))
                return VoiceCommand.CommandDetection.DetectorBackend.Whisper;
        }
        catch { }
        return null;
    }

    // Auto-detect available GPU accel libraries (CUDA / Vulkan) on the host.
    static string? AutoDetectWhisperAccel()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (TryLoadNativeLibrary("nvcuda.dll")) return "cuda";
                if (TryLoadNativeLibrary("vulkan-1.dll")) return "vulkan";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (TryLoadNativeLibrary("libcuda.so.1") || TryLoadNativeLibrary("libcuda.so")) return "cuda";
                if (TryLoadNativeLibrary("libvulkan.so.1") || TryLoadNativeLibrary("libvulkan.so")) return "vulkan";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                if (TryLoadNativeLibrary("libvulkan.1.dylib") || TryLoadNativeLibrary("libvulkan.dylib")) return "vulkan";
            }
        }
        catch { }
        return null;
    }

    static bool TryLoadNativeLibrary(string name)
    {
        try
        {
            if (NativeLibrary.TryLoad(name, out var handle))
            {
                try { NativeLibrary.Free(handle); } catch { }
                return true;
            }
        }
        catch { }
        return false;
    }

        // Run command (detect/full) is handled after ensuring models are available.

        // model directory is organized per-backend: model/vosk and model/whisper
        var baseModelDir = Path.Combine(AppContext.BaseDirectory, "model");
        var backend = VoiceCommand.CommandDetection.CommandDetector.ActiveBackend;
        var modelPath = backend == VoiceCommand.CommandDetection.DetectorBackend.Vosk
            ? Path.Combine(baseModelDir, "vosk")
            : Path.Combine(baseModelDir, "whisper");

        // If a whisper model size was requested, prefer a size-specific subfolder (unless overridden).
        if (backend == VoiceCommand.CommandDetection.DetectorBackend.Whisper && !string.IsNullOrEmpty(modelSize) && string.IsNullOrEmpty(modelDirOverride))
        {
            modelPath = Path.Combine(baseModelDir, "whisper", modelSize.ToLowerInvariant());
        }

        // If user requested a specific whisper size but the size folder is empty and
        // there is a generic model in model/whisper, copy it into the size folder
        // with a canonical filename so the selection matches the parameter.
        if (backend == VoiceCommand.CommandDetection.DetectorBackend.Whisper && !string.IsNullOrEmpty(modelSize) && string.IsNullOrEmpty(modelDirOverride))
        {
            var sizeLower = modelSize.ToLowerInvariant();
            var sizeFolder = Path.Combine(baseModelDir, "whisper", sizeLower);
            try
            {
                var hasSizeFiles = Directory.Exists(sizeFolder) && (Directory.GetFiles(sizeFolder, "*.bin").Length > 0 || Directory.GetFiles(sizeFolder, "*.ggml*").Length > 0);
                if (!hasSizeFiles)
                {
                    var rootFolder = Path.Combine(baseModelDir, "whisper");
                    if (Directory.Exists(rootFolder))
                    {
                        var rootBins = Directory.GetFiles(rootFolder, "*.bin");
                        var rootGgml = Directory.GetFiles(rootFolder, "*.ggml*");
                        string? src = null;
                        if (rootBins.Length > 0) src = rootBins[0];
                        else if (rootGgml.Length > 0) src = rootGgml[0];

                        if (!string.IsNullOrEmpty(src))
                        {
                            // map size -> canonical filename
                            var mapping = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                { "tiny", "ggml-tiny.bin" },
                                { "base", "ggml-base.bin" },
                                { "small", "ggml-small.bin" },
                                { "medium", "ggml-medium.bin" },
                                { "large", "ggml-large-v3.bin" }
                            };

                            var canonical = mapping.ContainsKey(sizeLower) ? mapping[sizeLower] : Path.GetFileName(src);
                            Directory.CreateDirectory(sizeFolder);
                            var dst = Path.Combine(sizeFolder, canonical);
                            try
                            {
                                if (!File.Exists(dst)) File.Copy(src, dst);
                                Console.WriteLine($"Existing generic model copied to {dst} to satisfy --model-size {modelSize}. If this is incorrect, re-run with --download-models --force to fetch the requested size.");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Could not copy existing model into size folder: {ex.Message}");
                            }
                        }
                    }
                }
            }
            catch { }
        }

        // Allow overriding model path from CLI
        if (!string.IsNullOrEmpty(modelDirOverride))
        {
            modelPath = Path.GetFullPath(modelDirOverride);
        }

        // If user requested a whisper model size, map to known URLs
        if (string.IsNullOrEmpty(modelUrl) && backend == VoiceCommand.CommandDetection.DetectorBackend.Whisper && !string.IsNullOrEmpty(modelSize))
        {
            switch (modelSize.ToLowerInvariant())
            {
                case "tiny": modelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin"; break;
                case "base": modelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin"; break;
                case "small": modelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin"; break;
                case "medium": modelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin"; break;
                case "large": modelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin"; break;
                default:
                    Console.WriteLine($"Unknown model size '{modelSize}', using default Whisper model.");
                    break;
            }
        }

        // Ensure model exists or attempt automatic install when requested
        var shouldAutoInstall = autoInstall || !string.IsNullOrEmpty(runCommand);
        if (!autoInstall && !string.IsNullOrEmpty(runCommand))
        {
            Console.WriteLine("Model missing or absent: automatic install will be attempted because a run command was specified.");
        }
        // If the user provided a custom native runtime path for Whisper (e.g., GPU-enabled whisper.cpp builds),
        // prepend it to PATH so native loader can find the GPU-enabled native libraries before the default ones.
        if (!string.IsNullOrEmpty(whisperNativePath))
        {
            try
            {
                var abs = Path.GetFullPath(whisperNativePath);
                if (Directory.Exists(abs))
                {
                    var old = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                    var sep = Path.PathSeparator;
                    Environment.SetEnvironmentVariable("PATH", abs + sep + old);
                    Console.WriteLine($"Added '{abs}' to PATH for native Whisper libraries.");
                }
                else
                {
                    Console.WriteLine($"Whisper native path not found: {abs}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to adjust PATH for native Whisper libs: {ex.Message}");
            }
        }

        // If user requested an acceleration backend (e.g., 'cuda' or 'vulkan'), expose it as an environment variable
        // so native runtimes or helper scripts can pick it up. This is advisory; user must install/compile GPU-enabled native libs.
        try
        {
            // If user didn't explicitly pick accel (or used 'auto'), try to detect installed GPU runtimes
            if (!whisperAccelSpecified || whisperAccel.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var detected = AutoDetectWhisperAccel();
                    if (!string.IsNullOrEmpty(detected))
                    {
                        whisperAccel = detected;
                        Console.WriteLine($"Auto-detected Whisper acceleration: {whisperAccel}");
                    }
                    else
                    {
                        whisperAccel = "cpu";
                        Console.WriteLine("No GPU acceleration detected; falling back to CPU (ggml).");
                    }
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(whisperAccel) && !whisperAccel.Equals("cpu", StringComparison.OrdinalIgnoreCase) && !whisperAccel.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("WHISPER_ACCEL", whisperAccel);
                Console.WriteLine($"Requested Whisper acceleration: {whisperAccel} (set WHISPER_ACCEL env var).\nIf you want GPU support, ensure you have a GPU-enabled whisper.cpp native build and place its DLLs in a folder and pass --whisper-native-path <path>.");
            }
        }
        catch { }

        var ensured = await VoiceCommand.Model.ModelInstaller.EnsureModelAsync(backend, modelPath, shouldAutoInstall, modelUrl, forceInstall);
        if (!ensured)
        {
            Console.WriteLine($"Model not available: {modelPath}");
            // If Whisper was requested and auto-install was requested, try fallback sizes
            if (backend == VoiceCommand.CommandDetection.DetectorBackend.Whisper && autoInstall)
            {
                Console.WriteLine("Attempting fallback Whisper model sizes...");
                var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var fallbacks = new List<string>();
                if (!string.IsNullOrEmpty(modelSize))
                {
                    if (modelSize.Equals("tiny", StringComparison.OrdinalIgnoreCase)) fallbacks.AddRange(new[] { "small", "base" });
                    else if (modelSize.Equals("small", StringComparison.OrdinalIgnoreCase)) fallbacks.Add("base");
                    else if (modelSize.Equals("base", StringComparison.OrdinalIgnoreCase)) fallbacks.Add("small");
                }
                // ensure some defaults if nothing specified
                if (fallbacks.Count == 0) fallbacks.AddRange(new[] { "small", "base", "tiny" });

                foreach (var fs in fallbacks)
                {
                    if (tried.Contains(fs)) continue;
                    tried.Add(fs);
                    string? fbUrl = fs.ToLowerInvariant() switch
                    {
                        "tiny" => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin",
                        "base" => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
                        "small" => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin",
                        "medium" => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin",
                        "large" => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin",
                        _ => null
                    };
                    if (string.IsNullOrEmpty(fbUrl)) continue;
                    Console.WriteLine($"Trying fallback size '{fs}' -> {fbUrl}");
                    var destForFallback = Path.Combine(baseModelDir, "whisper", fs.ToLowerInvariant());
                    var ok = await VoiceCommand.Model.ModelInstaller.EnsureModelAsync(backend, destForFallback, true, fbUrl, forceInstall);
                    if (ok)
                    {
                        Console.WriteLine($"Fallback model '{fs}' installed successfully.");
                        ensured = true;
                        break;
                    }
                    else
                    {
                        Console.WriteLine($"Fallback model '{fs}' failed to install.");
                    }
                }
            }

            if (!ensured)
            {
                // Print helpful runtime diagnostics for native runtime issues
                PrintRuntimeDiagnostics();
                return;
            }
        }

        if (backend == VoiceCommand.CommandDetection.DetectorBackend.Vosk)
        {
            Vosk.Vosk.SetLogLevel(0);
            // create shared instances and assign to class fields so we can synchronize disposal
            _modelInstance = new Vosk.Model(modelPath);
            _recognizerInstance = new VoskRecognizer(_modelInstance, 16000.0f);
        }

        // If user requested a run command, handle it now. If no run command specified
        // (e.g., user only asked to download models), exit after install to avoid
        // starting realtime work unexpectedly.
        if (runCommand == "detect")
        {
            if (backend == VoiceCommand.CommandDetection.DetectorBackend.Vosk)
            {
                if (inputMode.Equals("file", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(inputFile))
                {
                    Console.WriteLine($"Vosk: transcribing file {inputFile} ...");
                    try
                    {
                        var txt = VoiceCommand.CommandDetection.CommandDetectorVosk.TranscribeFile(modelPath, inputFile);
                        Console.WriteLine($"[Vosk file] {txt}");
                        var cmd = DetectCommand(txt, Commands);
                        if (cmd != null) Console.WriteLine(cmd);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Vosk file transcription error: {ex.Message}");
                    }
                    return;
                }

                DetectOnly();
                return;
            }
            else if (backend == VoiceCommand.CommandDetection.DetectorBackend.Whisper && inputMode.Equals("mic", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Starting Whisper realtime transcription...");
                // Callback invoked per chunk; detect canonical commands and print
                VoiceCommand.CommandDetection.CommandDetectorWhisper.StartRealtimeTranscription(modelPath, (text) =>
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(text)) return;
                        // In detect mode we only print canonical commands (like Vosk).
                        var cmd = DetectCommand(text, Commands);
                        if (cmd != null) Console.WriteLine(cmd);
                    }
                    catch { }
                });

                Console.WriteLine("Dinleniyor (Whisper realtime). Kapatmak için Ctrl+C basın.");
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    Console.WriteLine("Stopping...");
                    VoiceCommand.CommandDetection.CommandDetectorWhisper.StopRealtimeTranscription();
                    StopEvent.Set();
                };

                StopEvent.Reset();
                StopEvent.Wait();
                return;
            }
            else if (backend == VoiceCommand.CommandDetection.DetectorBackend.Whisper && inputMode.Equals("file", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(inputFile))
            {
                Console.WriteLine($"Whisper: transcribing file {inputFile} ...");
                try
                {
                    var txt = await VoiceCommand.CommandDetection.CommandDetectorWhisper.TranscribeFileAsync(modelPath, inputFile);
                    // Only print canonical commands for detect mode
                    var cmd = DetectCommand(txt, Commands);
                    if (cmd != null) Console.WriteLine(cmd);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Whisper file transcription error: {ex.Message}");
                }
                return;
            }
        }
        else if (runCommand == "partial")
        {
            // Partial mode: print intermediate partial transcripts and final canonical commands
            if (backend == VoiceCommand.CommandDetection.DetectorBackend.Vosk)
            {
                // File input: stream file through recognizer and print partials/finals
                if (inputMode.Equals("file", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(inputFile))
                {
                    Console.WriteLine($"Vosk: streaming file (partial) {inputFile} ...");
                    try
                    {
                        using var model = new Vosk.Model(modelPath);
                        using var recognizer = new VoskRecognizer(model, 16000.0f);

                        using var reader = new NAudio.Wave.WaveFileReader(inputFile);
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
                        string? lastPartial = null;
                        while ((read = waveProvider.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (recognizer.AcceptWaveform(buffer, read))
                            {
                                var finalJson = recognizer.Result();
                                var finalWords = VoiceCommand.CommandDetection.CommandDetectorVosk.ExtractFinalWords(finalJson);
                                if (finalWords.Length > 0)
                                {
                                    var finalText = string.Join(' ', finalWords);
                                    Console.WriteLine($"[Final] {finalText}");
                                    var cmd = DetectCommand(finalText, Commands);
                                    if (cmd != null) Console.WriteLine(cmd);
                                }
                            }
                            else
                            {
                                var partialJson = recognizer.PartialResult();
                                var parts = VoiceCommand.CommandDetection.CommandDetectorVosk.ExtractWordsFromResult(partialJson);
                                if (parts.Length > 0)
                                {
                                    var partText = string.Join(' ', parts);
                                    if (partText != lastPartial)
                                    {
                                        Console.WriteLine($"[Partial] {partText}");
                                        lastPartial = partText;
                                    }
                                }
                            }
                        }

                        try
                        {
                            var final = recognizer.FinalResult();
                            if (!string.IsNullOrWhiteSpace(final))
                            {
                                var fw = VoiceCommand.CommandDetection.CommandDetectorVosk.ExtractWordsFromResult(final);
                                var finalText2 = string.Join(' ', fw);
                                Console.WriteLine($"[Final] {finalText2}");
                                var cmd2 = DetectCommand(finalText2, Commands);
                                if (cmd2 != null) Console.WriteLine(cmd2);
                            }
                        }
                        catch { }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Vosk partial file error: {ex.Message}");
                    }
                    return;
                }

                // Mic input: reuse shared recognizer instance and print partials/finals as they arrive
                var waveInPartial = new WaveInEvent
                {
                    DeviceNumber = 0,
                    WaveFormat = new WaveFormat(16000, 16, 1)
                };

                waveInPartial.RecordingStopped += (s, e) => _recordingStopped.Set();

                waveInPartial.DataAvailable += (s, e) =>
                {
                    try
                    {
                        if (_stopping) return;
                        bool accepted;
                        lock (_recognizerLock)
                        {
                            if (_stopping || _recognizerInstance == null) return;
                            accepted = _recognizerInstance.AcceptWaveform(e.Buffer, e.BytesRecorded);
                        }

                        if (accepted)
                        {
                            string resultJson;
                            lock (_recognizerLock)
                            {
                                if (_recognizerInstance == null) return;
                                resultJson = _recognizerInstance.Result();
                            }
                            var finalWords = VoiceCommand.CommandDetection.CommandDetectorVosk.ExtractFinalWords(resultJson);
                            if (finalWords.Length > 0)
                            {
                                var finalText = string.Join(' ', finalWords);
                                Console.WriteLine($"[Final] {finalText}");
                                var cmd = DetectCommand(finalText, Commands);
                                if (cmd != null) Console.WriteLine(cmd);
                            }
                        }
                        else
                        {
                            string partialJson;
                            lock (_recognizerLock)
                            {
                                if (_recognizerInstance == null) return;
                                partialJson = _recognizerInstance.PartialResult();
                            }
                            var parts = VoiceCommand.CommandDetection.CommandDetectorVosk.ExtractWordsFromResult(partialJson);
                            if (parts.Length > 0)
                            {
                                var partText = string.Join(' ', parts);
                                Console.WriteLine($"[Partial] {partText}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Vosk partial DataAvailable error: {ex}");
                    }
                };

                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    Console.WriteLine("Stopping...");
                    _stopping = true;
                    try { waveInPartial?.StopRecording(); } catch { }
                    _recordingStopped.Wait(2000);
                    StopEvent.Set();
                };

                StopEvent.Reset();
                waveInPartial.StartRecording();
                Console.WriteLine("Dinleniyor (partial mode). Kapatmak için Ctrl+C basın.");
                StopEvent.Wait();
                return;
            }
            else if (backend == VoiceCommand.CommandDetection.DetectorBackend.Whisper)
            {
                // Whisper partial mode
                if (inputMode.Equals("file", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(inputFile))
                {
                    Console.WriteLine($"Whisper: streaming file (partial) {inputFile} ...");
                    try
                    {
                        await VoiceCommand.CommandDetection.CommandDetectorWhisper.TranscribeFileStreamAsync(modelPath, inputFile, (seg) =>
                        {
                            if (string.IsNullOrWhiteSpace(seg)) return;
                            Console.WriteLine($"[Partial] {seg}");
                            try
                            {
                                var cmd = DetectCommand(seg, Commands);
                                if (cmd != null) Console.WriteLine(cmd);
                            }
                            catch { }
                        });

                        // Also produce final aggregated result and try to detect
                        var finalText = await VoiceCommand.CommandDetection.CommandDetectorWhisper.TranscribeFileAsync(modelPath, inputFile);
                        if (!string.IsNullOrWhiteSpace(finalText))
                        {
                            Console.WriteLine($"[Final] {finalText}");
                            var cmd = DetectCommand(finalText, Commands);
                            if (cmd != null) Console.WriteLine(cmd);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Whisper partial file error: {ex.Message}");
                    }
                    return;
                }

                // Mic realtime: reuse whisper realtime helper
                Console.WriteLine("Starting Whisper realtime (partial) ...");
                VoiceCommand.CommandDetection.CommandDetectorWhisper.StartRealtimeTranscription(modelPath, (text) =>
                {
                    if (string.IsNullOrWhiteSpace(text)) return;
                    Console.WriteLine($"[Partial] {text}");
                    try
                    {
                        var cmd = DetectCommand(text, Commands);
                        if (cmd != null) Console.WriteLine(cmd);
                    }
                    catch { }
                });

                Console.WriteLine("Dinleniyor (Whisper realtime partial). Kapatmak için Ctrl+C basın.");
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    Console.WriteLine("Stopping...");
                    VoiceCommand.CommandDetection.CommandDetectorWhisper.StopRealtimeTranscription();
                    StopEvent.Set();
                };

                StopEvent.Reset();
                StopEvent.Wait();
                return;
            }
        }
        else if (runCommand == "full")
        {
            if (backend == VoiceCommand.CommandDetection.DetectorBackend.Vosk)
            {
                FullRecognize();
                return;
            }
            else if (backend == VoiceCommand.CommandDetection.DetectorBackend.Whisper)
            {
                // Whisper full mode: print all transcribed text (per-chunk for mic, full file for file input)
                if (inputMode.Equals("file", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(inputFile))
                {
                    Console.WriteLine($"Whisper: transcribing file {inputFile} ...");
                    try
                    {
                        var txt = await VoiceCommand.CommandDetection.CommandDetectorWhisper.TranscribeFileAsync(modelPath, inputFile);
                        Console.WriteLine("[Full] " + txt);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Whisper file full error: {ex.Message}");
                    }
                    return;
                }

                // Mic realtime
                Console.WriteLine("Starting Whisper realtime (full) ...");
                VoiceCommand.CommandDetection.CommandDetectorWhisper.StartRealtimeTranscription(modelPath, (text) =>
                {
                    if (string.IsNullOrWhiteSpace(text)) return;
                    Console.WriteLine("[Full] " + text);
                });

                Console.WriteLine("Dinleniyor (Whisper realtime full). Kapatmak için Ctrl+C basın.");
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    Console.WriteLine("Stopping...");
                    VoiceCommand.CommandDetection.CommandDetectorWhisper.StopRealtimeTranscription();
                    StopEvent.Set();
                };

                StopEvent.Reset();
                StopEvent.Wait();
                return;
            }
        }
        else
        {
            // No run command requested; we've already ensured models (if requested).
            Console.WriteLine("No run command specified (e.g. 'detect', 'partial' or 'full'). Exiting.");
            return;
        }

        // No grammar -> recognize free-form (all detected words)
        _waveInInstance = new WaveInEvent
        {
            DeviceNumber = 0,
            WaveFormat = new WaveFormat(16000, 16, 1)
        };

        // synchronize shutdown to avoid native access violations in Vosk when disposing while callbacks run
        _recordingStopped.Reset();
        _stopping = false;
        _waveInInstance.RecordingStopped += (s, e) => _recordingStopped.Set();

        _waveInInstance.DataAvailable += (s, e) =>
        {
            try
            {
                if (_stopping) return;
                // Feed audio to recognizer (call under lock to avoid races with disposal)
                bool accepted;
                lock (_recognizerLock)
                {
                    if (_stopping || _recognizerInstance == null) return;
                    accepted = _recognizerInstance.AcceptWaveform(e.Buffer, e.BytesRecorded);
                }

                if (accepted)
                {
                    string resultJson;
                    lock (_recognizerLock)
                    {
                        if (_recognizerInstance == null) return;
                        resultJson = _recognizerInstance.Result();
                    }
                    // Extract only final words (ignore partial fallback) and do not print raw recognized text here
                    var finalWords = VoiceCommand.CommandDetection.CommandDetectorVosk.ExtractFinalWords(resultJson);
                    if (finalWords.Length > 0)
                    {
                        // Clean tokens (remove middle dot etc.) and normalize
                        for (int i = 0; i < finalWords.Length; i++) finalWords[i] = VoiceCommand.CommandDetection.CommandDetectorVosk.CleanToken(finalWords[i]);
                        var text = string.Join(' ', finalWords).Trim();
                        var cmd = DetectCommand(text, Commands);
                        if (cmd != null)
                        {
                            Console.WriteLine(cmd);
                        }
                    }
                }
                else
                {
                    string partialJson;
                    lock (_recognizerLock)
                    {
                        if (_recognizerInstance == null) return;
                        partialJson = _recognizerInstance.PartialResult();
                    }
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
            // initiate safe shutdown sequence
            _stopping = true;
            try { _waveInInstance?.StopRecording(); } catch { }
            // wait for RecordingStopped to ensure no more DataAvailable callbacks
            _recordingStopped.Wait(2000);
            lock (_recognizerLock)
            {
                try { _recognizerInstance?.Dispose(); } catch { }
                _recognizerInstance = null;
                try { _modelInstance?.Dispose(); } catch { }
                _modelInstance = null;
            }
            StopEvent.Set();
        };

        _waveInInstance.StartRecording();
        Console.WriteLine("Dinleniyor (tüm kelimeler algılanır). Kapatmak için Ctrl+C basın.");
        StopEvent.Wait();
        // ensure recording stopped and callbacks finished
        _stopping = true;
        try { _waveInInstance?.StopRecording(); } catch { }
        _recordingStopped.Wait(2000);
        try { _waveInInstance?.Dispose(); } catch { }
    }

    // Command detection and text normalization functionality is provided by the
    // CommandDetection module (CommandDetector class).

    static string? PrintJsonResult(string kind, string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        Console.WriteLine($"[{kind}] {json}");

        var words = VoiceCommand.CommandDetection.CommandDetectorVosk.ExtractWordsFromResult(json);
        if (words.Length == 0) return null;
        var text = string.Join(' ', words);
        Console.WriteLine($"Recognized text: {text}");
        return text;
    }

    // Create a short silent WAV (mono 16-bit PCM @ 16 kHz) for quick testing.
    static string CreateTestWav(int seconds = 1)
    {
        var path = Path.Combine(Path.GetTempPath(), $"voicecommand_test_{Guid.NewGuid()}.wav");
        int sampleRate = 16000;
        short bitsPerSample = 16;
        short channels = 1;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);
        int dataLength = sampleRate * channels * bitsPerSample / 8 * seconds;
        using var fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
        using var bw = new System.IO.BinaryWriter(fs);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataLength);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16); // Subchunk1Size
        bw.Write((short)1); // AudioFormat PCM
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataLength);
        var zero = new byte[dataLength];
        bw.Write(zero);
        bw.Flush();
        return path;
    }

    static void PrintHelp()
    {
        Console.WriteLine("VoiceCommand usage:");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  detect                     Run detect-only mode (prints detected canonical commands)");
        Console.WriteLine("  full                       Run full recognition session (all words)");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --backend <vosk|whisper>   Select backend (default: vosk)");
        Console.WriteLine("  --input <mic|file>         Input mode (default: mic)");
        Console.WriteLine("  --file <path>              WAV file for --input file");
        Console.WriteLine("  --download-models          Attempt to download missing models automatically");
        Console.WriteLine("  --auto-install / --autoinstall  Alias for --download-models");
        Console.WriteLine("  --model-size <tiny|base|small|medium|large>  (Whisper only) Choose model size to download");
        Console.WriteLine("  --model-url <url>          Direct URL to model archive or file");
        Console.WriteLine("  --model-dir <path>         Override model directory");
        Console.WriteLine("  --force                    Force reinstall even if model present");
        Console.WriteLine("  -h, --help                 Show this help");
        Console.WriteLine();
        Console.WriteLine("Available models for --model:");
        Console.WriteLine("  vosk    - Vosk acoustic models. If automatic download fails, see https://alphacephei.com/vosk/models");
        Console.WriteLine("            Example model URLs:");
        Console.WriteLine("              tr-tr                 - https://alphacephei.com/vosk/models/vosk-model-small-tr-0.3.zip");
        Console.WriteLine("              en-us                 - https://alphacephei.com/vosk/models/vosk-model-en-us-0.22.zip");
        Console.WriteLine("              small-en-us-0.15      - https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip");
        Console.WriteLine("              en-us-0.22-lgraph     - https://alphacephei.com/vosk/models/vosk-model-en-us-0.22-lgraph.zip");
        Console.WriteLine("              en-us-0.42-gigaspeech - https://alphacephei.com/vosk/models/vosk-model-en-us-0.42-gigaspeech.zip");
        Console.WriteLine("              small-cn-0.22        - https://alphacephei.com/vosk/models/vosk-model-small-cn-0.22.zip");
        Console.WriteLine("  whisper - Whisper (ggml) models. Select size with --model-size");
        Console.WriteLine("            Sizes and example direct URLs:");
        Console.WriteLine("              tiny   - https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin");
        Console.WriteLine("              base   - https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin");
        Console.WriteLine("              small  - https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin");
        Console.WriteLine("              medium - https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin");
        Console.WriteLine("              large  - https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --project VoiceCommand -- detect");
        Console.WriteLine("  dotnet run --project VoiceCommand -- --download-models --model vosk");
        Console.WriteLine();
        Console.WriteLine("See docs/ModelInstall.md for manual installation instructions and recommended model sources.");
    }

    // Full free-form recognition in a separate function.
    // This does not change Main; call this manually for a dedicated full-recognition test.
    public static void FullRecognize()
    {
        Console.WriteLine("Starting full recognition session...");
        var baseModelDir = Path.Combine(AppContext.BaseDirectory, "model");
        var modelPath = Path.Combine(baseModelDir, "vosk");
        if (!Directory.Exists(modelPath))
        {
            Console.WriteLine($"Model folder not found: {modelPath}");
            return;
        }

        try
        {
            Vosk.Vosk.SetLogLevel(0);
            using var model = new Vosk.Model(modelPath);
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

    /*
     *** OUTPUT ***
    Sesli komut dinleyici başlatılıyor...
    Algılanabilecek komutlar: başla, başlat, kapa, kapat, yazdır, iptal, yeniden dene, kart ver, para yükle, bakiye kontrol
    Available input devices:
      0: Mikrofon (Razer Kraken V3 X)
    Starting detect-only session (prints only detected commands)...
    LOG (VoskAPI:ReadDataFiles():model.cc:213) Decoding params beam=13 max-active=7000 lattice-beam=6
    LOG (VoskAPI:ReadDataFiles():model.cc:216) Silence phones 1:2:3:4:5:6:7:8:9:10
    LOG (VoskAPI:RemoveOrphanNodes():nnet-nnet.cc:948) Removed 1 orphan nodes.
    LOG (VoskAPI:RemoveOrphanComponents():nnet-nnet.cc:847) Removing 2 orphan components.
    LOG (VoskAPI:Collapse():nnet-utils.cc:1488) Added 1 components, removed 2
    LOG (VoskAPI:ReadDataFiles():model.cc:248) Loading i-vector extractor from C:\Users\selcu\source\repos\VoiceCommand\VoiceCommand\bin\Debug\net10.0\model/ivector/final.ie
    LOG (VoskAPI:ComputeDerivedVars():ivector-extractor.cc:183) Computing derived variables for iVector extractor
    LOG (VoskAPI:ComputeDerivedVars():ivector-extractor.cc:204) Done.
    LOG (VoskAPI:ReadDataFiles():model.cc:282) Loading HCL and G from C:\Users\selcu\source\repos\VoiceCommand\VoiceCommand\bin\Debug\net10.0\model/HCLr.fst C:\Users\selcu\source\repos\VoiceCommand\VoiceCommand\bin\Debug\net10.0\model/Gr.fst
    LOG (VoskAPI:ReadDataFiles():model.cc:303) Loading winfo C:\Users\selcu\source\repos\VoiceCommand\VoiceCommand\bin\Debug\net10.0\model/word_boundary.int
    Algılanabilecek komutlar: başla, başlat, kapa, kapat, yazdır, iptal, yeniden dene, kart ver, para yükle, bakiye kontrol
    Detect-only: listening. Press Ctrl+C to stop.
    başla
    başlat
    kapa
    kapat
    yazdır
    iptal
    iptal
    iptal
    iptal
    yeniden dene
    kart ver
    para yükle
    bakiye kontrol
    Stopping detect-only...
     */
    // DetectOnly: listen and print only the detected canonical command (one per final result).
    public static void DetectOnly()
    {
        Console.WriteLine("Starting detect-only session (prints only detected commands)...");
        var baseModelDir = Path.Combine(AppContext.BaseDirectory, "model");
        var modelPath = Path.Combine(baseModelDir, "vosk");
        if (!Directory.Exists(modelPath))
        {
            Console.WriteLine($"Model folder not found: {modelPath}");
            return;
        }

        try
        {
            Vosk.Vosk.SetLogLevel(0);
            using var model = new Vosk.Model(modelPath);
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
                    // AcceptWaveform returns true when recognizer has a final result available
                    var accepted = recognizer.AcceptWaveform(e.Buffer, e.BytesRecorded);
                    string json = accepted ? recognizer.Result() : recognizer.PartialResult();

                    // Extract words (handles 'result', 'text' or 'partial') and normalize
                    var words = CommandDetectorVosk.ExtractWordsFromResult(json);
                    if (words.Length == 0) return;

                    var normalizedText = string.Join(' ', words);

                    // Exact normalized string comparison against canonical commands
                    foreach (var cmd in Commands)
                    {
                        if (CommandDetectorVosk.CleanToken(cmd) == normalizedText)
                        {
                            lock (_printLock)
                            {
                                var now = DateTime.UtcNow;
                                if (cmd != _lastPrintedCommand || (now - _lastPrintedTime).TotalMilliseconds > RepeatSuppressMs)
                                {
                                    Console.WriteLine(cmd);
                                    _lastPrintedCommand = cmd;
                                    _lastPrintedTime = now;
                                }
                            }
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"DetectOnly DataAvailable error: {ex}");
                }
            };

            Console.WriteLine("Algılanabilecek komutlar: " + string.Join(", ", Commands));
            Console.WriteLine("Detect-only: listening. Press Ctrl+C to stop.");
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("Stopping detect-only...");
                _stopping = true; // signal handler to stop processing
                waveIn.StopRecording();
                // wait for RecordingStopped to ensure no more DataAvailable callbacks
                _recordingStopped.Wait(2000);
                StopEvent.Set();
            };

            StopEvent.Reset();
            waveIn.StartRecording();
            StopEvent.Wait();
            // ensure recording stopped and callbacks finished
            _stopping = true;
            waveIn.StopRecording();
            _recordingStopped.Wait(2000);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DetectOnly error: {ex}");
        }
    }

    static string? DetectCommand(string recognizedText, string[] commands)
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
