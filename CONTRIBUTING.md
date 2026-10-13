# Katkıda bulunma rehberi

DeployNest'e katkıda bulunmak istediğin için teşekkürler! Bu belge; geliştirme ortamını, kod kurallarını ve
özellikle **kataloğa yeni program ekleme** sürecini anlatır.

## Davranış

Saygılı ve yapıcı ol. Kişisel saldırı, taciz ve ayrımcılık kabul edilmez.

## Hata bildirimi ve öneriler

- Önce [mevcut issue'ları](https://github.com/efebalci569-dot/DeployNest/issues) kontrol et.
- Hata bildirirken: Windows sürümü, DeployNest sürümü (Ayarlar → Tanılama ve sürüm), winget sürümü (`winget --version`),
  adım adım yeniden üretme yolu ve ilgili günlük satırları (`%LOCALAPPDATA%\DeployNest\logs`) ekle.
  Linux'ta: dağıtım ve sürümü (`cat /etc/os-release`), `flatpak --version` ve `~/.local/share/DeployNest/logs` günlükleri.
  Günlüklerde kullanıcı adın ve profil yolun maskelenir ama paylaşmadan önce yine de göz at.
- **Güvenlik açıklarını issue olarak açma**; [SECURITY.md](SECURITY.md) dosyasındaki yolu izle.

## Geliştirme ortamı

1. .NET SDK 10.0 kur (`winget install Microsoft.DotNet.SDK.10`).
2. Depoyu klonla, `dotnet build DeployNest.slnx` ve `dotnet test tests/DeployNest.Tests/DeployNest.Tests.csproj` çalıştır.
3. Uygulamayı `dotnet run --project src/DeployNest.App/DeployNest.App.csproj` ile başlat.

Linux sürümü üzerinde çalışıyorsan (Linux'ta, .NET SDK 10.0 ile):

```bash
dotnet run --project src/DeployNest.Linux/DeployNest.Linux.csproj
dotnet test tests/DeployNest.Tests/DeployNest.Tests.csproj -p:EnableWindowsTargeting=true
```

Test projesi `net10.0-windows` hedeflediği için Linux'ta `EnableWindowsTargeting` gerekir; testlerin tamamı Linux'ta da
çalışır. Windows uygulamasını (WPF) Linux'ta derlemek için de aynı özellik kullanılabilir
(`dotnet build DeployNest.slnx -p:EnableWindowsTargeting=true`), ancak çalıştırılamaz.

## Kod kuralları

- Mimari: **Core** (UI'dan bağımsız iş mantığı) → **Infrastructure** (Windows, Linux, ağ, dosya sistemi) → **App**
  (WPF/MVVM, Windows) ve **Linux** (Avalonia/MVVM). İş mantığını XAML veya code-behind'a koyma; görünüm modelleri ve
  servisler kullan. Platforma özgü kodu `[SupportedOSPlatform]` ile işaretle veya `Infrastructure/Linux` altına koy.
- UI thread'inde uzun süren iş yapma; `async/await` ve `CancellationToken` kullan.
- Süreç başlatırken **asla** komut metni birleştirme; `ProcessRequest` ile argüman listesi kullan.
- Hataları sessizce yutma: ya kullanıcıya anlaşılır bir `FailureKind` ile ilet ya da günlüğe yaz.
- Kullanıcıya görünen tüm metinler `src/DeployNest.Core/Localization/Languages/tr.json` içindedir. XAML'de `{l:Loc Anahtar}`,
  C#'ta `ILocalizer` kullan. Yeni `FailureKind` eklersen `Failure.<Kind>.Title` ve `.Advice` metinlerini de ekle (testler bunu denetler).
- Yeni davranış için test yaz. Testler bilgisayara **program kurmamalı**; süreç/indirme katmanlarını sahte uygulamalarla değiştir.
- `.editorconfig` kurallarına uy (dosya kapsamlı namespace, 4 boşluk girinti).

## Kataloğa program ekleme

Katalog: `catalog/software.json` — şema: [docs/CATALOG.md](docs/CATALOG.md).

Kontrol listesi:

1. **winget kimliğini doğrula:**
   ```powershell
   winget show --id Yayinci.Program --exact --source winget
   ```
   Çıktıdaki `Publisher` değerini `winget.publisher` alanına aynen yaz. Kimliği asla tahmin etme.
2. **Kurulu algılama kuralı ekle:** programı bir bilgisayara kurup kaldırma kaydını incele:
   ```powershell
   Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*','HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' |
     Where-Object DisplayName -match 'Program' | Select-Object DisplayName, Publisher, DisplayVersion
   ```
   `displayName` için **başı ve sonu sabitlenmiş** (`^...$`) bir düzenli ifade ve `publisher` kuralı kullan; yalnızca ada dayalı eşleşme kabul edilmez.
3. **Lisans, ana sayfa, açıklamalar:** `winget show` çıktısındaki lisans ve lisans adresini kullan. Açıklamalar Türkçe ve kısa olmalı.
4. **Yönetici izni ipucu (`elevation`):** yalnızca bildiğin durumlarda `likely`/`unlikely`, aksi halde `unknown`.
5. **Doğrudan indirme (`direct`) eklemek istiyorsan:** yalnızca yayıncının resmi, sabit bir HTTPS adresi varsa; `sha256` (winget manifestindeki değer kullanılabilir) ve/veya `signer` zorunludur. EXE için sessiz kurulum parametrelerini yalnızca yayıncının belgesine (`silentArgsSource`) dayanarak ekle.
6. **Linux kaynakları (`linux`):** program Linux'ta da varsa ekle; şema: [docs/CATALOG.md](docs/CATALOG.md#linux).
   - Flathub kimliğini Flathub API'siyle doğrula; yayıncının resmi veya Flathub'da doğrulanmış paketini tercih et:
     ```bash
     curl -s https://flathub.org/api/v2/appstream/org.videolan.VLC | jq '.name, .developer_name, .metadata."flathub::verification::verified"'
     ```
   - Paket adlarını dağıtımların resmi depolarında doğrula (`apt-cache policy <paket>`, `dnf info <paket>`,
     `pacman -Si <paket>` veya packages.ubuntu.com, packages.fedoraproject.org, archlinux.org/packages). AUR, PPA ve
     üçüncü taraf depoları kullanma.
   - Yalnızca Linux'ta olan bir program için `winget`/`direct` olmadan yalnızca `linux` bölümü yeterlidir.
7. **Logo:** `assets/icons/apps/<id>.png` (en fazla 160×160; `scripts/import-logos.ps1` yardımcı olur) ve kaynağını
   `assets/icons/apps/SOURCES.md` tablosuna ekle. Her programın logosu olmalıdır (testler denetler). Tercih sırası:
   yayıncının resmi deposu/sitesi, Flathub'daki resmi uygulama simgesi, dashboard-icons koleksiyonu.
8. **Siteyi eşitle:** `./scripts/build-website.ps1` (Linux/macOS: `./scripts/build-website.sh`)
9. **Testleri çalıştır:** katalog doğrulama testleri; eksik logo, geçersiz kimlik, tekrar eden id ve site kataloğunun eşit olmamasını yakalar.

## Pull request süreci

- Küçük ve odaklı PR'lar tercih edilir. Açıklamada **neden** yapıldığını yaz.
- CI (derleme + testler + yayın denemesi) yeşil olmalı.
- UI değişikliklerinde açık ve koyu temada ekran görüntüsü ekle.
- `CHANGELOG.md` dosyasının “Yayımlanmamış” bölümüne kısa bir not ekle.

## Lisans

Katkıların [MIT Lisansı](LICENSE) altında dağıtılır.
