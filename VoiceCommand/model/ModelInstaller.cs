using System;
using System.IO;
using System.Net.Http;
using System.IO.Compression;
using System.Threading.Tasks;
using System.Diagnostics;

namespace VoiceCommand.Model
{
    public static class ModelInstaller
    {
        static void Log(string message)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        public static async Task<bool> EnsureModelAsync(VoiceCommand.CommandDetection.DetectorBackend backend, string? modelDir = null, bool autoInstall = false, string? modelUrl = null, bool force = false)
        {
            var baseModelDir = Path.Combine(AppContext.BaseDirectory, "model");
            string? dest = modelDir;
            if (string.IsNullOrEmpty(dest))
            {
                dest = backend == VoiceCommand.CommandDetection.DetectorBackend.Vosk
                    ? Path.Combine(baseModelDir, "vosk")
                    : Path.Combine(baseModelDir, "whisper");
            }

            Log($"Checking model directory: {dest}");
            if (!force && Directory.Exists(dest) && BackendHasModelFiles(dest, backend))
            {
                Log("Model already present and valid.");
                return true;
            }

            if (!autoInstall)
            {
                Log("Model missing.");
                PrintManualInstructions(backend, dest);
                return false;
            }

            Log("Automatic install requested — starting install sequence.");

            var downloadUrl = modelUrl ?? GetDefaultUrlForBackend(backend);
            if (string.IsNullOrEmpty(downloadUrl))
            {
                Console.WriteLine("No default download URL available for this backend. Provide one with --model-url.");
                PrintManualInstructions(backend, dest);
                return false;
            }

            Log($"Downloading model from: {downloadUrl}");
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromMinutes(30);
                using var resp = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode)
                {
                    Log($"Download failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
                    Log("Stage: download");
                    PrintManualInstructions(backend, dest);
                    return false;
                }

                var urlPath = new Uri(downloadUrl).AbsolutePath;
                var ext = Path.GetExtension(urlPath).ToLowerInvariant();
                // Prefer preserving the remote filename when possible so installed files include the model size/name
                var origName = Path.GetFileName(urlPath);
                var downloadFileName = !string.IsNullOrWhiteSpace(origName)
                    ? $"{Guid.NewGuid():N}_{origName}"
                    : Path.GetFileName(Path.GetTempFileName()) + ext;
                var downloadPath = Path.Combine(Path.GetTempPath(), downloadFileName);
                Log($"Saving download to temporary file: {downloadPath}");

                // Stream copy with progress
                using (var contentStream = await resp.Content.ReadAsStreamAsync())
                using (var fs = File.Create(downloadPath))
                {
                    var total = resp.Content.Headers.ContentLength ?? -1L;
                    var buffer = new byte[81920];
                    long totalRead = 0;
                    int read;
                    var sw = Stopwatch.StartNew();
                    var lastReport = DateTime.MinValue;
                    while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fs.WriteAsync(buffer, 0, read);
                        totalRead += read;
                        // report at most 4x/sec
                        if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 250)
                        {
                            lastReport = DateTime.UtcNow;
                            if (total != -1)
                            {
                                var pct = (double)totalRead / total * 100.0;
                                Console.Write($"\r[{DateTime.Now:HH:mm:ss}] Downloaded {totalRead:N0} / {total:N0} bytes ({pct:F1}%)");
                            }
                            else
                            {
                                Console.Write($"\r[{DateTime.Now:HH:mm:ss}] Downloaded {totalRead:N0} bytes");
                            }
                        }
                    }
                    sw.Stop();
                    Console.WriteLine();
                    Log($"Download completed: {downloadPath} ({totalRead:N0} bytes in {sw.Elapsed.TotalSeconds:F1}s)");
                }

                Directory.CreateDirectory(dest);
                Console.WriteLine($"Extracting/placing model into: {dest}");
                if (ext == ".zip")
                {
                    try
                    {
                        ZipFile.ExtractToDirectory(downloadPath, dest, true);
                        Console.WriteLine($"Extraction complete to: {dest}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Extraction failed: {ex.Message}");
                        Console.WriteLine("Stage: extract");
                        PrintManualInstructions(backend, dest);
                        return false;
                    }
                }
                else if (ext == ".bin" || ext.Contains("ggml") || ext == ".ggml")
                {
                    try
                    {
                        // Decide a canonical filename based on destination folder name when possible
                        var destFolder = new DirectoryInfo(dest).Name.ToLowerInvariant();
                        var canonical = Path.GetFileName(downloadPath);
                        var mapping = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "tiny", "ggml-tiny.bin" },
                            { "base", "ggml-base.bin" },
                            { "small", "ggml-small.bin" },
                            { "medium", "ggml-medium.bin" },
                            { "large", "ggml-large-v3.bin" }
                        };

                        if (mapping.ContainsKey(destFolder)) canonical = mapping[destFolder];
                        else if (!string.IsNullOrWhiteSpace(origName)) canonical = origName;

