using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Running;
using VoiceCommand.CommandDetection;

namespace Benchmarks
{
    public class Program
    {
        // Benchmarks runner entry. This program will:
        // - enumerate models under ../VoiceCommand/model/whisper and ../VoiceCommand/model/vosk
        // - enumerate WAV files in ./audio
        // - for each model run the transcribe helper and measure elapsed time

        static async Task<int> Main(string[] args)
        {
            var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "VoiceCommand"));
            var modelRoot = Path.Combine(repoRoot, "model");
            var whisperRoot = Path.Combine(modelRoot, "whisper");
            var voskRoot = Path.Combine(modelRoot, "vosk");

            var audioDir = Path.Combine(AppContext.BaseDirectory, "audio");
            if (!Directory.Exists(audioDir))
            {
                Console.WriteLine($"Please place test WAV files under: {audioDir}");
                return 1;
            }

            var audioFiles = Directory.GetFiles(audioDir, "*.wav");
            if (audioFiles.Length == 0)
            {
                Console.WriteLine($"No WAV files found in {audioDir}");
                return 1;
            }

            // Whisper models
            if (Directory.Exists(whisperRoot))
            {
                foreach (var modelDir in Directory.GetDirectories(whisperRoot))
                {
                    Console.WriteLine($"Testing Whisper model: {modelDir}");
                    CommandDetector.SetBackend(DetectorBackend.Whisper);
                    foreach (var wav in audioFiles)
                    {
                        Console.WriteLine($"  File: {Path.GetFileName(wav)}");
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var text = await CommandDetectorWhisper.TranscribeFileAsync(modelDir, wav);
                        sw.Stop();
                        Console.WriteLine($"    Time: {sw.ElapsedMilliseconds} ms, Text: {text}");
                    }
                }
            }

            // Vosk (single model dir expected)
            if (Directory.Exists(voskRoot))
            {
                foreach (var modelDir in Directory.GetDirectories(voskRoot))
                {
                    Console.WriteLine($"Testing Vosk model: {modelDir}");
                    CommandDetector.SetBackend(DetectorBackend.Vosk);
                    foreach (var wav in audioFiles)
                    {
                        Console.WriteLine($"  File: {Path.GetFileName(wav)}");
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var text = CommandDetectorVosk.TranscribeFile(modelDir, wav);
                        sw.Stop();
                        Console.WriteLine($"    Time: {sw.ElapsedMilliseconds} ms, Text: {text}");
                    }
                }
            }

            return 0;
        }
    }
}
