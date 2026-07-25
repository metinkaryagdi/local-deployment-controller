# Local Deployment Controller — Kurulum

Bu klasör **kendi kendine yeten** bir pakettir. Hedef makinede **.NET kurulu olmasına gerek yoktur**;
çalışma zamanı `DeployController.exe` içine gömülüdür.

## 1. Ön koşullar (hedef makinede)

| Gerekli | Neden |
| --- | --- |
| **Windows 10/11 x64** | Paket `win-x64` olarak derlendi |
| **Docker Desktop** (WSL2 arka ucu) | Projeleri derleyip çalıştıran motor |
| **Git for Windows** | Depoları klonlayan araç — `git` PATH üzerinde olmalı |

Docker Desktop ayarlarında **Settings → General → Start Docker Desktop when you sign in**
açık olsun; kontrolcü, Docker olmadan panel açar ama dağıtım yapamaz.

## 2. Kopyala ve kur

1. Zip'i hedef makinede kalıcı bir yere açın (örn. `C:\LocalDeploymentController`).
   Masaüstü/İndirilenler yerine kalıcı bir klasör seçin — otomatik başlatma görevi
   bu yolu kaydeder, sonradan taşırsanız görev çalışmaz.
2. **Dosya internet üzerinden geldiyse** (e-posta, Drive, WeTransfer) Windows onu
   engellemiş olabilir. Klasörde PowerShell açıp bir kez şunu çalıştırın:

   ```bash
   Get-ChildItem -Recurse | Unblock-File
   ```

3. O klasörde **PowerShell'i yönetici olarak** açın (güvenlik duvarı kuralı için gerekir).
4. Betiği çalıştırın:

```bash
powershell -ExecutionPolicy Bypass -File .\setup.ps1
```

Betik sırasıyla şunları yapar:

- `git` ve `docker`'ı doğrular, Docker daemon'ı yanıt veriyor mu bakar
- `C:\Deployments` klasörünü oluşturur
- `appsettings.json` içindeki portu ve dağıtım klasörünü ayarlar
- TCP 5000 için gelen bağlantı güvenlik duvarı kuralı ekler
- Oturum açıldığında otomatik başlaması için **Zamanlanmış Görev** oluşturur

Farklı port veya klasör isterseniz:

```bash
powershell -ExecutionPolicy Bypass -File .\setup.ps1 -Port 5050 -BaseDirectory D:\Deployments
```

Otomatik başlatma veya güvenlik duvarı istemiyorsanız: `-SkipAutoStart`, `-SkipFirewall`.

## 3. Çalıştır

```bash
.\start.bat
```

Ya da oturumu kapatıp açın — zamanlanmış görev kontrolcüyü kendisi başlatır.

Panel adresleri:

- Aynı makinede: `http://localhost:5000`
- Ağdaki başka bir bilgisayardan: `http://<hedef-makine-ip>:5000`

IP'yi `ipconfig` ile ya da `setup.ps1` çıktısının **Özet** bölümünden görebilirsiniz.

## 4. Ağ profili uyarısı

Windows, **Public** olarak işaretlenmiş ağlarda gelen bağlantıları varsayılan olarak engeller.
`setup.ps1` bunu tespit edip uyarır. Ev/ofis ağınız Public görünüyorsa, yönetici PowerShell'de:

```bash
Set-NetConnectionProfile -InterfaceAlias "Ethernet" -NetworkCategory Private
```

## 5. Dağıttığınız projelerin portları

Kontrolcü yalnızca kendi portunu (5000) güvenlik duvarına ekler. Dağıttığınız bir uygulamaya
ağdan erişmek istiyorsanız onun portu için de kural gerekir:

```bash
New-NetFirewallRule -DisplayName "LDC app 8080" -Direction Inbound -Protocol TCP -LocalPort 8080 -Action Allow -Profile Private
```

## 6. Kullanım

Panelde soldaki formu doldurun:

| Alan | Açıklama |
| --- | --- |
| **Project name** | Küçük harfli slug — hem klasör hem compose proje adı olur |
| **Git repository URL** | **Hedef makineden erişilebilir** olmalı (HTTPS, SSH veya yerel yol) |
| **Branch** | Varsayılan `main` |
| **Host port** | Uygulamanın bu makinede yayınlanacağı port |
| **Environment variables** | Depo köküne `.env` olarak aynen yazılır |

Depo kökünde **ya** bir compose dosyası (`docker-compose.yml` / `compose.yml`) **ya da** bir
`Dockerfile` bulunmalıdır. Yalnızca `Dockerfile` varsa kontrolcü compose dosyasını kendisi üretir;
container portunu `.env` içindeki `PORT` / `APP_PORT` / `SERVER_PORT` değerinden alır.

Kendi compose dosyanızı yazıyorsanız host portunu `${HOST_PORT}` ile kullanabilirsiniz —
formdaki port değeri `.env` dosyasına `HOST_PORT=` olarak eklenir.

## 7. Özel (private) depolar

Kontrolcü kimlik doğrulama istemi çıkarmaz; parola soran bir klonlama hemen hata verir
(sonsuza kadar beklemesin diye böyle tasarlandı). Private depo için hedef makinede önceden:

- **HTTPS:** Git Credential Manager ile bir kez `git clone` yapıp kimlik bilgisini kaydedin, ya da
- **SSH:** anahtarı `%USERPROFILE%\.ssh` altına koyup URL'i `git@github.com:kullanici/repo.git` verin.

## 8. Kaldırma

```bash
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

Zamanlanmış görevi, güvenlik duvarı kuralını ve çalışan süreci kaldırır.
**Dağıtılmış projelere ve container'lara dokunmaz** — onları önce panelden silin.

## 9. Güvenlik

Panelde **kimlik doğrulama yoktur** ve tasarımı gereği verdiğiniz deponun `Dockerfile`'ını
bu makinede çalıştırır. Yani panele erişebilen herkes makinede kod çalıştırabilir.
Sadece güvendiğiniz yerel ağda kullanın, internete açmayın.

## Sorun giderme

| Belirti | Bakılacak yer |
| --- | --- |
| Öbür PC'den açılmıyor | Güvenlik duvarı kuralı + ağ profili (Public/Private), `Test-NetConnection <ip> -Port 5000` |
| Panel açılıyor, dağıtım "docker" hatası veriyor | Docker Desktop çalışıyor mu — `docker version` |
| Klonlama hemen hata veriyor | Private depo kimlik bilgisi (bkz. bölüm 7) veya yanlış branch adı |
| "neither a docker-compose file nor a Dockerfile" | Depo kökünde ikisinden biri yok |
| Silme "klasör kilitli" diyor | Bir editör/terminal o klasörde açık; kapatıp tekrar deneyin |
| Port çakışması | Aynı host portunu iki projeye vermeyin; `docker ps` ile kontrol edin |
