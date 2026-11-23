# FasterMC Launcher

FasterMC Dungeons [MMORPG] Minecraft sunucusunun resmi launcher'ı (Windows, .NET / WPF).

- Modpaketini (CurseForge biçimi) indirir, kurar, günceller ve oyunu başlatır
- Güncelleme baloncuğu: launcher ve modpaketi güncellemelerini otomatik bildirir
- `%AppData%\FasterMCLauncher` altına kurulur; Windows "Yüklü uygulamalar"dan kaldırılabilir / onarılabilir
- Tek kopya çalışır

**İndir:** https://firewing.store/fmc/indir/FasterMCLauncher.exe

## Derleme

```
dotnet publish -c Release -p:PublishProfile=win-x64-single
```

Gizli değerler (CurseForge API anahtarı, harita adresi) depoda **yoktur**. Kendi derlemeniz için
`Gizli/Gizli.ornek.cs` dosyasını `Gizli/Gizli.local.cs` olarak kopyalayıp doldurun.

## Lisans

[MIT](LICENSE) © 2026 FasterMC
