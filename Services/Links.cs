using System;
using System.Diagnostics;
using FWLauncherV2.Dialogs;

namespace FWLauncherV2.Services
{
    /// <summary>Dış bağlantıları (Discord, web, YouTube, klasörler) güvenle açar.</summary>
    public static class Links
    {
        public static void Open(string? url, string whatForMessage = "Bu bağlantı")
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                FWDialog.Info($"{whatForMessage} henüz ayarlanmamış. Yetkililer Panel → Bağlantılar'dan ekleyebilir.", "Bağlantı yok");
                return;
            }
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Logger.Warn($"Bağlantı açılamadı ({url}): {ex.Message}"); }
        }

        public static void OpenFolder(string path)
        {
            try
            {
                System.IO.Directory.CreateDirectory(path);
                Process.Start("explorer.exe", $"\"{path}\"");
            }
            catch (Exception ex) { Logger.Warn($"Klasör açılamadı ({path}): {ex.Message}"); }
        }
    }
}
