using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FWLauncherV2.Models
{
    // ================= USER SETTINGS =================
    public class UserSettings
    {
        public string LastUsername { get; set; } = "Oyuncu";
        public string? SessionToken { get; set; }
        public string? DeviceId { get; set; }
        public int LastRamInGB { get; set; } = 6;   // min sistem şartı 6 GB — varsayılan ayırma da 6
        public string? JavaPath { get; set; }

        // Son seçilen mod paketi (modpacks.json'daki Id) — sürüm seçici bunu hatırlar.
        public string? SelectedPackId { get; set; }

        // ---- Pencere (oyun) ----
        public bool FullScreen { get; set; }
        public int GameWidth { get; set; } = 854;
        public int GameHeight { get; set; } = 480;
        public bool MinimizeOnLaunch { get; set; } = true;
        public bool RestoreOnExit { get; set; } = true;

        // ---- Java ----
        /// <summary>Kullanıcının eklediği ek JVM bayrakları (boşlukla ayrılır).</summary>
        public string? ExtraJvmArgs { get; set; }

        // ---- Modlar ----
        /// <summary>usermods/ klasöründeki açık modlar oyuna eklensin mi (ana paket her zaman yüklenir).</summary>
        public bool UserModsEnabled { get; set; } = true;
        /// <summary>CurseForge mağazası için kullanıcı anahtarı (boşsa launcher'ın gömülü anahtarı kullanılır).</summary>
        public string? CurseForgeApiKey { get; set; }

        // ---- Görünüm ----
        public bool PageColors { get; set; } = true;
        public bool ShowNewsStrip { get; set; } = true;
        /// <summary>Oyun hata koduyla kapanırsa günlüğü açmayı öner.</summary>
        public bool CrashHelper { get; set; } = true;
    }


    // ================= LOCAL PACK =================
    public class LocalPackInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
    }

    // ================= MODPACK INFO =================
    public class ModpackInfo
    {
        [JsonPropertyName("Id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("Name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("Version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("ZipUrl")]
        public string ZipUrl { get; set; } = string.Empty;

        [JsonPropertyName("ForgeInstallerUrl")]
        public string? ForgeInstallerUrl { get; set; }

        // Yeni: Forge/NeoForge için açık yükleyici URL'si (ForgeInstallerUrl'den önceliklidir).
        [JsonPropertyName("LoaderInstallerUrl")]
        public string? LoaderInstallerUrl { get; set; }

        [JsonPropertyName("JavaUrl")]
        public string? JavaUrl { get; set; }

        [JsonPropertyName("Description")]
        public string? Description { get; set; }

        [JsonPropertyName("ServerIp")]
        public string? ServerIp { get; set; }

        // Sunucu portu (varsayılan 25565).
        [JsonPropertyName("ServerPort")]
        public int ServerPort { get; set; } = 25565;

        // Ana ekranda rozet için (örn. "NeoForge"). Opsiyonel; manifesttten de türetilebilir.
        [JsonPropertyName("Loader")]
        public string? Loader { get; set; }

        // Ana ekranda rozet için (örn. "1.21.1"). Opsiyonel.
        [JsonPropertyName("McVersion")]
        public string? McVersion { get; set; }

        // Sunucu modu ("cobblemon" | "roleplay") — oyun içi menü teması bu ayara göre değişir.
        // Doluysa launcher packDir/config/pokewing_mode.json'a kilitli yazar; boşsa mod site ayarını kullanır.
        [JsonPropertyName("Mode")]
        public string? Mode { get; set; }

        // Oyun motoru: boş/"java" = Mojang'ın Java oyunu + mod yükleyici, "horizon" = Rust motoru (JVM yok).
        [JsonPropertyName("Engine")]
        public string? Engine { get; set; }

        // Horizon sürüm etiketi (ör. "nightly-2026-10-30"). Boşsa en yeni tarihli Nightly kullanılır.
        [JsonPropertyName("HorizonTag")]
        public string? HorizonTag { get; set; }

        // Kart: oyun ici gorseller (dongu) ve kart arka plani (URL ya da pack:// yolu).
        [JsonPropertyName("Gorseller")]
        public List<string>? Gorseller { get; set; }

        [JsonPropertyName("Arkaplan")]
        public string? Arkaplan { get; set; }

        [JsonIgnore]
        public bool IsHorizon => string.Equals(Engine, "horizon", System.StringComparison.OrdinalIgnoreCase);
    }

    // ================= MANIFEST =================
    public class Manifest
    {
        [JsonPropertyName("minecraft")]
        public MinecraftInfo Minecraft { get; set; } = new();

        [JsonPropertyName("files")]
        public List<ManifestFile> Files { get; set; } = new();
    }

    public class MinecraftInfo
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("modLoaders")]
        public List<ModLoader> ModLoaders { get; set; } = new();
    }

    public class ModLoader
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("primary")]
        public bool Primary { get; set; }
    }

    public class ManifestFile
    {
        [JsonPropertyName("projectID")]
        public long ProjectID { get; set; }

        [JsonPropertyName("fileID")]
        public long FileID { get; set; }
    }

    // ================= API RESPONSES =================
    public class ApiResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    public class LoginApiResponse : ApiResponse
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("token")]
        public string Token { get; set; } = string.Empty;
    }

    // Oturum kontrolü için
    public class SessionCheckResponse : ApiResponse
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;
    }
}
