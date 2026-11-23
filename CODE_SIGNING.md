# Code signing policy / Kod imzalama politikası

Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

Ücretsiz kod imzalama [SignPath.io](https://signpath.io) tarafından sağlanır, sertifika [SignPath Vakfı](https://signpath.org)'na aittir.

## Ne imzalanır

Yalnızca bu depodaki kaynak koddan, GitHub Actions (`.github/workflows/yayin.yml`) üzerinde derlenen
`FasterMCLauncher.exe` imzalanır. Yerel derlemeler ve üçüncü taraf dosyalar imzalanmaz.

**İndirme sayfası:** https://firewing.store/fmc/indir/

## Ekip ve roller

| Rol | Kişi |
|---|---|
| Committers and reviewers (kod yazar / inceler) | [AhmetYasir61](https://github.com/AhmetYasir61) |
| Approvers (her imzalama isteğini onaylar) | [AhmetYasir61](https://github.com/AhmetYasir61) |

Depoya yazma yetkisi olan herkes iki aşamalı doğrulama (2FA) kullanır. Her imzalama isteği SignPath'te elle onaylanır.

## Gizlilik / Privacy

Privacy policy / Gizlilik politikası: **https://firewing.store/fmc/gizlilik/**

The launcher communicates with `firewing.store` for account login, game installation and updates, downloads mods from
CurseForge / Modrinth / Forge, and sends an automatic security report (username, device ID, reason) if it detects that its
own files were tampered with. All collected data and third parties are listed in the privacy policy.

Launcher; hesap girişi, oyun kurulumu ve güncellemeler için `firewing.store` ile iletişim kurar, modları CurseForge / Modrinth / Forge'dan
indirir ve kendi dosyalarına müdahale tespit ederse otomatik güvenlik bildirimi (kullanıcı adı, cihaz kimliği, neden) gönderir.
Toplanan tüm veriler ve üçüncü taraflar gizlilik politikasında listelenmiştir.
