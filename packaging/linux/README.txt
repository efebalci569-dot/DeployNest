DeployNest — Linux
==================

Yeni bilgisayarın veya temiz kurulum sonrası ihtiyacın olan programları seç ve tek seferde kur.
Programlar Flathub'dan (kullanıcı hesabına, yönetici izni olmadan) veya dağıtımının resmi paket
deposundan (apt, dnf, pacman) doğrulanarak kurulur.

Kurulum (yönetici izni gerekmez)
--------------------------------
  tar -xzf DeployNest-<sürüm>-linux-x64.tar.gz
  cd DeployNest-<sürüm>-linux-x64
  ./install.sh

Ardından DeployNest'i uygulama menüsünden veya terminalde "deploynest" komutuyla aç.
Kurmadan denemek için klasördeki ./DeployNest dosyasını çalıştırabilirsin.

Kaldırma: ~/.local/opt/deploynest/uninstall.sh  (ayarlar ve geçmiş de silinsin: --purge)

Gereksinimler
-------------
- 64 bit Linux (x86_64 veya ARM64), X11 veya Wayland (XWayland) masaüstü
- Desteklenen paket yöneticileri: apt (Debian, Ubuntu, Linux Mint, Pop!_OS…), dnf (Fedora, RHEL türevleri),
  pacman (Arch, Manjaro, EndeavourOS…). Diğer dağıtımlarda yalnızca Flathub programları kurulabilir.
- Flatpak yoksa DeployNest onu tek tıkla dağıtımının deposundan kurabilir.
- Dağıtım paketleri için pkexec (polkit) gerekir; yönetici parolan kurulum başına bir kez sorulur.
- libicu (Ubuntu, Fedora, Arch masaüstlerinde varsayılan olarak kuruludur).

Ayarlar, kurulum geçmişi ve günlükler: ~/.local/share/DeployNest

Kaynak kodu ve sorun bildirimi: https://github.com/efebalci569-dot/DeployNest
Lisans: MIT (LICENSE dosyasına bakın). Kurulan programlar kendi yayıncılarının lisanslarına tabidir.
