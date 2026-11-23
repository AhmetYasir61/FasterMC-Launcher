using System;
using System.IO;
using System.Management;
using System.Text.Json;
using FWLauncherV2.Models;

namespace FWLauncherV2.Services
{
    /// <summary>
    /// Kullanıcı ayarlarının okunup yazılmasından ve sistem RAM bilgisinden sorumlu
    /// merkezi servis. Daha önce ayarları okumak için tüm bir SettingsView UserControl'ü
    /// oluşturuluyordu; bu servis o bağımlılığı ortadan kaldırır.
    /// </summary>
    public static class SettingsService
    {
        public static string LauncherDirectory { get; } = ResolveLauncherDirectory();

        /// <summary>
        /// Veri klasörü %AppData%\FireWingLauncher — yoksa sıfırdan oluşturulur. Eski %AppData%\PokeWingLauncher
        /// klasörüne dokunulmaz (taşınmaz, silinmez).
        /// </summary>
        private static string ResolveLauncherDirectory()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FasterMCLauncher");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        public static string SettingsPath { get; } = Path.Combine(LauncherDirectory, "user_settings.json");

        public const int MinRamGB = 4;

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static int _cachedTotalRamMb;

        /// <summary>Ayarları diskten okur; dosya yok/bozuksa varsayılanlarla oluşturup kaydeder.</summary>
        public static UserSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var settings = JsonSerializer.Deserialize<UserSettings>(json);
                    if (settings != null)
                        return Normalize(settings);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Ayarlar okunamadı, varsayılanlar kullanılacak: {ex.Message}");
            }

            var fresh = Normalize(new UserSettings());
            try { Save(fresh); } catch { /* ilk kayıt başarısız olsa da devam et */ }
            return fresh;
        }

        public static void Save(UserSettings settings)
        {
            Directory.CreateDirectory(LauncherDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }

        /// <summary>RAM ve kullanıcı adı değerlerini geçerli aralığa çeker.</summary>
        public static UserSettings Normalize(UserSettings s)
        {
            int max = MaxRamGB();
            if (s.LastRamInGB < MinRamGB) s.LastRamInGB = MinRamGB;
            if (s.LastRamInGB > max) s.LastRamInGB = max;
            if (string.IsNullOrWhiteSpace(s.LastUsername)) s.LastUsername = "Oyuncu";
            return s;
        }

        /// <summary>Toplam fiziksel RAM (MB). WMI sonucu önbelleğe alınır.</summary>
        public static int TotalRamMb()
        {
            if (_cachedTotalRamMb > 0)
                return _cachedTotalRamMb;

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                foreach (ManagementObject result in searcher.Get())
                {
                    var totalBytes = (ulong)result["TotalPhysicalMemory"];
                    _cachedTotalRamMb = (int)(totalBytes / 1024 / 1024);
                    return _cachedTotalRamMb;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Toplam RAM tespit edilemedi: {ex.Message}");
            }

            _cachedTotalRamMb = 8192; // güvenli varsayılan (8 GB)
            return _cachedTotalRamMb;
        }

        /// <summary>Önerilen üst sınır: toplam RAM'in yarısı, en az 4 GB.</summary>
        public static int RecommendedMaxRamGB() => Math.Max(MinRamGB, TotalRamMb() / 1024 / 2);

        /// <summary>Seçilebilecek en yüksek değer: toplam RAM'in tamamı (GB'a yuvarlanır; Windows'un ayırdığı
        /// kısım yüzünden 8 GB'lık sistem ~7.8 GB görünür, bu 8 olarak gösterilir).</summary>
        public static int MaxRamGB() => Math.Max(MinRamGB, (int)Math.Round(TotalRamMb() / 1024.0));

        /// <summary>0 = güvenli (yarıya kadar), 1 = yüksek (yarıdan fazla), 2 = tehlikeli (toplam-1 GB ve üstü).</summary>
        public static int RamRiskLevel(int selectedGB)
        {
            if (selectedGB >= MaxRamGB() - 1) return 2;
            if (selectedGB > RecommendedMaxRamGB()) return 1;
            return 0;
        }
    }
}
