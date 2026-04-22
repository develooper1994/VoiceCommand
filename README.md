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

