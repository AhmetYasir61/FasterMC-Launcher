using FWLauncherV2.Models;
using FWLauncherV2.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FWLauncherV2.Dialogs
{
    /// <summary>
    /// Owner: yeni oyun paketi ekle / düzenle. Kaydet -> sitede /fmc/modpacks.json (herkese).
    /// Görseller bilgisayardan seçilir ve sunucuya yüklenir; kaldırılan görsel sunucudan da silinir.
    /// </summary>
    public partial class PaketDialog : Window
    {
        public const string Adres = UserSession.ApiBase + "fmc_paket_admin.php";
        public const string GorselAdres = UserSession.ApiBase + "fmc_gorsel.php";

        readonly List<string> _gorseller = new();
        string _arkaplan = "";
        readonly HashSet<string> _buOturumYuklenen = new();   // vazgecilirse silinir
        readonly HashSet<string> _silinecek = new();          // kaydedilince silinir (onceden yayinda olanlar)
        bool _kaydedildi;

        public PaketDialog(ModpackInfo? p = null)
        {
            InitializeComponent();
            var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
            if (owner != null && !ReferenceEquals(owner, this)) Owner = owner;
            else WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Closed += async (_, _) => { if (!_kaydedildi) foreach (var u in _buOturumYuklenen) await GorselSil(u); };

            if (p != null)
            {
                TxtBaslik.Text = "Paketi Düzenle";
                TxtId.Text = p.Id; TxtId.IsEnabled = false;
                TxtAd.Text = p.Name; TxtSurum.Text = p.Version; TxtMc.Text = p.McVersion ?? "";
                TxtZip.Text = p.ZipUrl; TxtIp.Text = p.ServerIp ?? ""; TxtPort.Text = p.ServerPort.ToString();
                TxtAciklama.Text = p.Description ?? "";
                _arkaplan = p.Arkaplan ?? "";
                _gorseller.AddRange(p.Gorseller ?? new());
                foreach (ComboBoxItem i in CmbYukleyici.Items)
                    if (string.Equals(i.Content?.ToString(), p.Loader, StringComparison.OrdinalIgnoreCase)) CmbYukleyici.SelectedItem = i;
                BtnSil.Visibility = Visibility.Visible;
            }
            Ciz();
        }

        // ---------------------------------------------------------------- gorseller
        static ImageSource? Onizleme(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            try
            {
                var b = new BitmapImage();
                b.BeginInit(); b.UriSource = new Uri(url, UriKind.Absolute); b.DecodePixelWidth = 320; b.CacheOption = BitmapCacheOption.OnLoad; b.EndInit();
                return b;
            }
            catch { return null; }
        }

        void Ciz()
        {
            ImgArkaplan.ImageSource = Onizleme(_arkaplan);
            PnlGorseller.Children.Clear();
            foreach (var url in _gorseller)
            {
                var g = new Grid { Width = 128, Height = 76, Margin = new Thickness(0, 0, 10, 10) };
                g.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xB3, 0x47)),
                    Background = new ImageBrush(Onizleme(url)) { Stretch = Stretch.UniformToFill },
                });
                var x = new Button
                {
                    Content = "✕", Width = 24, Height = 24, FontSize = 11, Margin = new Thickness(0, 4, 4, 0),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Style = (Style)FindResource("IconSquareButton"), ToolTip = "Görseli kaldır", Tag = url,
                };
                x.Click += async (s, _) => await Kaldir((string)((Button)s).Tag);
                g.Children.Add(x);
                PnlGorseller.Children.Add(g);
            }
        }

        async Task Kaldir(string url)
        {
            _gorseller.Remove(url);
            await Birak(url);
            Ciz();
        }

        /// <summary>Bu oturumda yuklendiyse hemen silinir; yayindaki eski gorsel kaydedilince silinir.</summary>
        async Task Birak(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            if (_buOturumYuklenen.Remove(url)) await GorselSil(url);
            else _silinecek.Add(url);
        }

        string[]? DosyaSec(bool coklu)
        {
            var d = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Görsel seç", Multiselect = coklu,
                Filter = "Görseller (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp",
            };
            return d.ShowDialog(this) == true ? d.FileNames : null;
        }

        async void ArkaplanSec_Click(object sender, RoutedEventArgs e)
        {
            var f = DosyaSec(false);
            if (f == null) return;
            var url = await Yukle(f[0]);
            if (url == null) return;
            await Birak(_arkaplan);
            _arkaplan = url;
            Ciz();
        }

        async void ArkaplanKaldir_Click(object sender, RoutedEventArgs e)
        {
            await Birak(_arkaplan);
            _arkaplan = "";
            Ciz();
        }

        async void GorselEkle_Click(object sender, RoutedEventArgs e)
        {
            var f = DosyaSec(true);
            if (f == null) return;
            foreach (var yol in f.Take(12 - _gorseller.Count))
            {
                var url = await Yukle(yol);
                if (url != null) { _gorseller.Add(url); Ciz(); }
            }
        }

        /// <summary>Gorseli kucultup (en fazla 1600 px) JPEG olarak sunucuya yukler; adresini dondurur.</summary>
        async Task<string?> Yukle(string yol)
        {
            ErrorBox.Visibility = Visibility.Collapsed;
            if (string.IsNullOrWhiteSpace(TxtId.Text)) { Hata("Görsel yüklemeden önce paket kimliğini (Id) yaz."); return null; }
            TxtYukleniyor.Visibility = Visibility.Visible;
            try
            {
                byte[] veri;
                var kaynak = new BitmapImage();
                kaynak.BeginInit(); kaynak.UriSource = new Uri(yol); kaynak.CacheOption = BitmapCacheOption.OnLoad; kaynak.EndInit();
                BitmapSource bs = kaynak;
                if (bs.PixelWidth > 1600) bs = new TransformedBitmap(bs, new ScaleTransform(1600.0 / bs.PixelWidth, 1600.0 / bs.PixelWidth));
                var enc = new JpegBitmapEncoder { QualityLevel = 85 };
                enc.Frames.Add(BitmapFrame.Create(bs));
                using (var ms = new MemoryStream()) { enc.Save(ms); veri = ms.ToArray(); }

                var r = await Post(GorselAdres, new Dictionary<string, object> { ["action"] = "yukle", ["id"] = TxtId.Text.Trim(), ["veri"] = Convert.ToBase64String(veri) });
                if (r == null) return null;
                var url = r.Value.GetProperty("url").GetString();
                if (url != null) _buOturumYuklenen.Add(url);
                return url;
            }
            catch (Exception ex) { Hata("Görsel yüklenemedi: " + ex.Message); return null; }
            finally { TxtYukleniyor.Visibility = Visibility.Collapsed; }
        }

        async Task GorselSil(string url)
        {
            try { await Post(GorselAdres, new Dictionary<string, object> { ["action"] = "sil", ["url"] = url }, sessiz: true); } catch { }
        }

        // ---------------------------------------------------------------- kaydet / sil
        void Hata(string m) { TxtHata.Text = m; ErrorBox.Visibility = Visibility.Visible; }

        async void Kaydet_Click(object sender, RoutedEventArgs e)
        {
            ErrorBox.Visibility = Visibility.Collapsed;
            if (string.IsNullOrWhiteSpace(TxtId.Text) || string.IsNullOrWhiteSpace(TxtAd.Text) || string.IsNullOrWhiteSpace(TxtSurum.Text))
            { Hata("Kimlik, ad ve sürüm zorunlu."); return; }
            if (!int.TryParse(TxtPort.Text.Trim(), out int port) || port < 1 || port > 65535) { Hata("Port 1-65535 arası olmalı."); return; }

            var paket = new Dictionary<string, object>
            {
                ["Id"] = TxtId.Text.Trim(), ["Name"] = TxtAd.Text.Trim(), ["Version"] = TxtSurum.Text.Trim(), ["ZipUrl"] = TxtZip.Text.Trim(),
                ["McVersion"] = TxtMc.Text.Trim(), ["Loader"] = (CmbYukleyici.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Forge",
                ["ServerIp"] = TxtIp.Text.Trim(), ["ServerPort"] = port, ["Description"] = TxtAciklama.Text.Trim(),
                ["Arkaplan"] = _arkaplan, ["Gorseller"] = _gorseller.ToList(),
            };
            BtnKaydet.IsEnabled = false;
            var r = await Post(Adres, new Dictionary<string, object> { ["action"] = "kaydet", ["paket"] = paket });
            BtnKaydet.IsEnabled = true;
            if (r == null) return;
            _kaydedildi = true;
            foreach (var u in _silinecek) await GorselSil(u);   // yayindan kalkan eski gorseller sunucudan da silinir
            FWDialog.Info(r.Value.TryGetProperty("message", out var m) ? m.GetString() ?? "Kaydedildi." : "Kaydedildi.", "Kaydedildi");
            DialogResult = true;
        }

        async void Sil_Click(object sender, RoutedEventArgs e)
        {
            if (!FWDialog.Confirm($"'{TxtAd.Text}' paketi ve görselleri herkes için kaldırılsın mı?", "Paketi Sil")) return;
            var r = await Post(Adres, new Dictionary<string, object> { ["action"] = "sil", ["id"] = TxtId.Text.Trim() });
            if (r == null) return;
            _kaydedildi = true;   // sunucu paketin gorsel klasorunu de siler
            FWDialog.Info("Paket kaldırıldı.", "Silindi");
            DialogResult = true;
        }

        /// <summary>Kimlik bilgisiyle POST; basarisizsa hatayi gosterir ve null doner.</summary>
        async Task<JsonElement?> Post(string adres, Dictionary<string, object> govde, bool sessiz = false)
        {
            var (u, t) = UserSession.Credentials();
            govde["username"] = u; govde["token"] = t;
            try
            {
                var resp = await HttpClientProvider.Api.PostAsJsonAsync(adres, govde);
                var r = await resp.Content.ReadFromJsonAsync<JsonElement>();
                bool ok = r.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
                if (ok) return r;
                if (!sessiz) Hata(r.TryGetProperty("message", out var m) ? m.GetString() ?? "İşlem başarısız." : "İşlem başarısız.");
            }
            catch (Exception ex) { if (!sessiz) Hata("Sunucuya ulaşılamadı: " + ex.Message); }
            return null;
        }

        void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
        void Header_MouseDown(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }
    }
}
