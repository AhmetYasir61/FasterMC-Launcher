using System;
using System.IO;
using System.Text.Json;

namespace FWLauncherV2.Services
{
    /// <summary>
    /// Launcher'ın kendi tuttuğu yerel istatistikler (sunucu API'si istatistik döndürmediği için):
    /// toplam oynama süresi, oyun açılış sayısı, ilk/son giriş ve son oturum süresi. stats.json'da saklanır.
    /// </summary>
    public static class LocalStatsService
    {
        public class Stats
        {
            public double TotalPlaySeconds { get; set; }
            public int LaunchCount { get; set; }
            public int CrashCount { get; set; }
            public DateTime? FirstSeen { get; set; }
            public DateTime? LastLogin { get; set; }
            public DateTime? LastPlayed { get; set; }
            public double LastSessionSeconds { get; set; }
        }

        private static string PathFor(string user)
        {
            var safe = string.Concat((user ?? "oyuncu").ToLowerInvariant().Split(Path.GetInvalidFileNameChars()));
            return System.IO.Path.Combine(SettingsService.LauncherDirectory, "stats", safe + ".json");
        }

        public static event Action? Changed;

        public static Stats Load(string user)
        {
            try
            {
                var p = PathFor(user);
                if (File.Exists(p))
                    return JsonSerializer.Deserialize<Stats>(File.ReadAllText(p)) ?? new Stats();
            }
            catch (Exception ex) { Logger.Warn($"İstatistik okunamadı: {ex.Message}"); }
            return new Stats();
        }

        private static void Save(string user, Stats s)
        {
            try
            {
                var p = PathFor(user);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
                File.WriteAllText(p, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { Logger.Warn($"İstatistik yazılamadı: {ex.Message}"); }
            try { Changed?.Invoke(); } catch { }
        }

        public static void MarkLogin(string user)
        {
            var s = Load(user);
            s.FirstSeen ??= DateTime.Now;
            s.LastLogin = DateTime.Now;
            Save(user, s);
        }

        public static void MarkLaunch(string user)
        {
            var s = Load(user);
            s.FirstSeen ??= DateTime.Now;
            s.LaunchCount++;
            s.LastPlayed = DateTime.Now;
            Save(user, s);
        }

        public static void AddSession(string user, TimeSpan duration, bool crashed)
        {
            if (duration.TotalSeconds < 1) return;
            var s = Load(user);
            s.TotalPlaySeconds += duration.TotalSeconds;
            s.LastSessionSeconds = duration.TotalSeconds;
            if (crashed) s.CrashCount++;
            Save(user, s);
        }

        public static string FormatHours(double seconds)
        {
            var h = seconds / 3600.0;
            if (h >= 10) return $"{h:0} sa";
            if (h >= 1) return $"{h:0.0} sa";
            return $"{Math.Round(seconds / 60.0):0} dk";
        }

        public static string FormatRelative(DateTime? when)
        {
            if (when == null) return "—";
            var d = DateTime.Now.Date - when.Value.Date;
            if (d.TotalDays < 1) return "Bugün";
            if (d.TotalDays < 2) return "Dün";
            if (d.TotalDays < 7) return $"{(int)d.TotalDays} gün önce";
            return when.Value.ToString("dd.MM.yyyy");
        }

        public static string FormatSince(DateTime? when)
        {
            if (when == null) return "Yeni";
            var d = DateTime.Now - when.Value;
            if (d.TotalDays >= 365) return $"{(int)(d.TotalDays / 365)} yıl";
            if (d.TotalDays >= 30) return $"{(int)(d.TotalDays / 30)} ay";
            if (d.TotalDays >= 1) return $"{(int)d.TotalDays} gün";
            return "Bugün";
        }
    }
}
