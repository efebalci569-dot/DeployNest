# Üçüncü taraf bildirimleri

DeployNest aşağıdaki üçüncü taraf bileşenleri kullanır. Lisans metinleri ilgili projelerin depolarında bulunur.

## Uygulamaya dahil edilen bileşenler

| Bileşen | Lisans | Kaynak |
| --- | --- | --- |
| .NET 10 çalışma zamanı ve WPF (self-contained EXE içinde) | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| Microsoft.Extensions.DependencyInjection | MIT | https://github.com/dotnet/runtime |
| Microsoft.Extensions.Logging | MIT | https://github.com/dotnet/runtime |

## Program logoları

Program logoları ilgili markaların sahiplerine aittir ve yalnızca programları tanıtmak için gösterilir.
Logoların bir kısmı [homarr-labs/dashboard-icons](https://github.com/homarr-labs/dashboard-icons) koleksiyonundan
(Apache License 2.0) alınmıştır; diğerleri yayıncıların resmi depolarından veya sitelerinden alınmıştır.
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
