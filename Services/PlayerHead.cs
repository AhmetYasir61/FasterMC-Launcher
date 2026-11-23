using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FWLauncherV2.Services
{
    /// <summary>Avatar kutularındaki oyuncu kafası (kullanıcı adına göre mc-heads.net); yüklenemezse baş harf görünür.</summary>
    public static class PlayerHead
    {
        private static readonly Dictionary<string, BitmapImage> RemoteHeads = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Avatar kutusunu oyuncu kafasıyla doldurur. Uzak görsel kullanıcı başına önbelleğe alınır.</summary>
        public static void Apply(System.Windows.Controls.Image img, UIElement fallback, string? user)
        {
            void Show(ImageSource? src)
            {
                img.Source = src;
                img.Visibility = src != null ? Visibility.Visible : Visibility.Collapsed;
                fallback.Visibility = src != null ? Visibility.Collapsed : Visibility.Visible;
            }

            if (string.IsNullOrWhiteSpace(user) || user == "Oyuncu") { Show(null); return; }

            try
            {
                if (!RemoteHeads.TryGetValue(user, out var bi))
                {
                    bi = new BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = new Uri($"https://mc-heads.net/avatar/{Uri.EscapeDataString(user)}/128");
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    RemoteHeads[user] = bi;
                }
                if (!bi.IsDownloading) { Show(bi); return; }
                Show(null);   // inene dek baş harf
                bi.DownloadCompleted += (s, e) => Show(bi);
                bi.DownloadFailed += (s, e) => { RemoteHeads.Remove(user); Logger.Warn($"Oyuncu kafası indirilemedi: {e.ErrorException?.Message}"); };
            }
            catch (Exception ex)
            {
                Logger.Warn($"Oyuncu kafası yüklenemedi: {ex.Message}");
                Show(null);
            }
        }
    }
}
