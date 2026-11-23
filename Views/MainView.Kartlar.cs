using FWLauncherV2.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FWLauncherV2.Views
{
    /// <summary>Paket kartı: döngüsel görsel, ad, sürüm ve duruma göre KUR / GÜNCELLE / OYNA.</summary>
    public sealed class PaketKart : INotifyPropertyChanged
    {
        public ModpackInfo Pack { get; init; } = null!;
        public string Ad => Pack.Name;
        public string Surum => "Sürüm " + (string.IsNullOrWhiteSpace(Pack.McVersion) ? Pack.Version : Pack.McVersion + " · " + Pack.Version);
        public ImageSource? Arkaplan { get; init; }
        public List<ImageSource> Gorseller { get; init; } = new();
        int _i;
        public ImageSource? Gorsel => Gorseller.Count == 0 ? null : Gorseller[_i % Gorseller.Count];

        string _buton = "KUR"; bool _aktif = true;
        public string Buton { get => _buton; set { if (_buton != value) { _buton = value; On(nameof(Buton)); } } }
        public bool Aktif { get => _aktif; set { if (_aktif != value) { _aktif = value; On(nameof(Aktif)); } } }

        public Visibility DuzenleGorunur => FWLauncherV2.Services.UserSession.Role == "owner" ? Visibility.Visible : Visibility.Collapsed;

        string _durum = "", _sunucu = ""; double _ilerleme; Brush? _sunucuRenk;
        public string Durum { get => _durum; set { if (_durum != value) { _durum = value; On(nameof(Durum)); } } }
        public string Sunucu { get => _sunucu; set { if (_sunucu != value) { _sunucu = value; On(nameof(Sunucu)); } } }
        public double Ilerleme { get => _ilerleme; set { if (_ilerleme != value) { _ilerleme = value; On(nameof(Ilerleme)); On(nameof(IlerlemeGorunur)); } } }
        public Visibility IlerlemeGorunur => _ilerleme > 0 && _ilerleme < 100 ? Visibility.Visible : Visibility.Collapsed;
        public Brush? SunucuRenk { get => _sunucuRenk; set { _sunucuRenk = value; On(nameof(SunucuRenk)); } }

        public void Ilerle() { if (Gorseller.Count > 1) { _i++; On(nameof(Gorsel)); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        void On(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public partial class MainView
    {
        readonly ObservableCollection<PaketKart> _kartlar = new();
        DispatcherTimer? _kartZaman;
        int _kartTik;

        static readonly string[] YerelGorseller =
        {
            "pack://application:,,,/Assets/Images/phoenix.jpg",
            "pack://application:,,,/Assets/Images/lava.jpg",
            "pack://application:,,,/Assets/Images/lava_dragon.jpg",
        };

        static ImageSource? Resim(string? yol)
        {
            if (string.IsNullOrWhiteSpace(yol)) return null;
            try
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.UriSource = new Uri(yol, UriKind.Absolute);
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.DecodePixelWidth = 640;
                b.EndInit();
                return b;
            }
            catch { return null; }
        }

        /// <summary>Paket listesi gelince kartları kurar (Horizon gibi Java dışı paketler de kart olur).</summary>
        void KartlariKur()
        {
            _kartlar.Clear();
            foreach (var p in allPacks)
            {
                var gor = (p.Gorseller ?? new List<string>()).Select(Resim).Where(x => x != null).Cast<ImageSource>().ToList();
                if (gor.Count == 0) gor = YerelGorseller.Select(Resim).Where(x => x != null).Cast<ImageSource>().ToList();
                _kartlar.Add(new PaketKart { Pack = p, Gorseller = gor, Arkaplan = Resim(p.Arkaplan) ?? gor.FirstOrDefault() });
            }
            KartList.ItemsSource = _kartlar;
            BtnYeniPaket.Visibility = FWLauncherV2.Services.UserSession.Role == "owner" ? Visibility.Visible : Visibility.Collapsed;
            KartlariTazele();

            if (_kartZaman == null)
            {
                _kartZaman = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _kartZaman.Tick += (_, _) =>
                {
                    _kartTik++;
                    if (_kartTik % 4 == 0) foreach (var k in _kartlar) k.Ilerle();
                    if (_kartTik % 2 == 0) KartlariTazele();
                };
                _kartZaman.Start();
            }
        }

        /// <summary>Her kartın durumunu diskten hesaplar: kurulu değil -> KUR, eski sürüm -> GÜNCELLE, güncel -> OYNA.</summary>
        void KartlariTazele()
        {
            BtnYeniPaket.Visibility = FWLauncherV2.Services.UserSession.Role == "owner" ? Visibility.Visible : Visibility.Collapsed;
            foreach (var k in _kartlar)
            {
                var p = k.Pack;
                if (_operationInProgress)
                {
                    k.Aktif = false;
                    if (p == currentPack) k.Buton = "BEKLE...";
                    continue;
                }
                if (p.IsHorizon) { k.Aktif = true; k.Buton = "OYNA"; continue; }
                if (string.IsNullOrWhiteSpace(p.ZipUrl)) { k.Aktif = false; k.Buton = "HAZIRLANIYOR"; continue; }

                string dir = PackDir(p);
                bool kurulu = File.Exists(Path.Combine(dir, "manifest.json")) && File.Exists(Path.Combine(dir, "launcher_info.json"));
                bool guncel = true;
                if (kurulu)
                {
                    try { guncel = JsonSerializer.Deserialize<LocalPackInfo>(File.ReadAllText(Path.Combine(dir, "launcher_info.json")))?.Version == p.Version; }
                    catch { guncel = false; }
                }
                k.Aktif = true;
                k.Buton = !kurulu ? "KUR" : !guncel ? "GÜNCELLE" : (p == currentPack && _serverOnline == false ? "SUNUCU KAPALI" : "OYNA");
                if (k.Buton == "SUNUCU KAPALI") k.Aktif = false;
            }
            KartDurumAynala();
        }

        /// <summary>Secili paketin durum / ilerleme / sunucu bilgisi onun kartinda; digerleri kendi kurulum durumunu gosterir.</summary>
        void KartDurumAynala()
        {
            foreach (var k in _kartlar)
            {
                if (k.Pack == currentPack)
                {
                    k.Durum = LblStatus.Text;
                    k.Ilerleme = ProgressBar.Value;
                    k.Sunucu = string.IsNullOrWhiteSpace(TxtServerIp.Text) ? TxtServerStatus.Text : TxtServerStatus.Text + " · " + TxtServerIp.Text;
                    k.SunucuRenk = ServerDot.Fill;
                }
                else
                {
                    k.Durum = k.Buton switch { "KUR" => "Kurulu değil.", "GÜNCELLE" => "Yeni sürüm var — güncellemelisin.", "OYNA" => "Hazır.", _ => "" };
                    k.Ilerleme = 0;
                    k.Sunucu = string.IsNullOrWhiteSpace(k.Pack.ServerIp) ? "" : k.Pack.ServerIp + ":" + k.Pack.ServerPort;
                    k.SunucuRenk = (Brush)FindResource("TextMutedBrush");
                }
            }
        }

        /// <summary>Kart butonu: paketi seç, sonra durumuna göre kur / güncelle / oyna.</summary>
        void KartButon_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not PaketKart k || _operationInProgress) return;
            int i = allPacks.IndexOf(k.Pack);
            if (i >= 0 && CmbPack.SelectedIndex != i) CmbPack.SelectedIndex = i;   // CmbPack_SelectionChanged: currentPack + UpdateUI
            else UpdateUI();

            if (k.Buton == "KUR" || k.Buton == "GÜNCELLE") BtnUpdate_Click(sender, e);
            else BtnPlay_Click(sender, e);
            KartlariTazele();
        }

        /// <summary>Owner: yeni paket formu; kaydedilince liste yeniden yüklenir.</summary>
        async void YeniPaket_Click(object sender, RoutedEventArgs e)
        {
            if (FWLauncherV2.Services.UserSession.Role != "owner") return;
            if (new FWLauncherV2.Dialogs.PaketDialog().ShowDialog() == true) await LoadModpackInfo();
        }

        async void PaketDuzenle_Click(object sender, RoutedEventArgs e)
        {
            if (FWLauncherV2.Services.UserSession.Role != "owner" || sender is not FrameworkElement fe || fe.DataContext is not PaketKart k) return;
            if (new FWLauncherV2.Dialogs.PaketDialog(k.Pack).ShowDialog() == true) await LoadModpackInfo();
        }

        // Oklar sayfa sayfa gecer: her sayfa 3 kart (kart 310 + 20 aralik).
        const double KartGenislik = 330, SayfaKart = 3;
        void KartSol_Click(object sender, RoutedEventArgs e) => Sayfa(-1);
        void KartSag_Click(object sender, RoutedEventArgs e) => Sayfa(+1);
        void Sayfa(int yon)
        {
            double sayfa = KartGenislik * SayfaKart;
            double hedef = Math.Round(KartScroll.HorizontalOffset / sayfa + yon) * sayfa;
            KartScroll.ScrollToHorizontalOffset(Math.Max(0, Math.Min(KartScroll.ScrollableWidth, hedef)));
        }

        void KartScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            KartScroll.ScrollToHorizontalOffset(KartScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }
}
