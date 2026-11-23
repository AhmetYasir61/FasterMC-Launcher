using FWLauncherV2.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FWLauncherV2.Views
{
    /// <summary>
    /// Sunucu haritası: Panel → Bağlantılar'daki "map_url" (BlueMap/Dynmap/squaremap web adresi) launcher içinde,
    /// tam alan olarak açılır. Launcher kendi düğmesini eklemez; haritanın kendi kontrolleri (yakınlaştırma, katmanlar,
    /// oyuncular) kullanılır. WebView2CompositionControl saydam pencerede çizilebildiği için kullanılır.
    /// </summary>
    public partial class MapView : UserControl
    {
        // Varsayılan sunucu haritası (Pl3xMap): resmi derlemede Gizli/Gizli.local.cs'den gelir (kaynak kodda adres yok).
        // Panel → Bağlantılar'daki map_url doluysa o önceliklidir.
        private static string DefaultMapUrl() => Gizli.HaritaUrl;

        private string? _url;
        private bool _ready;
        private WebView2CompositionControl? _web;
        private WebView2CompositionControl Web => _web!;

        public MapView()
        {
            InitializeComponent();
            Loaded += async (s, e) => await InitAsync();
            // Web içeriği kart sınırına kırpılsın (düz köşe).
            WebHost.SizeChanged += (s, e) =>
                WebHost.Clip = new RectangleGeometry(new Rect(e.NewSize));
        }

        private async Task InitAsync()
        {
            var links = (Window.GetWindow(this) as MainWindow)?.LinkSettings;
            if (string.IsNullOrWhiteSpace(links?.MapUrl))
                links = await ShopService.GetSettingsAsync();
            _url = links?.MapUrl?.Trim();
            if (string.IsNullOrWhiteSpace(_url)) _url = DefaultMapUrl();
#if DEBUG
            var dbgUrl = Environment.GetEnvironmentVariable("FW_MAP_URL");   // geliştirme testi için
            if (!string.IsNullOrWhiteSpace(dbgUrl)) _url = dbgUrl;
#endif

            if (string.IsNullOrWhiteSpace(_url))
            {
                ShowPlaceholder("Harita ayarlanmamış", "[Sunucu haritası burada gösterilecek]",
                    "Yetkililer Panel → Bağlantılar'dan web haritası adresini (BlueMap, Dynmap, squaremap) eklediğinde canlı harita burada açılır.", false);
                return;
            }

            if (_ready) return;
            try
            {
                SetStatus(null, "Harita yükleniyor...");
                var dataDir = Path.Combine(SettingsService.LauncherDirectory, "cache", "webview2");
                Directory.CreateDirectory(dataDir);
                var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
                _web ??= new WebView2CompositionControl { DefaultBackgroundColor = System.Drawing.Color.FromArgb(11, 9, 20) };
                if (!WebHost.Children.Contains(_web)) WebHost.Children.Add(_web);
                WebHost.Visibility = Visibility.Visible;   // kontrol görünür ağaçta olmadan başlatılamaz
                await Web.EnsureCoreWebView2Async(env);
                Web.CoreWebView2.Settings.AreDevToolsEnabled = false;
                Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                Web.CoreWebView2.NavigationCompleted += (s, e) =>
                {
                    if (e.IsSuccess)
                    {
                        Placeholder.Visibility = Visibility.Collapsed;
                        WebHost.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        ShowPlaceholder("Harita açılamadı", "Harita açılamadı.",
                            "Harita sunucusu kapalı olabilir ya da adres yanlış. Biraz sonra tekrar dene.", true);
                    }
                };
                // Harita kendi içinde kalır: yeni pencere/sekme açma istekleri engellenir (adres tarayıcıda görünmesin);
                // haritanın kendi sunucusu dışındaki bağlantılar sistem tarayıcısında açılır.
                var mapHost = new Uri(_url).Host;
                Web.CoreWebView2.NewWindowRequested += (s, e) =>
                {
                    e.Handled = true;
                    if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && !string.Equals(u.Host, mapHost, StringComparison.OrdinalIgnoreCase))
                        Links.Open(e.Uri);
                };
                Web.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;   // Ctrl+P, F12 vb. kapalı
                // Pl3xMap'in "bağlantıyı kopyala" düğmesi adresi (IP) açığa çıkarır → gizlenir.
                await Web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                    "document.addEventListener('DOMContentLoaded',function(){var s=document.createElement('style');" +
                    "s.textContent='.leaflet-control-link{display:none!important}';document.head.appendChild(s);});");
                Web.Source = new Uri(_url);
                _ready = true;
            }
            catch (WebView2RuntimeNotFoundException)
            {
                ShowPlaceholder("WebView2 yok", "Harita için Microsoft Edge WebView2 gerekli.",
                    "WebView2 Runtime bulunamadı. Microsoft Edge WebView2 Runtime'ı kurup tekrar dene.", true);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Harita açılamadı: {ex.Message}");
                ShowPlaceholder("Harita açılamadı", "Harita açılamadı.", "Biraz sonra tekrar dene.", true);
            }
        }

        private void SetStatus(bool? ok, string text)
        {
            TxtStatus.Text = text;
            LiveDot.Fill = ok switch
            {
                true => (Brush)FindResource("OnlineBrush"),
                false => (Brush)FindResource("DangerBrush"),
                _ => (Brush)FindResource("TextMutedBrush")
            };
        }

        private void ShowPlaceholder(string status, string title, string sub, bool canRetry)
        {
            WebHost.Visibility = Visibility.Collapsed;
            Placeholder.Visibility = Visibility.Visible;
            SetStatus(canRetry ? false : null, status);
            TxtPlaceholder.Text = title;
            TxtPlaceholderSub.Text = sub;
            BtnRetry.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;

        }

        private async void Retry_Click(object sender, RoutedEventArgs e)
        {
            if (_ready && _web?.CoreWebView2 != null)
            {
                SetStatus(null, "Harita yükleniyor...");
                Web.Reload();
            }
            else await InitAsync();
        }

    }
}
