# Model installation guide

This document describes how to manually install Vosk and Whisper (ggml) models for the VoiceCommand app, and how to troubleshoot common native runtime issues.

## Locations

- Application looks for models at runtime under the `model` directory located next to the app executable (`AppContext.BaseDirectory`).
  - Vosk: `model/vosk/<modelname>/` — the folder must contain Vosk model files such as `final.mdl`, `mfcc.conf`, `HCLr.fst`, `Gr.fst`, `word_boundary.int`, and optional `ivector/` files.
  - Whisper: `model/whisper/<modelname>/` — the folder must contain a `*.bin` or `*.ggml*` file (ggml model file).

## Vosk (recommended small TR model)

1. Download a model from the official Vosk models page:

   https://alphacephei.com/vosk/models

   Example direct URL (Turkish small):

   https://alphacephei.com/vosk/models/vosk-model-small-tr-0.3.zip

2. Extract the downloaded ZIP into `model/vosk/` so that the model files are inside a subfolder, e.g.:

   - `model/vosk/vosk-model-small-tr-0.3/final.mdl`
   - `model/vosk/vosk-model-small-tr-0.3/mfcc.conf`

3. Run the app in file-mode or detect-mode and point `--model-dir` if you installed to a custom location:

```powershell
dotnet run --project VoiceCommand -- detect --backend vosk --input file --file C:\path\to\test.wav --model-dir C:\path\to\project\model\vosk\vosk-model-small-tr-0.3
```

## Whisper (ggml models)

Whisper models are usually distributed as `.bin` or `*.ggml` files. These files are large (tens to hundreds of MB) depending on size.

Common sources:

- `whisper.cpp` repository model links: https://github.com/ggerganov/whisper.cpp
- Example direct URLs (from `whisper.cpp` / HuggingFace):
  - Tiny:  https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin
  - Base:  https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin
  - Small: https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin
  - Medium:https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin
  - Large: https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin

1. Download the chosen `.bin`/`.ggml` file and place it under `model/whisper/<name>/`:

   - `model/whisper/ggml-small.bin` or
   - `model/whisper/small/ggml-small.bin`

### Model-size behavior and canonical filenames

The application supports `--model-size <tiny|base|small|medium|large>`. Internally the runtime expects a canonical ggml filename such as `ggml-tiny.bin`, `ggml-base.bin`, etc., located inside a size-specific folder `model/whisper/<size>/`.

If you pass `--model-size` and the corresponding `model/whisper/<size>/` folder is empty but a generic model file exists directly under `model/whisper/` (for example a previously-downloaded `tmp*.bin`), the app will automatically copy that generic model into the size folder and rename it to the canonical filename (e.g. `ggml-tiny.bin`). A warning message is printed when this fallback copy occurs. This is a convenience for quick testing, but it does not guarantee the copied model actually matches the requested size — to ensure correct model files, prefer using the explicit download or manual placement steps below.

2. To avoid native runtime errors, ensure the Whisper native runtime (Whisper.net runtime) is available for your platform. The simplest option for a .NET app is to add the NuGet runtime package:

```powershell
dotnet add package Whisper.net.Runtime
```

If you use prebuilt native libraries (whisper.cpp), ensure the native library architecture (x64/x86/arm) matches your .NET process architecture.

## Using the app CLI to download models automatically

The app supports automatic download when you run a command that requires models. Example:

```powershell
dotnet run --project VoiceCommand -- detect --backend whisper --model-size tiny
```

Or explicitly request download:

```powershell
dotnet run --project VoiceCommand -- --download-models --model whisper --model-size small
dotnet run --project VoiceCommand -- --download-models --model vosk --model-url "https://alphacephei.com/vosk/models/vosk-model-small-tr-0.3.zip"
```

Use `--force` to re-download if a downloaded file appears corrupt:

```powershell
dotnet run --project VoiceCommand -- --download-models --model whisper --model-size small --force
```