                        var final = Path.Combine(dest, canonical);
                        if (File.Exists(final)) File.Delete(final);
                        File.Move(downloadPath, final);
                        Console.WriteLine($"Saved model file to: {final}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to move downloaded model file: {ex.Message}");
                        Console.WriteLine("Stage: place-file");
                        PrintManualInstructions(backend, dest);
                        return false;
                    }
                }
                else
                {
                    try
                    {
                        ZipFile.ExtractToDirectory(downloadPath, dest, true);
                        Console.WriteLine($"Extraction complete to: {dest}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Could not extract archive (not a zip?). Will try to move file: {ex.Message}");
                        try
                        {
                            var final = Path.Combine(dest, Path.GetFileName(downloadPath));
                            if (File.Exists(final)) File.Delete(final);
                            File.Move(downloadPath, final);
                            Console.WriteLine($"Saved model file to: {final}");
                        }
                        catch (Exception ex2)
                        {
                            Console.WriteLine($"Failed to place downloaded file: {ex2.Message}");
                            Console.WriteLine("Stage: place-file");
                            PrintManualInstructions(backend, dest);
                            return false;
                        }
                    }
                }

                // Validate post-install
                if (BackendHasModelFiles(dest, backend))
                {
                    Console.WriteLine($"Model installation validated in: {dest}");
                    return true;
                }

                Console.WriteLine("Downloaded files but expected model file patterns were not found after extraction/placement.");
                Console.WriteLine("Stage: validate");
                PrintManualInstructions(backend, dest);
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Model download/install failed: {ex.Message}");
                Console.WriteLine("Stage: download/install exception");
                PrintManualInstructions(backend, dest);
                return false;
            }
        }

        static bool BackendHasModelFiles(string dir, VoiceCommand.CommandDetection.DetectorBackend backend)
        {
            try
            {
                if (!Directory.Exists(dir)) return false;
                if (backend == VoiceCommand.CommandDetection.DetectorBackend.Vosk)
                {
                    var files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories);
                    foreach (var f in files)
                    {
                        var lower = Path.GetFileName(f).ToLowerInvariant();
                        if (lower.EndsWith(".mdl") || lower.EndsWith(".fst") || lower.EndsWith("mfcc.conf") || lower.EndsWith("word_boundary.int") || lower.EndsWith(".int") || lower == "final.mdl")
                            return true;
                    }
                    return false;
                }
                else
                {
                    var bins = Directory.GetFiles(dir, "*.bin", SearchOption.AllDirectories);
                    if (bins.Length > 0) return true;
                    var ggmls = Directory.GetFiles(dir, "*.ggml*", SearchOption.AllDirectories);
                    if (ggmls.Length > 0) return true;
                    return false;
                }
            }
            catch { return false; }
        }

        static string GetDefaultUrlForBackend(VoiceCommand.CommandDetection.DetectorBackend backend)
        {
            if (backend == VoiceCommand.CommandDetection.DetectorBackend.Vosk)
            {
                return "https://alphacephei.com/vosk/models/vosk-model-small-tr-0.3.zip";
            }
            else
            {
                // default to small
                return "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin";
            }
        }

        static void PrintManualInstructions(VoiceCommand.CommandDetection.DetectorBackend backend, string dest)
        {
            Console.WriteLine();
            Console.WriteLine("Model not found and automatic install was not performed or failed.");
            Console.WriteLine($"Expected model location: {dest}");
            Console.WriteLine("Manual install options:");
            if (backend == VoiceCommand.CommandDetection.DetectorBackend.Vosk)
            {
                Console.WriteLine("- Vosk modellerini buradan indirin: https://alphacephei.com/vosk/models");
                Console.WriteLine("- İndirilen modeli `model/vosk/<modelname>` dizinine açın (zip içinde model dosyaları olmalı).");
                Console.WriteLine("- Örnek: `model/vosk/vosk-model-small-tr-0.3/` içinde `final.mdl`, `mfcc.conf` vb. bulunmalıdır.");
            }
            else
            {
                Console.WriteLine("- Whisper ggml modelleri için sayfa (tüm modellerin listelendiği yer): https://huggingface.co/ggerganov/whisper.cpp/blob/main");
                Console.WriteLine("- Örnek doğrudan indirme linkleri:");
                Console.WriteLine("  Tiny:  https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin");
                Console.WriteLine("  Base:  https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin");
                Console.WriteLine("  Small: https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin");
                Console.WriteLine("  Medium:https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin");
                Console.WriteLine("  Large: https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin");
                Console.WriteLine("- İndirilen `.bin` veya `.ggml` dosyasını `model/whisper/<modelname>/` içine koyun.");
            }
            Console.WriteLine();
            Console.WriteLine("You can also provide a direct download URL with the `--model-url <url>` argument.");
            Console.WriteLine("Example: dotnet run --project VoiceCommand -- --download-models --model vosk");
            Console.WriteLine();
        }
    }
}
