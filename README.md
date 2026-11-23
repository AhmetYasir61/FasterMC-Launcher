# FasterMC Launcher

FasterMC Dungeons [MMORPG] Minecraft sunucusunun resmi launcher'ı (Windows, .NET / WPF).

- Modpaketini (CurseForge biçimi) indirir, kurar, günceller ve oyunu başlatır
- Güncelleme baloncuğu: launcher ve modpaketi güncellemelerini otomatik bildirir
- `%AppData%\FasterMCLauncher` altına kurulur; Windows "Yüklü uygulamalar"dan kaldırılabilir / onarılabilir
- Tek kopya çalışır

**İndir:** https://firewing.store/fmc/indir/

## Kod imzalama

Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).
Ayrıntılar: [CODE_SIGNING.md](CODE_SIGNING.md) · Gizlilik politikası: https://firewing.store/fmc/gizlilik/

## Derleme

```
dotnet publish -c Release -p:PublishProfile=win-x64-single
```

Gizli değerler (CurseForge API anahtarı, harita adresi) depoda **yoktur**. Kendi derlemeniz için
`Gizli/Gizli.ornek.cs` dosyasını `Gizli/Gizli.local.cs` olarak kopyalayıp doldurun.

## Lisans

[MIT](LICENSE) © 2020-2026 Ametio (AhmetYasir61). FasterMC bu launcher'ı sahibinin izniyle kullanır.
