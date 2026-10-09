# Güvenlik politikası

## Desteklenen sürümler

| Sürüm | Destek |
| --- | --- |
| En güncel yayımlanmış sürüm | ✅ |
| Daha eski sürümler | ❌ (lütfen güncelle) |

## Güvenlik açığı bildirme

DeployNest bilgisayarlara yazılım indirip kurduğu için güvenlik raporlarını ciddiye alıyoruz.

- **Herkese açık issue açma.** Bunun yerine GitHub'ın özel bildirim özelliğini kullan:
  [Security → Report a vulnerability](https://github.com/efebalci569-dot/DeployNest/security/advisories/new).
- Raporunda etkilenen sürümü, yeniden üretme adımlarını ve mümkünse etkisini açıkla.
- Raporları en kısa sürede inceleyip sonucu sana bildireceğiz. Düzeltme yayımlanana kadar ayrıntıları paylaşmamanı rica ederiz.

Özellikle ilgilendiğimiz konular:

- Katalog verisi veya ağ yanıtları üzerinden **komut/argüman enjeksiyonu**
- Doğrulama atlatma: SHA-256, Authenticode imzası, yayıncı eşleşmesi veya izinli sunucu listesi kontrollerinin aşılması
- Bir dosyanın doğrulanmadan çalıştırılması, indirme klasörü dışına yazma (path traversal)
- Yetki yükseltme veya istem dışı yönetici işlemi
- Günlüklere hassas bilgi sızması

## Güvenlik tasarımı (özet)

- Kurulumlar resmi `winget` kaynağından, `--exact` paket kimliğiyle yapılır; paket yayıncısı katalogla eşleşmezse kurulum yapılmaz.
- Doğrudan indirmeler yalnızca HTTPS ve katalogda izin verilen sunuculardan yapılır; yönlendirmeler tek tek denetlenir.
  Dosyalar SHA-256 ve/veya Authenticode (WinVerifyTrust + beklenen yayıncı) doğrulanmadan çalıştırılmaz.
- Süreçler kabuk kullanılmadan, ayrı argümanlarla başlatılır; paket kimlikleri ve argümanlar biçim denetiminden geçer.
- Uygulama yönetici olarak çalışmaz (`asInvoker`); yönetici izni gerekiyorsa yalnızca ilgili kurulum programı UAC ile ister.
- Kullanıcı verisi toplanmaz; günlüklerde kullanıcı adı ve profil yolu maskelenir.

Ayrıntılar: [README → Güvenlik yaklaşımı](README.md#güvenlik-yaklaşımı)

## Kapsam dışı

- Kurulan üçüncü taraf programların kendi güvenlik açıkları (lütfen ilgili yayıncıya bildir).
- winget'in veya Windows'un kendi açıkları (Microsoft'a bildir).
- Kod imzası olmayan EXE için SmartScreen uyarısı (bilinen sınırlama).