If you previously saw a generic temporary file under `model/whisper/` (e.g. `tmp2zy2ot.tmp.bin`) and want to make it available for a specific size quickly, you can either let the app auto-copy when running with `--model-size`, or manually copy it yourself using the commands below.

Windows PowerShell (example):

```powershell
$src = 'C:\path\to\project\model\whisper\tmp2zy2ot.tmp.bin'
$dstDir = 'C:\path\to\project\model\whisper\tiny'
New-Item -ItemType Directory -Force -Path $dstDir | Out-Null
Copy-Item -Path $src -Destination (Join-Path $dstDir 'ggml-tiny.bin') -Force
Write-Output 'copied'
```

Windows CMD (example):

```cmd
mkdir "C:\path\to\project\model\whisper\tiny" 2>nul
copy /Y "C:\path\to\project\model\whisper\tmp2zy2ot.tmp.bin" "C:\path\to\project\model\whisper\tiny\ggml-tiny.bin"
```

Make sure to adapt paths and filename to your setup. If you need to guarantee the requested model size, re-run the automatic download with `--download-models --model-size <size> --force`.

## GPU acceleration (CUDA / Vulkan)

Whisper/whisper.cpp can be built to use GPU acceleration (CUDA on NVIDIA or Vulkan on supported hardware). The .NET wrapper (`Whisper.net`) loads a native library; to use a GPU-enabled native build you must supply a compatible native runtime.

Steps to enable GPU acceleration:

1. Obtain or build a GPU-enabled whisper.cpp native library for your platform (Windows x64 example):
  - Build whisper.cpp with CUDA support by following the project's build instructions and enabling CUDA backend. See: https://github.com/ggerganov/whisper.cpp
  - Alternatively, build with Vulkan/WGPU support if CUDA is not available.

