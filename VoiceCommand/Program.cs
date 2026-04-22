using NAudio.Wave;
using Vosk;
using VoiceCommand.CommandDetection;

class Program
{
    static readonly ManualResetEventSlim StopEvent = new(false);
    // Shared canonical commands list (provided by selected command detection backend)
    static readonly string[] Commands = VoiceCommand.CommandDetection.CommandDetector.Commands;
    // Prevent printing the same command repeatedly
    static string _lastPrintedCommand = null;
    static DateTime _lastPrintedTime = DateTime.MinValue;
    static readonly object _printLock = new();
    // Minimum milliseconds between repeated prints of the same command
    const int RepeatSuppressMs = 2000;
    // Recording shutdown coordination used by modes to avoid native crashes
    static readonly ManualResetEventSlim _recordingStopped = new(false);
    static volatile bool _stopping = false;
    // Recognizer resources and lock for safe P/Invoke access
    static Model _modelInstance = null;
    static VoskRecognizer _recognizerInstance = null;
    static WaveInEvent _waveInInstance = null;
    static readonly object _recognizerLock = new();

    static void Main(string[] args)
    {
        // Parse arguments: allow backend selection and input mode
        var backendArg = "vosk"; // default
        var inputMode = "mic"; // mic or file
        string inputFile = null;
        if (args != null)
        {
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.Equals("--backend", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    backendArg = args[i + 1]; i++;
                }
                else if (a.Equals("--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    inputMode = args[i + 1]; i++;
                }
                else if (a.Equals("--file", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    inputFile = args[i + 1]; i++;
                }
                else if (a.Equals("full", StringComparison.OrdinalIgnoreCase))
                {
                    FullRecognize();
                    return;
                }
                else if (a.Equals("detect", StringComparison.OrdinalIgnoreCase))
                {
                    // handled below after backend chosen
                }
            }
        }

        // select backend
        if (!VoiceCommand.CommandDetection.CommandDetector.TrySetBackend(backendArg))
        {
            Console.WriteLine($"Bilinmeyen backend '{backendArg}', varsayılan 'vosk' kullanılacak.");
            VoiceCommand.CommandDetection.CommandDetector.SetBackend(VoiceCommand.CommandDetection.DetectorBackend.Vosk);
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

        // If started with the 'detect' argument, run the DetectOnly session that prints only detected commands.
        if (args != null && args.Length > 0 && args[0].Equals("detect", StringComparison.OrdinalIgnoreCase))
        {
            DetectOnly();
            return;
        }

        // model directory is organized per-backend: model/vosk and model/whisper
        var baseModelDir = Path.Combine(AppContext.BaseDirectory, "model");
        var backend = VoiceCommand.CommandDetection.CommandDetector.ActiveBackend;
        var modelPath = backend == VoiceCommand.CommandDetection.DetectorBackend.Vosk
            ? Path.Combine(baseModelDir, "vosk")
            : Path.Combine(baseModelDir, "whisper");

        if (!Directory.Exists(modelPath))
        {
            Console.WriteLine($"Model klasörü bulunamadı: {modelPath}");
            Console.WriteLine("Lütfen uygun backend modelini ilgili klasöre koyun (model/vosk veya model/whisper).");
            return;
        }

        if (backend == VoiceCommand.CommandDetection.DetectorBackend.Vosk)
        {
            Vosk.Vosk.SetLogLevel(0);
            // create shared instances and assign to class fields so we can synchronize disposal
            _modelInstance = new Model(modelPath);
            _recognizerInstance = new VoskRecognizer(_modelInstance, 16000.0f);
        }

        // If Whisper backend and input mode is mic -> start Whisper real-time transcription
        if (backend == VoiceCommand.CommandDetection.DetectorBackend.Whisper && inputMode.Equals("mic", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Starting Whisper realtime transcription...");
            // Callback invoked per chunk; detect canonical commands and print
            VoiceCommand.CommandDetection.CommandDetectorWhisper.StartRealtimeTranscription(modelPath, (text) =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(text)) return;
                    Console.WriteLine($"[Whisper chunk] {text}");
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

    static string PrintJsonResult(string kind, string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        Console.WriteLine($"[{kind}] {json}");

        var words = VoiceCommand.CommandDetection.CommandDetectorVosk.ExtractWordsFromResult(json);
        if (words.Length == 0) return null;
        var text = string.Join(' ', words);
        Console.WriteLine($"Recognized text: {text}");
        return text;
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
