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

2. Vosk Türkçe modelini indirin ve uygulama çalışma dizininde `model/` klasörüne koyun.
   - Örnek model: https://alphacephei.com/vosk/models/vosk-model-small-tr-0.3.zip
   - Zip'i açtıktan sonra içindeki klasörün içeriğini doğrudan `model/` altına kopyalayın.

Çalıştırma:

- Normal: dotnet run --project VoiceCommand
- Detect-only modu (sadece eşleşen kanonik komutları yazar): dotnet run --project VoiceCommand -- detect
- Full mod (tüm algılanan kelimeleri gösterir): dotnet run --project VoiceCommand -- full

## Whisper.net denemesi

Projeye deneysel olarak `Whisper.net` paketi eklendi. Varsayılan olarak uygulama hâlâ Vosk kullanır. Whisper ile denemek için:

1. Whisper modellerini (ve kullanım talimatlarını) Whisper.net belgelendirmesinden edinin.
2. Yeni bir backend uygulaması yazıp CommandDetection modülünü yeniden kullanın.

## Bağımlılıklar

- Vosk 0.3.38
- NAudio 2.3.0
- Whisper.net 1.9.0 (deneysel)

## Lisans

MIT (kendiniz ekleyin)

