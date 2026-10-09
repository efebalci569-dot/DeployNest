# Değişiklik günlüğü

Bu projedeki önemli değişiklikler bu dosyada belgelenir.
Biçim [Keep a Changelog](https://keepachangelog.com/tr-TR/1.1.0/) temel alınarak hazırlanmıştır ve proje
[Anlamsal Sürümleme](https://semver.org/lang/tr/) kullanır.

## [Yayımlanmamış] — 0.1.0

İlk sürüm (MVP). Henüz GitHub Releases'ta yayımlanmadı; `v0.1.0` etiketi gönderildiğinde yayımlanacak.

### Eklenenler

- WPF (.NET 10 LTS) masaüstü uygulaması: Ana Sayfa, Tüm Programlar, Program Detayları, Seçilen Programlar,
  Kurulum Kuyruğu, Kurulum İlerlemesi, Kurulum Sonuçları, Kurulum Geçmişi, Ayarlar ve Hakkında sayfaları.
- 24 programlık, 7 kategorili, winget kimlikleri ve yayıncıları doğrulanmış JSON kataloğu; geçersiz kayıtların reddedilmesi.
- Türkçe karakter toleranslı arama; kategori, kurulu/kurulu olmayan ve seçilen filtreleri; dört hazır seçim grubu.
- Kayıt defteri ve `winget export` ile kurulu program algılama (ad + yayıncı + paket kimliği).
- winget ile kurulum motoru: kurulum öncesi paket/yayıncı doğrulaması, boyut sorgusu, lisans onayı, sıralı kuyruk,
  hata izolasyonu, yumuşak ve zorla iptal, kurulum sonrası doğrulama.
- winget olmadan yedek doğrudan indirme yöntemi: yalnızca HTTPS, izinli sunucu listesi, SHA-256 ve Authenticode doğrulaması.
- Türkçe hata açıklamaları ve öneriler, oturum raporu (otomatik kaydedilir, dışa aktarılabilir), kurulum geçmişi.
- Açık/koyu tema, gerçek program logoları, erişilebilir buton adları, JSON tabanlı dil altyapısı.
- Ayarlar: tema, dil, indirme klasörü, eşzamanlı indirme sayısı, dosyaları saklama, kurulu programları atlama,
  günlük klasörü, sürüm ve GitHub Releases üzerinden güncelleme denetimi.
- Yapılandırılmış (JSON satırı) günlükler; kullanıcı adı ve profil yolu maskeleme.
- 132 otomatik test; GitHub Actions CI ve sürüm yayımlama iş akışları.
- Self-contained tek dosyalık x64 EXE ve taşınabilir ZIP.
- Vercel'e uygun tanıtım ve indirme sitesi (`website/`).

[Yayımlanmamış]: https://github.com/efebalci569-dot/DeployNest/commits/main
