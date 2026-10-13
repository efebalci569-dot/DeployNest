# Değişiklik günlüğü

Bu projedeki önemli değişiklikler bu dosyada belgelenir.
Biçim [Keep a Changelog](https://keepachangelog.com/tr-TR/1.1.0/) temel alınarak hazırlanmıştır ve proje
[Anlamsal Sürümleme](https://semver.org/lang/tr/) kullanır.

## [Yayımlanmamış]

### Eklenenler

- **Linux sürümü** (Avalonia, .NET 10): Ana Sayfa, Tüm Programlar, Kurulum Kuyruğu, İlerleme, Sonuçlar, Geçmiş ve
  Ayarlar sayfaları; Windows sürümüyle aynı katalog, Türkçe arayüz, açık/koyu tema ve gerçek program logoları.
  - Programlar Flathub'dan (Flatpak, kullanıcı hesabına, yönetici izni olmadan) veya dağıtımın resmi deposundan
    (apt, dnf, pacman) kurulur; dağıtım ve paket yöneticisi `/etc/os-release` ile algılanır.
  - Flatpak yoksa tek tıkla kurulum ve Flathub kaynağının eklenmesi.
  - Dağıtım paketleri için pkexec ile oturum başına bir kez başlatılan, yalnızca doğrulanmış paket adlarını kabul eden
    yetkili yardımcı; izin reddedilirse elle çalıştırılacak komutun gösterilmesi.
  - Kurulu programların `flatpak list`, `dpkg-query`, `rpm` ve `pacman -Q` ile algılanması; kurulum öncesi Flathub ve
    depo doğrulaması (sürüm, indirme boyutu, lisans); kurulum sonrası doğrulama.
  - Ayarlar: Flathub yerine dağıtım deposunu tercih etme.
  - `tar.gz` (her dağıtım, `install.sh` ile kullanıcı hesabına kurulum) ve `.deb` paketleri; x64 ve ARM64.
- Katalog **183 programa ve 15 kategoriye** genişletildi (önceden 24 program, 7 kategori): Windows'ta 166, Linux'ta 96
  program. Yeni kategoriler: fotoğraf/tasarım/kurgu, ofis ve PDF, bulut, güvenlik ve gizlilik, sistem ve donanım
  araçları, kişiselleştirme ve verimlilik, çalışma zamanları ve sürücüler, indirme ve uzak erişim, yapay zekâ.
  Tüm winget kimlikleri winget-pkgs manifestlerinde, Flathub kimlikleri Flathub API'sinde doğrulandı.
- Her program için gerçek logo (183 logo; kaynakları `assets/icons/apps/SOURCES.md` içinde).
- Yeni hazır gruplar: Temel Çalışma Zamanları, Öğrenci ve Ofis Paketi, İçerik Üreticisi Paketi, Güvenlik ve Gizlilik,
  Sistem Bakım Araçları ve yalnızca Linux'ta görünen Linux Temel Paketi.
- Katalog şemasına isteğe bağlı `linux` bölümü ve hazır gruplar için `platforms` alanı.
- Web sitesi: Windows ve Linux indirme kartları, platforma göre program filtresi, Linux bölümü ve SSS.
- CI ve sürüm iş akışlarında Linux derlemesi, testleri ve paketleri.

### Değişenler

- `DeployNest.Infrastructure` artık `net10.0` hedefler (Windows'a özgü sınıflar `SupportedOSPlatform` ile işaretli).
- İndirme dosya adı temizleme işletim sisteminden bağımsız olarak hem `/` hem `\` ayırıcılarını dikkate alır.

## [0.1.0] - 2026-10-10

İlk sürüm (MVP).

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

[Yayımlanmamış]: https://github.com/efebalci569-dot/DeployNest/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/efebalci569-dot/DeployNest/releases/tag/v0.1.0
