Benchmark notes

This folder is intended for lightweight benchmarking helpers comparing Vosk vs Whisper detection paths.

Suggestions:
- Add a BenchmarkDotNet project that references VoiceCommand and calls CommandDetector with prerecorded audio files
- Or create a small console app that runs many iterations and measures elapsed times using Stopwatch
