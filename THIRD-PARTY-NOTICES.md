# Üçüncü taraf bildirimleri

DeployNest aşağıdaki üçüncü taraf bileşenleri kullanır. Lisans metinleri ilgili projelerin depolarında bulunur.

## Uygulamaya dahil edilen bileşenler

| Bileşen | Lisans | Kaynak |
| --- | --- | --- |
| .NET 10 çalışma zamanı ve WPF (self-contained EXE içinde) | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| Microsoft.Extensions.DependencyInjection | MIT | https://github.com/dotnet/runtime |
| Microsoft.Extensions.Logging | MIT | https://github.com/dotnet/runtime |

### Yalnızca Linux sürümünde

| Bileşen | Lisans | Kaynak |
| --- | --- | --- |
| .NET 10 çalışma zamanı (self-contained, Linux) | MIT | https://github.com/dotnet/runtime |
| Avalonia UI (Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.X11, Avalonia.Skia, Avalonia.FreeDesktop) | MIT | https://github.com/AvaloniaUI/Avalonia |
| Inter yazı tipi (Avalonia.Fonts.Inter) | SIL Open Font License 1.1 | https://github.com/rsms/inter |
| SkiaSharp ve HarfBuzzSharp (yerel `libSkiaSharp.so`, `libHarfBuzzSharp.so` dahil) | MIT | https://github.com/mono/SkiaSharp |
| Skia | BSD-3-Clause | https://skia.org |
| HarfBuzz | MIT (“Old MIT”) | https://github.com/harfbuzz/harfbuzz |
| Tmds.DBus.Protocol | MIT | https://github.com/tmds/Tmds.DBus |
| MicroCom.Runtime | MIT | https://github.com/kekekeks/MicroCom |
| Fluent UI System Icons (arayüz simgeleri, vektör yolları olarak) | MIT | https://github.com/microsoft/fluentui-system-icons |

## Program logoları

Program logoları ilgili markaların sahiplerine aittir ve yalnızca programları tanıtmak için gösterilir.
Logoların bir kısmı [homarr-labs/dashboard-icons](https://github.com/homarr-labs/dashboard-icons) koleksiyonundan
(Apache License 2.0) alınmıştır; diğerleri Flathub'daki resmi uygulama simgelerinden, yayıncıların resmi depolarından
veya sitelerinden alınmıştır.
Dosya bazında kaynak listesi: [assets/icons/apps/SOURCES.md](assets/icons/apps/SOURCES.md).

## Yalnızca geliştirmede kullanılanlar (dağıtılmaz)

| Bileşen | Lisans |
| --- | --- |
| xUnit | Apache-2.0 |
| xunit.runner.visualstudio | Apache-2.0 |
| Microsoft.NET.Test.Sdk | MIT |
| coverlet.collector | MIT |

## Çalışma zamanında kullanılan harici araçlar

| Araç | Not |
| --- | --- |
| Windows Package Manager (winget) | Microsoft tarafından Windows ile (App Installer) dağıtılır; DeployNest'e dahil edilmez. MIT lisanslı kaynak: https://github.com/microsoft/winget-cli |
| Flatpak ve Flathub | Linux'ta programları kurmak için kullanılır; dağıtımın deposundan kurulur, DeployNest'e dahil edilmez. https://flatpak.org, https://flathub.org |
| apt, dnf, pacman, pkexec (polkit) | Linux dağıtımının kendi araçlarıdır; DeployNest'e dahil edilmez. |
