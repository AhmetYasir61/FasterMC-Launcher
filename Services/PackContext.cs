using System;
using System.Diagnostics;
using FWLauncherV2.Models;

namespace FWLauncherV2.Services
{
    /// <summary>
    /// Seçili paketin ve sunucunun anlık durumu. Oyna sayfası (MainView) doldurur; üst çubuk, Modlar,
    /// Mağaza ve Hesap sayfaları buradan okur (MC sürümü / mod yükleyici uyumluluk filtresi için).
    /// </summary>
    public static class PackContext
    {
        public static ModpackInfo? Pack { get; private set; }
        /// <summary>Paketin kurulu olduğu oyun klasörü (mods/, usermods/, shaderpacks/ burada).</summary>
        public static string PackDir { get; private set; } = SettingsService.LauncherDirectory;
        public static string McVersion { get; private set; } = "";
        /// <summary>"neoforge" | "forge" | "fabric" | "quilt" | "horizon" | "" (Modrinth/CurseForge filtre adı).</summary>
        public static string LoaderKey { get; private set; } = "";
        public static string LoaderDisplay { get; private set; } = "";

        public static bool? ServerOnline { get; private set; }
        public static int OnlinePlayers { get; private set; }
        public static int MaxPlayers { get; private set; }

        public static event Action? PackChanged;
        public static event Action? ServerChanged;

        public static void SetPack(ModpackInfo? pack, string packDir, string mcVersion, string loaderDisplay)
        {
            Pack = pack;
            PackDir = string.IsNullOrWhiteSpace(packDir) ? SettingsService.LauncherDirectory : packDir;
            McVersion = mcVersion ?? "";
            LoaderDisplay = loaderDisplay ?? "";
            string l = LoaderDisplay.ToLowerInvariant();
            LoaderKey = l.Contains("horizon") ? "horizon"
                      : l.Contains("neoforge") ? "neoforge"
                      : l.Contains("forge") ? "forge"
                      : l.Contains("fabric") ? "fabric"
                      : l.Contains("quilt") ? "quilt" : "";
            Raise(PackChanged);
        }

        public static void SetServer(bool? online, int players, int max)
        {
            ServerOnline = online;
            OnlinePlayers = players;
            MaxPlayers = max;
            Raise(ServerChanged);
        }

        private static void Raise(Action? a)
        {
            try { a?.Invoke(); } catch (Exception ex) { Debug.WriteLine(ex); }
        }
    }
}