2. Place the resulting native DLLs/shared objects in a folder, e.g. `C:\whisper_native_gpu\` (optional if you installed a runtime package).

3. The application supports a `--runtime` option and automatic detection of Whisper.net runtime assemblies. It looks for `Whisper.net.Runtime.*` assemblies in the application output (for example `Whisper.net.Runtime.Cuda.dll` or `Whisper.net.Runtime.Vulkan.dll`). If a GPU-enabled runtime assembly is present, the app will attempt to load it and use GPU acceleration.

Example (auto-detect preferred runtime):

```powershell
dotnet run --project VoiceCommand -- partial --backend whisper --input mic --model-size base --runtime auto
```

Force a specific runtime:

```powershell
dotnet run --project VoiceCommand -- partial --backend whisper --input mic --model-size base --runtime cuda
```

Notes:
- To actually enable GPU inference you must have both system GPU drivers/runtime (CUDA/Vulkan) installed and a GPU-enabled `Whisper.net` runtime assembly present in the app output (install the appropriate NuGet runtime package or copy the assembly/DLLs into the output folder).
- If the requested runtime assembly is not found or fails to load, the app will fall back to CPU (ggml) behavior and print a warning.

## Troubleshooting

- If you see `DllNotFoundException` or `BadImageFormatException` from Whisper: ensure `Whisper.net.Runtime` is installed or native libs are present for your platform and architecture. Also confirm your process is 64-bit if using x64 libs.
- If the whisper model file is too small (partial download), delete and re-download with `--force`.
- If automatic install fails, follow the manual steps above and then run with `--model-dir` to point to the installed model.

## Example: manual install and run (Vosk)

1. Download and extract `vosk-model-small-tr-0.3.zip` to `model/vosk/vosk-model-small-tr-0.3`.
2. Run:

```powershell
dotnet run --project VoiceCommand -- detect --backend vosk --input file --file C:\path\to\test.wav --model-dir .\model\vosk\vosk-model-small-tr-0.3
```

## Notes

- Automatic downloads may be large and take time; prefer wired connections and ensure enough disk space.
- For CI or automated testing, prefer small test models or mock the detector interface.
# Model Kurulum (Vosk ve Whisper)

Bu doküman, `VoiceCommand` uygulaması için Vosk ve Whisper modellerinin nasıl kurulacağını anlatır. Uygulama `model/` klasörünü bekler:

- Vosk: `model/vosk/<modelname>/...` (ör. `model/vosk/vosk-model-small-tr-0.3/`)
- Whisper: `model/whisper/<modelname>/` ve içinde `.bin` veya `.ggml` dosyası

Önerilen yöntemler

1) Otomatik indirme (kolay)

- Uygulama, eksik modelleri indirmek için `--download-models` veya `--auto-install` argümanını destekler.
- Örnek:

```
dotnet run --project VoiceCommand -- --download-models --model vosk
```

- Whisper için doğrudan model URL'si belirtmek gerekir bazen:

```
dotnet run --project VoiceCommand -- --download-models --model whisper --model-url <direct-ggml-url>
```

- Not: Otomatik indirme büyük dosyalar indirebilir (yüzler MB). Bağlantı sonrası uygulama indirmeyi deneyecek, arşivi açacak ve `model/` altında uygun dizine yerleştirecektir. Eğer indirme başarısız olursa uygulama sizi manuel kurulum talimatlarına yönlendirir.

2) Manuel kurulum (güvenli)

- Vosk modelleri:
  - İndirme sayfası: https://alphacephei.com/vosk/models
  - Örnek: `vosk-model-small-tr-0.3.zip` indirip açın ve içindeki klasörü `model/vosk/` altına koyun.

- Whisper / ggml modelleri:
  - `whisper.cpp` ve ggml modelleri: https://github.com/ggerganov/whisper.cpp
  - HuggingFace üzerinde `ggml` veya `ggml-small.bin` arayabilirsiniz: https://huggingface.co/models
  - İndirilen `.bin` veya `.ggml` dosyasını `model/whisper/<modelname>/` klasörüne koyun.

Whisper doğrudan indirme linkleri (kullanıcıyı yönlendirmek için):

- Tüm modellerin listelendiği sayfa: https://huggingface.co/ggerganov/whisper.cpp/blob/main
- Tiny:  https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin
- Base:  https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin
- Small: https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin
- Medium: https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin
- Large (v3): https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin

Not: uygulama `--model-size <tiny|base|small|medium|large>` argümanını destekler ve bu değerler otomatik indirme sırasında kullanılabilir.

Kontrol

- Vosk için: klasör içinde `final.mdl`, `mfcc.conf`, `Gr.fst`, `HCLr.fst` gibi dosyalar görmelisiniz.
- Whisper için: klasör içinde `.bin` veya `.ggml` uzantılı model dosyası olmalıdır.

Sorun giderme

- İndirme başarısız olursa uygulama hata mesajı verecek ve bu dokümandaki linkleri gösterecektir.
- Windows izin problemleri veya antivirüs nedeniyle dosya kopyalanamıyorsa, elle indirip `model/` altına yerleştirin.

Native runtime (Whisper) ile ilgili sorunlar

- Eğer `Whisper transcribe error: Native Library not found` veya benzeri bir hata görürseniz, uygulama Whisper'ın yerel (native) kütüphanesini bulamıyor veya yükleyemiyor demektir.
- Çözüm yolları:
  - Projeye resmi native runtime paketini ekleyin:

```
dotnet add package Whisper.net.Runtime
```

  - NuGet paket sayfası: https://www.nuget.org/packages/Whisper.net.Runtime
  - Alternatif olarak platformunuza uygun `whisper.cpp` native derlemelerini kurun ve uygulamanın çalıştığı PATH / çalışma dizininde erişilebilir hale getirin.
  - Mimarilere dikkat edin (x64 vs x86 vs arm). .NET uygulamanızın çalıştığı mimari ile native kütüphane mimarisi aynı olmalıdır.

Uygulama, native runtime hatası tespit ederse realtime işlemi durduracak ve yukarıdaki talimatları bir kez gösterecektir.

Ek notlar

- Otomatik indirme varsayılan olarak kapalı tutulmuştur; `--download-models` ile etkinleştirin.
- Büyük modeller için sabır: indirme ve çıkarma işlemleri birkaç dakika sürebilir.
