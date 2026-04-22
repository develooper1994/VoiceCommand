# VoiceCommand

Lightweight Türkçe sesli komut algılama örneği. Proje, komut eşleştirme mantığını ayrı bir modüle taşıyarak farklı konuşma motorlarıyla (ör. Vosk, Whisper.net) deneme yapmayı kolaylaştırır.

## İçerik

- VoiceCommand (konsol uygulaması)
- CommandDetection (komut normalizasyonu ve eşleştirme)
- model/ (Vosk model dosyalarını buraya koyun)

## Gereksinimler

- .NET 10 SDK
- Windows (mikrofon) - NAudio ile ses yakalanır

## Kurulum ve çalıştırma

1. Bağımlılıkları yükleyin ve derleyin:

    dotnet restore
    dotnet build

2. Modeller

    - Manuel: Vosk modelleri: https://alphacephei.com/vosk/models
       - Örnek model: `vosk-model-small-tr-0.3.zip`
       - Zip'i açtıktan sonra içindeki klasörün içeriğini doğrudan `model/vosk/<modelname>/` altına kopyalayın.
    - Otomatik: Uygulama, eksik modelleri indirmek için `--download-models` veya `--auto-install` argümanını destekler. Bu işlem büyük dosyalar indirebilir; internet bağlantısı gerektirir.
    - Otomatik: Uygulama, eksik modelleri indirmek için `--download-models`, `--auto-install` veya `--autoinstall` argümanlarını destekler. Bu işlem büyük dosyalar indirebilir; internet bağlantısı gerektirir.
    - Whisper için model boyutu seçeneği: `--model-size <tiny|base|small|medium|large>` kullanılabilir veya `--model-url <url>` ile doğrudan URL verilebilir.

   - Not: Eğer `--model-size` kullanırsanız uygulama `model/whisper/<size>/` klasörünü arar. Eğer bu klasör boşsa ancak `model/whisper/` altında daha önce indirilmiş bir genel `.bin` dosyası varsa, uygulama bu genel dosyayı otomatik olarak `model/whisper/<size>/ggml-<size>.bin` adıyla kopyayıp kullanmaya çalışır ve bir uyarı gösterir. Bu yalnızca hızlı test için bir kolaylıktır; doğru modeli garanti etmek için `--download-models --model-size <size> --force` ile yeniden indirme yapın veya el ile doğru dosyayı `model/whisper/<size>/ggml-<size>.bin` olarak yerleştirin.

   - PowerShell örnek (elle kopyalamak isterseniz):

   ```powershell
   $src = 'C:\path\to\project\model\whisper\tmp2zy2ot.tmp.bin'
   $dstDir = 'C:\path\to\project\model\whisper\tiny'
   New-Item -ItemType Directory -Force -Path $dstDir | Out-Null
   Copy-Item -Path $src -Destination (Join-Path $dstDir 'ggml-tiny.bin') -Force
   ```

   - CMD örnek:

   ```cmd
   mkdir "C:\path\to\project\model\whisper\tiny" 2>nul
   copy /Y "C:\path\to\project\model\whisper\tmp2zy2ot.tmp.bin" "C:\path\to\project\model\whisper\tiny\ggml-tiny.bin"
   ```

Çalıştırma ve örnekler:

- Yardım: `dotnet run --project VoiceCommand -- --help`
- Detect-only modu (sadece eşleşen kanonik komutları yazar):
   `dotnet run --project VoiceCommand -- detect`
- Full mod (tüm algılanan kelimeleri gösterir):
   `dotnet run --project VoiceCommand -- full`
- Otomatik model indirme (örnek):
   `dotnet run --project VoiceCommand -- --download-models --model vosk`
   veya
   `dotnet run --project VoiceCommand -- --auto-install --model whisper --model-url <direct-model-url>`

## Whisper.net denemesi

Projeye deneysel olarak `Whisper.net` paketi eklendi. Varsayılan olarak uygulama hâlâ Vosk kullanır. Whisper ile denemek için yönergeler ve manuel kurulum adımları için [docs/ModelInstall.md](docs/ModelInstall.md) dosyasına bakın.

## Bağımlılıklar

- Vosk 0.3.38
- NAudio 2.3.0
- Whisper.net 1.9.0 (deneysel)

## Lisans

MIT (kendiniz ekleyin)

