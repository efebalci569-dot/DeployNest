# Mimari

DeployNest üç katmandan oluşur. Bağımlılık yönü tek yönlüdür: **App → Infrastructure → Core**.
İki masaüstü uygulaması aynı Core ve Infrastructure katmanlarını ve aynı program kataloğunu kullanır:

- **DeployNest.App** — Windows (WPF, `net10.0-windows`), kurulumlar winget veya doğrulanmış doğrudan indirme ile.
- **DeployNest.Linux** — Linux (Avalonia, `net10.0`), kurulumlar Flathub (Flatpak) veya dağıtımın paket yöneticisi
  (apt, dnf, pacman) ile. Ayrıntılar: [Linux sürümü](#linux-sürümü).

```
┌──────────────────────────── DeployNest.App (WPF, net10.0-windows) ────────────────────────────┐
│ Views (XAML)  ⇄  ViewModels (CommunityToolkit.Mvvm)  ⇄  Services                               │
│   Home, Programs, Details, Selection, Queue, Progress, Results, History, Settings, About       │
│   CatalogState · InstallSessionController · NavigationService · ThemeService · DialogService  │
└───────────────────────────────────────────────┬────────────────────────────────────────────────┘
                                                │ arayüzler (Core.Abstractions)
┌─────────────────────────── DeployNest.Infrastructure (net10.0) ───────────────────────────────┐
│ WingetLocator / WingetClient / WingetInstallStrategy   ProcessRunner (ArgumentList, kill tree)  │
│ HttpFileDownloader (HTTPS + izinli sunucular)          AuthenticodeVerifier (WinVerifyTrust)    │
│ DirectInstallStrategy (SHA-256/imza → msiexec/exe)     RegistryInstalledProgramSource           │
│ PackageVerifier + InstallerSizeProbe                   JsonSettingsStore / JsonHistoryStore     │
│ CatalogProvider (gömülü + harici katalog)              FileLoggerProvider · GitHubUpdateChecker │
└───────────────────────────────────────────────┬────────────────────────────────────────────────┘
┌──────────────────────────────── DeployNest.Core (net10.0) ────────────────────────────────────┐
│ Catalog: CatalogParser (doğrulama), CatalogQuery (arama/filtre), TextNormalizer (Türkçe)        │
│ Detection: InstallationDetector (kayıt defteri + winget, üç durum)                             │
│ Installation: InstallPlanner (sıra, tekrar önleme, atlama), InstallSessionRunner (kuyruk,       │
│               ön indirme, iptal, hata izolasyonu), ReportBuilder, FailureDescriber              │
│ Winget: WingetPackageId, WingetExitCodes, WingetShowParser, WingetExportParser, ilerleme yorumu │
│ Linux: LinuxDistribution (os-release), LinuxParsers, LinuxFailureClassifier,                    │
│        LinuxInstallationDetector                                                                │
│ Localization: JsonLocalizer (Languages/*.json)                                                  │
└────────────────────────────────────────────────────────────────────────────────────────────────┘
```

Infrastructure, Linux'ta da derlenebilmesi için `net10.0` hedefler; yalnızca Windows'a özgü sınıflar
(`RegistryInstalledProgramSource`, `AuthenticodeVerifier`) `[SupportedOSPlatform("windows")]` ile işaretlidir.
Linux'a özgü sınıflar `DeployNest.Infrastructure/Linux` klasöründedir.

## Kurulum akışı

1. **Seçim** — `CatalogState` seçimi sayfalar arasında tutar.
2. **Ön denetim (Kurulum Kuyruğu)** — `QueueViewModel`:
   - internet (`SystemProbe`: ağ + DNS), winget (`WingetLocator`: PATH ve `%LOCALAPPDATA%\Microsoft\WindowsApps`), disk alanı;
   - her paket için `PackageVerifier`: `winget show --id X --exact --source winget` → kimlik ve **yayıncı eşleşmesi**,
     sürüm, kurulum adresi; `InstallerSizeProbe` ile yalnızca başlık isteğiyle boyut;
   - `InstallPlanner.Create`: tekrarları kaldırır, kurulu olanları (kullanıcı istemedikçe) atlar, doğrulanamayan winget
     paketlerini engeller, katılımsız kurulumları öne, etkileşimli olanları sona alır, kategori/katalog sırasını korur.
3. **Çalıştırma** — `InstallSessionController` motoru UI thread'i dışında başlatır.
   `InstallSessionRunner`:
   - ön indirmeyi destekleyen stratejiler (doğrudan indirme) için indirmeleri `SemaphoreSlim` ile sınırlı paralel başlatır,
   - kurulumları **her zaman sırayla** çalıştırır,
   - her öğe için hataları yakalar ve diğerlerine devam eder,
   - iki seviyeli iptal: `RequestStop` (bekleyenler + indirmeler iptal, çalışan kurulum biter) ve `RequestAbort`
     (çalışan süreç ağacı da sonlandırılır),
   - başarıdan sonra `InstalledStateVerifier` ile programın gerçekten kurulu olduğunu denetler.
4. **Sonuç** — geçmiş (`JsonHistoryStore`), rapor (`ReportBuilder` → `%LOCALAPPDATA%\DeployNest\reports`), kurulu
   durumların yeniden algılanması.

## Kurulum stratejileri

| Strateji | Ne zaman | Doğrulama | Çalıştırma |
| --- | --- | --- | --- |
| `WingetInstallStrategy` | winget varsa (varsayılan) | ön denetimde kimlik + yayıncı; winget manifest SHA-256 | `winget install --id X --exact --source winget --silent --accept-package-agreements [--no-upgrade]` |
| `DirectInstallStrategy` | winget yoksa ve katalogda `direct` tanımı varsa | HTTPS + izinli sunucu, boyut, SHA-256 ve/veya Authenticode yayıncısı | MSI: `msiexec /i dosya /passive /norestart`; EXE: yalnızca belgeli sessiz parametreler, yoksa kurulum arayüzü |

Çıkış kodları resmi winget tablosuna ([returnCodes.md](https://github.com/microsoft/winget-cli/blob/master/doc/windows/package-manager/winget/returnCodes.md))
ve standart Windows Installer kodlarına göre `InstallOutcome`/`FailureKind` değerlerine çevrilir.

## Kurulu algılama

`InstallationDetector` iki kaynağı birleştirir:

- **Kayıt defteri:** `HKLM` (64/32 bit) ve `HKCU` altındaki `Uninstall` anahtarları; sistem bileşenleri ve güncellemeler hariç.
  Eşleşme için katalogdaki **sabitlenmiş** `displayName` deseni ve `publisher` deseni birlikte sağlanmalıdır.
- **winget:** `winget export` JSON çıktısındaki paket kimlikleri (dilden bağımsız).

Bir kaynak henüz yanıt vermediyse durum *Denetleniyor*; hiçbir güvenilir kaynak yoksa *Durum doğrulanamadı* olur.

## Kalıcılık

| Dosya | İçerik |
| --- | --- |
| `settings.json` | Tema, dil, indirme klasörü, eşzamanlı indirme, dosya saklama, kurulu programları atlama |
| `history.json` | Kurulum geçmişi (en fazla 2000 kayıt) |
| `cache/package-info.json` | Doğrulanan sürüm ve boyutlar (kartlarda boyut göstermek için) |
| `logs/deploynest-YYYYMMDD.log` | JSON satırı günlükler, 14 gün saklanır |
| `reports/*.txt` | Oturum raporları |

Tüm JSON dosyaları atomik olarak yazılır (geçici dosya + değiştirme). Bozuk bir dosya `*.corrupt-*` olarak yedeklenir ve
varsayılan değerlerle devam edilir; uygulama yeniden başlatıldığında bozuk duruma düşmez.

## Yerelleştirme

Metinler `src/DeployNest.Core/Localization/Languages/<dil>.json` dosyalarındadır ve gömülü kaynak olarak yüklenir.
Yeni bir dil için aynı anahtarlarla bir dosya eklemek yeterlidir (`_language` ve `_culture` meta alanlarıyla);
Ayarlar sayfası bulunan dilleri otomatik listeler. XAML'de `{l:Loc Anahtar}` dil değişince anında güncellenir.

## Linux sürümü

```
┌────────────────────────── DeployNest.Linux (Avalonia 11, net10.0) ──────────────────────────┐
│ Views (AXAML) ⇄ ViewModels (CommunityToolkit.Mvvm) ⇄ Services                                │
│   Home, Programs, Queue, Progress, Results, History, Settings                                │
│   LinuxCatalogState · LinuxInstallController · Loc (tr.json) · LogoLoader                    │
└──────────────────────────────────────────────┬────────────────────────────────────────────────┘
┌──────────────────────── DeployNest.Infrastructure/Linux ─────────────────────────────────────┐
│ LinuxSystemDetector (os-release, paket yöneticisi, Flatpak, ağ, disk)                         │
│ FlatpakClient · NativePackageClient · LinuxInventorySource (kurulu paketler)                  │
│ LinuxPackageVerifier (flatpak remote-info, apt-cache/dnf/pacman bilgisi)                       │
│ FlatpakInstallStrategy · NativePackageStrategy                                                │
│ RootHelper + PrivilegedSession(Provider) (pkexec ile başlatılan yetkili yardımcı)             │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
```

Katalog `SoftwareCatalog.ForPlatform(CatalogPlatform.Linux)` ile süzülür: yalnızca `linux` bölümü olan programlar,
Linux'ta sunulan hazır gruplar ve boş kalmayan kategoriler gösterilir. Windows uygulaması aynı yöntemi
`CatalogPlatform.Windows` ile kullanır.

### Kurulum yöntemi seçimi

`InstallPlanner`, platformdan bağımsızdır; yöntem seçimi `PlannerContext` ile yapılır
(`PlannerContext.Windows(...)` / `PlannerContext.Linux(flatpakAvailable, packageManager, preferNative)`):

| Durum | Yöntem |
| --- | --- |
| Programın Flathub kimliği var ve Flatpak kurulu (varsayılan) | **Flatpak** — `flatpak install --user --noninteractive -y flathub <kimlik>` |
| Flathub kimliği yok, ya da Ayarlar'da "dağıtım deposunu tercih et" açık ve dağıtım için paket adı var | **Dağıtım paketi** — apt / dnf / pacman |
| Flatpak kurulu değil, dağıtım paketi de yok | Kurulmaz: *Flatpak gerekli* (kuyruk ekranı Flatpak'ı tek tıkla kurmayı önerir) |
| Paket yöneticisi tanınmıyor ve Flathub kimliği yok | Kurulmaz: *Bu dağıtım desteklenmiyor* |

Flatpak kurulumları kullanıcı hesabına yapılır, yönetici izni gerektirmez. `flathub` kaynağı yoksa
`flatpak remote-add --user --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo` ile eklenir.

### Ön denetim ve algılama

- **Dağıtım:** `/etc/os-release` (`ID`, `ID_LIKE`) → paket yöneticisi ailesi (Debian/Ubuntu → apt, Fedora/RHEL → dnf,
  Arch → pacman); araç sistemde yoksa mevcut olan ilk araç kullanılır.
- **Kurulu programlar:** `flatpak list --app` ve `dpkg-query` / `rpm -qa` / `pacman -Q`. Bir program, Flathub
  uygulaması kuruluysa **veya** tüm dağıtım paketleri kuruluysa *Kurulu* sayılır.
- **Doğrulama:** `flatpak remote-info flathub <kimlik>` (sürüm, indirme boyutu, lisans) ve `apt-cache show` /
  `dnf info` / `pacman -Si`. Pakete ulaşılamazsa kurulum yapılmaz. Kurulumdan sonra durum yeniden denetlenir.
- Tüm komutlar `LC_ALL=C` ile ve argüman listesiyle çalıştırılır; çıktı `LinuxParsers` ile okunur, hatalar
  `LinuxFailureClassifier` ile `FailureKind` değerlerine çevrilir (ağ yok, disk dolu, paket bulunamadı, kilitli
  paket veritabanı, bağımlılık sorunu, reddedilen yönetici izni…).

### Yetkili yardımcı (root helper)

Dağıtım paketleri root gerektirir. Arayüz hiçbir zaman root olarak çalışmaz; ilk dağıtım paketi kurulacağı zaman
aynı çalıştırılabilir dosya `pkexec <DeployNest> --root-helper` ile **oturum başına bir kez** başlatılır
(DeployNest zaten root ise doğrudan). Yardımcı standart girdi/çıktı üzerinden JSON satırlarıyla konuşur:

| Mesaj | Yön | Anlamı |
| --- | --- | --- |
| `{"type":"ready","manager":"apt"}` | yardımcı → uygulama | Yardımcı hazır |
| `{"id":1,"op":"install","packages":["vlc"]}` | uygulama → yardımcı | Paketleri kur |
| `{"id":1,"op":"install-flatpak"}` | uygulama → yardımcı | Dağıtımın `flatpak` paketini kur |
| `{"op":"cancel"}` | uygulama → yardımcı | Çalışan komutu durdur |
| `{"id":1,"type":"output","line":"…"}` | yardımcı → uygulama | Komut çıktısı (ilerleme için) |
| `{"id":1,"type":"done","exitCode":0}` | yardımcı → uygulama | Komut bitti |

Yardımcı yalnızca **sabit** komutları çalıştırır (`apt-get install -y …`, `dnf install -y …`,
`pacman -S --needed --noconfirm …`); paket adları `^[A-Za-z0-9][A-Za-z0-9+._-]{0,99}$` biçim denetiminden geçer,
kabuk kullanılmaz, rastgele komut veya dosya yolu kabul edilmez. apt için `apt-get update` oturumda bir kez çalışır.
pkexec penceresi kapatılırsa (çıkış kodu 126/127) yalnızca dağıtım paketleri *yönetici izni reddedildi* olarak
raporlanır ve sonuç ekranında elle çalıştırılabilecek `sudo apt install …` komutu gösterilir.

### Paketleme

`scripts/publish-linux.sh <sürüm> [linux-x64|linux-arm64]` self-contained tek dosya yayını üretir ve paketler:

- `DeployNest-<sürüm>-linux-<mimari>.tar.gz` — çalıştırılabilir dosya, Skia/HarfBuzz yerel kütüphaneleri,
  `install.sh` / `uninstall.sh` (kullanıcı hesabına, `~/.local/opt/deploynest`), `.desktop` dosyası ve simge;
- `deploynest_<sürüm>_<amd64|arm64>.deb` — `/opt/deploynest`, `/usr/bin/deploynest`, menü kaydı;
- `SHA256SUMS-linux.txt`.

Ayarlar, geçmiş, günlükler ve raporlar `~/.local/share/DeployNest` altında tutulur (`$XDG_DATA_HOME` dikkate alınır).
