using FWLauncherV2.Dialogs;
using FWLauncherV2.Models;
using FWLauncherV2.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FWLauncherV2.Views
{
    /// <summary>Haberler: duyurular, etkinlikler ve kampanyalar (Panel → Duyurular'dan yönetilir).</summary>
    public partial class ReklamlarView : UserControl
    {
        // Sayfa açıkken arka planda yeni duyuru yayınlanırsa otomatik gelsin diye periyodik yoklama.
        private readonly DispatcherTimer _poll;
        private List<AdItem> _all = new();
        private string _filter = "Tümü";
        private AdItem? _featured;

        private static readonly (string a, string b)[] Gradients =
        {
            ("#FFFF9F3A", "#FF8F5417"), ("#FFB89914", "#FF33312E"), ("#FFEC8C28", "#FF5B340B"),
            ("#FFF59E0B", "#FF5B3A06"), ("#FFF68A19", "#FF6B3C0B"), ("#FFC5A622", "#FF2C2B28")
        };

        public ReklamlarView()
        {
            InitializeComponent();

            _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _poll.Tick += async (s, e) => await LoadAsync();

            // Sayfaya her girişte (navigasyon) yeniden yükle — yayınlanan duyuru anında görünsün.
            Loaded += async (s, e) => { _poll.Start(); await LoadAsync(); };
            Unloaded += (s, e) => _poll.Stop();
        }

        private async Task LoadAsync()
        {
            try
            {
                _all = await ShopService.GetAdsAsync();
                Render();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Haberler yüklenemedi: {ex.Message}");
            }
        }

        private void Render()
        {
            var cats = new List<string> { "Tümü" };
            cats.AddRange(_all.Select(a => a.Badge?.Trim() ?? "").Where(b => b.Length > 0)
                              .Distinct(StringComparer.CurrentCultureIgnoreCase));
            if (!cats.Contains(_filter, StringComparer.CurrentCultureIgnoreCase)) _filter = "Tümü";
            FilterChips.ItemsSource = cats.Select(c => new { Name = c, State = string.Equals(c, _filter, StringComparison.CurrentCultureIgnoreCase) ? "active" : null }).ToList();
            FilterChips.Visibility = cats.Count > 2 ? Visibility.Visible : Visibility.Collapsed;

            var list = _filter == "Tümü" ? _all
                : _all.Where(a => string.Equals(a.Badge?.Trim(), _filter, StringComparison.CurrentCultureIgnoreCase)).ToList();

            _featured = list.FirstOrDefault();
            if (_featured != null)
            {
                FeaturedCard.Visibility = Visibility.Visible;
                FeaturedCard.Tag = _featured;
                FeaturedTitle.Text = _featured.Title;
                FeaturedDesc.Text = _featured.Description ?? "";
                FeaturedBadge.Text = string.IsNullOrWhiteSpace(_featured.Badge) ? "YENİLİK" : _featured.Badge!.ToUpper(new System.Globalization.CultureInfo("tr-TR"));
                try
                {
                    FeaturedImage.ImageSource = new BitmapImage(new Uri(string.IsNullOrWhiteSpace(_featured.ImageUrl)
                        ? "pack://application:,,,/Assets/Images/lava_dragon.jpg" : _featured.ImageUrl!, UriKind.Absolute));
                }
                catch { FeaturedImage.ImageSource = new BitmapImage(new Uri("pack://application:,,,/Assets/Images/lava_dragon.jpg")); }
            }
            else FeaturedCard.Visibility = Visibility.Collapsed;

            int i = 0;
            AdsList.ItemsSource = list.Skip(1).Select(a =>
            {
                var g = Gradients[i++ % Gradients.Length];
                var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(g.a), (Color)ColorConverter.ConvertFromString(g.b), 30);
                brush.Freeze();
                bool img = !string.IsNullOrWhiteSpace(a.ImageUrl);
                return new
                {
                    Ad = a,
                    Brush = (Brush)brush,
                    Image = img ? a.ImageUrl : null,
                    ImageVisibility = img ? Visibility.Visible : Visibility.Collapsed
                };
            }).ToList();
            TxtEmpty.Text = _all.Count == 0 ? "Henüz haber veya duyuru yok." : "Bu kategoride haber yok.";
            TxtEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Filter_Click(object sender, RoutedEventArgs e)
        {
            _filter = (sender as Button)?.CommandParameter as string ?? "Tümü";
            Render();
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not AdItem ad)
                return;

            if (!string.IsNullOrWhiteSpace(ad.Url))
            {
                Links.Open(ad.Url);
                return;
            }

            FWDialog.Info($"{ad.Title}\n\n{ad.Description}", ad.Badge ?? "Duyuru");
        }
    }
}
