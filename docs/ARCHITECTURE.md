# Mimari

DeployNest üç katmandan oluşur. Bağımlılık yönü tek yönlüdür: **App → Infrastructure → Core**.

```
┌──────────────────────────── DeployNest.App (WPF, net10.0-windows) ────────────────────────────┐
│ Views (XAML)  ⇄  ViewModels (CommunityToolkit.Mvvm)  ⇄  Services                               │
│   Home, Programs, Details, Selection, Queue, Progress, Results, History, Settings, About       │
│   CatalogState · InstallSessionController · NavigationService · ThemeService · DialogService  │
└───────────────────────────────────────────────┬────────────────────────────────────────────────┘
                                                │ arayüzler (Core.Abstractions)
┌──────────────────────── DeployNest.Infrastructure (net10.0-windows) ──────────────────────────┐
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
│ Localization: JsonLocalizer (Languages/*.json)                                                  │
└────────────────────────────────────────────────────────────────────────────────────────────────┘
```

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
