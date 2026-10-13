# Program kataloğu şeması

Katalog dosyası: [`catalog/software.json`](../catalog/software.json). Windows ve Linux uygulamaları **aynı** katalogu
kullanır; her uygulama yalnızca kendi platformunda kurulabilen programları gösterir (bkz. [Platformlar](#platformlar)).
Uygulama, katalogu EXE içine gömülü olarak taşır. EXE'nin yanında `catalog\software.json` dosyası varsa o kullanılır
(geçersizse gömülü kataloğa dönülür ve uyarı gösterilir).

Doğrulama `CatalogParser` tarafından yapılır. **Yapısal** hatalar (geçersiz JSON, desteklenmeyen şema, hiç kategori/program
olmaması) kataloğu kullanılamaz kılar; tek bir kayıttaki hata yalnızca o kaydın reddedilmesine yol açar.

## Kök

| Alan | Tür | Açıklama |
| --- | --- | --- |
| `schemaVersion` | sayı | Şu an `1` |
| `verifiedOn` | metin | Kayıtların son doğrulandığı tarih (bilgi amaçlı) |
| `categories` | dizi | Kategoriler (sıra korunur) |
| `software` | dizi | Programlar |
| `presets` | dizi | Hazır seçim grupları |

## Kategori

| Alan | Zorunlu | Açıklama |
| --- | --- | --- |
| `id` | ✔ | küçük harf, rakam ve tire (`browsers`) |
| `name.tr` | ✔ | Türkçe ad; diğer diller isteğe bağlı (`name.en`) |
| `icon` | | Segoe Fluent Icons kod noktası (onaltılık, örn. `E774`) |

## Program

| Alan | Zorunlu | Açıklama |
| --- | --- | --- |
| `id` | ✔ | benzersiz, küçük harf/rakam/tire; logo dosya adı da budur |
| `name`, `publisher` | ✔ | Görünen ad ve yayıncı |
| `category` | ✔ | Var olan bir kategori kimliği |
| `summary.tr` | ✔ | Kısa açıklama (kartlarda) |
| `description.tr` | | Uzun açıklama (detay sayfası) |
| `homepage`, `licenseUrl` | | Yalnızca HTTPS |
| `license.tr` | | Lisans özeti (ör. “MIT (açık kaynak)”) |
| `icon.monogram`, `icon.color` | | Logo yoksa kullanılacak harf simgesi (en fazla 4 karakter) ve `#RRGGBB` rengi |
| `tags` | | Aramada kullanılan ek kelimeler |
| `elevation` | | `likely`, `unlikely` veya `unknown` (varsayılan) |
| `notes.tr` | | Kuyruk ekranında gösterilen önemli not |
| `winget` | en az biri: `winget`, `direct`, `linux` | `{ "id": "Yayinci.Program", "publisher": "winget show çıktısındaki yayıncı" }` |
| `direct` | | Aşağıya bak |
| `linux` | | Linux kurulum kaynakları; aşağıya bak |
| `detection.registry` | | `[{ "displayName": "^...$", "publisher": "^...$" }]` — büyük/küçük harf duyarsız .NET düzenli ifadeleri |
| `detection.wingetIds` | | Programı kurulu sayan ek winget kimlikleri (örn. yerelleştirilmiş paketler) |

### `winget`

- `id` biçim denetiminden geçer (`Yayinci.Program`, nokta içermeli, boşluk/kabuk karakteri içeremez).
- Kurulumdan önce `winget show --id <id> --exact --source winget` çalıştırılır; paket bulunamazsa veya yayıncı
  `publisher` alanıyla eşleşmezse program kurulmaz.

### `direct` (winget olmadan yedek yöntem)

| Alan | Zorunlu | Açıklama |
| --- | --- | --- |
| `url` | ✔ | Resmi HTTPS indirme adresi |
| `allowedHosts` | ✔ | İzinli sunucular; `url` ve tüm yönlendirmeler bu listede olmalı |
| `installerType` | ✔ | `msi` veya `exe` |
| `sha256` | `sha256` veya `signer` | Beklenen dosya hash'i (64 onaltılık karakter) |
| `signer` | `sha256` veya `signer` | Beklenen Authenticode yayıncısı (sertifikadaki `O=` veya `CN=`) |
| `version`, `sizeBytes` | | Bilgi ve eksik indirme denetimi için |
| `silentArgs` | | Yalnızca `exe` için ve yalnızca `silentArgsSource` ile birlikte |
| `silentArgsSource` | | Sessiz parametreleri belgeleyen yayıncı sayfası (HTTPS) |

MSI paketleri standart Windows Installer anahtarlarıyla (`/passive /norestart`) kurulur. EXE için sessiz parametre
tanımlı değilse kurulum programı kendi arayüzüyle açılır ve kullanıcı adımları tamamlar.

### Platformlar

Bir program `winget` veya `direct` tanımı varsa **Windows** uygulamasında, `linux` bölümü varsa **Linux** uygulamasında
listelenir (`SoftwareEntry.SupportsWindows` / `SupportsLinux`). Yalnızca `linux` bölümü olan programlar (ör. Timeshift,
Flatseal) Windows uygulamasında hiç görünmez; tersi de geçerlidir.

### `linux`

| Alan | Zorunlu | Açıklama |
| --- | --- | --- |
| `flatpak` | `flatpak` veya en az bir paket listesi | Flathub uygulama kimliği (ters alan adı biçiminde, ör. `org.videolan.VLC`) |
| `apt` | | Debian, Ubuntu, Linux Mint, Pop!_OS… paket adları |
| `dnf` | | Fedora ve RHEL türevleri paket adları |
| `pacman` | | Arch, Manjaro, EndeavourOS… paket adları (yalnızca resmi depolar, AUR değil) |
| `notes.tr` | | Linux'a özgü not (kuyruk ve ayrıntı ekranında gösterilir) |

- Flathub kimliği `^[A-Za-z][A-Za-z0-9_-]*(\.[A-Za-z0-9_-]+){2,}$`, paket adları `^[A-Za-z0-9][A-Za-z0-9+._-]{0,99}$`
  biçiminde olmalıdır; geçersiz bir değer kaydın reddedilmesine yol açar.
- Bir paket listesi **birden fazla paket** içerebilir; program yalnızca hepsi kuruluysa *Kurulu* sayılır ve hepsi tek
  komutla kurulur (ör. derleme araçları: `["build-essential"]`, `["gcc", "gcc-c++", "make"]`, `["base-devel"]`).
- Dağıtımın paket yöneticisi için liste yoksa ve `flatpak` da yoksa program o dağıtımda *desteklenmiyor* olarak gösterilir.
- Varsayılan olarak Flathub tercih edilir (kullanıcı hesabına, yönetici izni olmadan); kullanıcı Ayarlar'dan dağıtım
  deposunu tercih edebilir. Ayrıntılar: [ARCHITECTURE.md](ARCHITECTURE.md#linux-sürümü).
- Yalnızca yayıncının **resmi** Flathub paketlerini veya Flathub'da doğrulanmış (✔) paketleri ve dağıtımların resmi
  depolarındaki paketleri kullan. Kimlikleri Flathub API'sinden (`https://flathub.org/api/v2/appstream/<kimlik>`) ve
  dağıtımın paket aramasından (packages.ubuntu.com, packages.fedoraproject.org, archlinux.org/packages) doğrula.

## Hazır seçim grubu

| Alan | Zorunlu | Açıklama |
| --- | --- | --- |
| `id`, `name.tr` | ✔ | |
| `description.tr`, `icon` | | |
| `software` | ✔ | Program kimlikleri; bilinmeyen kimlikler gruptan çıkarılır ve uyarı verilir |
| `platforms` | | `["windows"]`, `["linux"]` veya ikisi; yoksa grup her iki uygulamada da gösterilir |

Her uygulama gruptaki programlardan yalnızca kendi platformunda kurulabilenleri gösterir; hiçbiri kalmayan grup gizlenir.

## Örnek

```json
{
  "id": "7zip",
  "name": "7-Zip",
  "publisher": "Igor Pavlov",
  "category": "archive",
  "summary": { "tr": "Yüksek sıkıştırma oranlı ücretsiz arşiv aracı." },
  "license": { "tr": "LGPL-2.1 (açık kaynak)" },
  "licenseUrl": "https://7-zip.org/license.txt",
  "icon": { "monogram": "7z", "color": "#3D4C5E" },
  "elevation": "likely",
  "winget": { "id": "7zip.7zip", "publisher": "Igor Pavlov" },
  "direct": {
    "url": "https://www.7-zip.org/a/7z2604-x64.msi",
    "allowedHosts": [ "www.7-zip.org", "github.com", "release-assets.githubusercontent.com" ],
    "installerType": "msi",
    "sha256": "0b01334a418654293513449f61d6bfc99e5196ca371e3c3dc961eda57cd535c6"
  },
  "detection": { "registry": [ { "displayName": "^7-Zip \\d", "publisher": "^Igor Pavlov$" } ] },
  "linux": {
    "apt": [ "7zip" ],
    "dnf": [ "7zip" ],
    "pacman": [ "7zip" ],
    "notes": { "tr": "Linux'ta komut satırı sürümü (7z) kurulur." }
  }
}
```

Yalnızca Linux'ta sunulan bir program:

```json
{
  "id": "flatseal",
  "name": "Flatseal",
  "publisher": "Martin Abente Lahaye",
  "category": "system",
  "summary": { "tr": "Flatpak uygulamalarının izinlerini yönet." },
  "license": { "tr": "GPL-3.0 (açık kaynak)" },
  "icon": { "monogram": "FS", "color": "#4A86CF" },
  "linux": { "flatpak": "com.github.tchx84.Flatseal" }
}
```
