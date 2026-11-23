using FWLauncherV2.Dialogs;
using FWLauncherV2.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FWLauncherV2.Views
{
    /// <summary>Mod mağazası: Modrinth / CurseForge araması, profil sürümüne uygun dosya + bağımlılık kurulumu.</summary>
    public partial class StoreView : UserControl
    {
        private StoreSource _source = StoreSource.Modrinth;
        private ContentKind _kind = ContentKind.Mod;
        private int _searchToken;
        private bool _dirty = true;

        public StoreView()
        {
            InitializeComponent();
            // Paket (MC sürümü / yükleyici) değişince uyumluluk filtresi yeniden uygulanır.
            PackContext.PackChanged += () => Dispatcher.BeginInvoke(() =>
            {
                _dirty = true;
                if (IsLoaded && IsVisible) _ = SearchAsync();
            });
            Loaded += async (s, e) => { if (_dirty) await SearchAsync(); };
            TxtSearch.TextChanged += (s, e) => SearchHint.Visibility = TxtSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public void SetKind(ContentKind kind)
        {
            if (kind != _kind) { _kind = kind; _dirty = true; }
            KindMod.Tag = _kind == ContentKind.Mod ? "active" : null;
            KindShader.Tag = _kind == ContentKind.Shader ? "active" : null;
            KindPack.Tag = _kind == ContentKind.ResourcePack ? "active" : null;
            if (IsLoaded && _dirty) _ = SearchAsync();
        }

        private void Kind_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && int.TryParse(b.CommandParameter?.ToString(), out int k)) SetKind((ContentKind)k);
        }

        private void Source_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || !int.TryParse(b.CommandParameter?.ToString(), out int s)) return;
            _source = (StoreSource)s;
            SrcModrinth.Tag = _source == StoreSource.Modrinth ? "active" : null;
            SrcCurse.Tag = _source == StoreSource.CurseForge ? "active" : null;
            SearchHint.Text = _source == StoreSource.Modrinth ? "Modrinth içinde ara..." : "CurseForge içinde ara...";
            _ = SearchAsync();
        }

        private void Search_Click(object sender, RoutedEventArgs e) => _ = SearchAsync();
        private void Search_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; _ = SearchAsync(); }
        }

        private async Task SearchAsync()
        {
            _dirty = false;
            int token = ++_searchToken;
            if (PackContext.LoaderKey == "horizon" && _kind != ContentKind.ResourcePack)
            {
                // Modrinth/CurseForge modları ve Iris gölgelendiricileri Java içindir; Horizon'da çalışmaz.
                ResultList.ItemsSource = null;
                TxtCompat.Text = "";
                TxtState.Text = "Horizon motorunda Java modları ve Iris gölgelendiricileri çalışmaz. Kaynak paketleri kullanılabilir.";
                TxtState.Visibility = Visibility.Visible;
                return;
            }
            TxtCompat.Text = string.IsNullOrWhiteSpace(PackContext.McVersion)
                ? "Paket bilgisi bekleniyor — tüm sürümler gösteriliyor"
                : $"{PackContext.McVersion}{(_kind == ContentKind.Mod && PackContext.LoaderDisplay.Length > 0 ? " · " + PackContext.LoaderDisplay : "")} ile uyumlu sonuçlar";
            TxtState.Text = "Yükleniyor...";
            TxtState.Visibility = Visibility.Visible;
            ResultList.ItemsSource = null;
            try
            {
                var items = await StoreService.SearchAsync(_source, _kind, TxtSearch.Text.Trim());
                if (token != _searchToken) return;   // daha yeni bir arama başladı
                var installed = UserContentService.LoadIndex(PackContext.PackDir);
                foreach (var it in items)
                    it.Installed = installed.Any(x => x.Source == it.SourceKey && x.ProjectId == it.ProjectId);
                ResultList.ItemsSource = items;
                Scroll.ScrollToTop();
                TxtState.Text = "Sonuç bulunamadı.";
                TxtState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                if (token != _searchToken) return;
                Logger.Warn($"Mağaza araması başarısız: {ex.Message}");
                TxtState.Text = "Mağazaya ulaşılamadı.\n" + ex.Message;
                TxtState.Visibility = Visibility.Visible;
            }
        }

        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not StoreItem item) return;
            if (item.Installed)
            {
                if (FWDialog.Confirm($"'{item.Title}' zaten kurulu. Modlar sayfasında görmek ister misin?", "Kurulu", "Git", "Kapat"))
                    (Window.GetWindow(this) as MainWindow)?.NavigateToMods((int)item.Kind);
                return;
            }
            if (MainView.RunningGame != null)
                (Window.GetWindow(this) as MainWindow)?.ShowToast("Oyun açık — yeni içerik bir sonraki açılışta yüklenir.");

            InstallOverlay.Visibility = Visibility.Visible;
            TxtInstallTitle.Text = $"{item.Title} kuruluyor";
            TxtInstallStep.Text = "";
            try
            {
                var progress = new Progress<string>(s => TxtInstallStep.Text = s);
                List<string> done = await StoreService.InstallAsync(item, progress);
                item.Installed = true;
                ResultList.Items.Refresh();
                string extra = done.Count > 1 ? $"\nBağımlılıklar: {string.Join(", ", done.Skip(1))}" : "";
                (Window.GetWindow(this) as MainWindow)?.ShowToast($"{item.Title} kuruldu ✓{extra}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Mağaza kurulumu başarısız ({item.Title}): {ex.Message}");
                FWDialog.Error(ex.Message, "Kurulamadı");
            }
            finally
            {
                InstallOverlay.Visibility = Visibility.Collapsed;
            }
        }
    }
}
