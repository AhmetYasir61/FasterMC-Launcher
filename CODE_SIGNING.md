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

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

Bu program, kullanıcı ya da onu kuran / çalıştıran kişi açıkça istemedikçe başka ağ sistemlerine hiçbir bilgi aktarmaz.
Launcher yalnızca oyunun kurulumu, güncellenmesi ve hesap girişi için `firewing.store` ile iletişim kurar ve modları CurseForge'dan indirir.
