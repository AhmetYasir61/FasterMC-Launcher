using FWLauncherV2.Dialogs;
using FWLauncherV2.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace FWLauncherV2.Views
{
    /// <summary>Modlar · Gölgelendiriciler · Kaynak Paketleri. Ana paket kilitli; oyuncu içerikleri ayrı klasörde.</summary>
    public partial class ModsView : UserControl
    {
        private ContentKind _kind = ContentKind.Mod;
        private List<ContentRow> _rows = new();
        private List<ContentRow> _mainCache = new();
        private string _mainCacheDir = "";

        public ModsView()
        {
            InitializeComponent();
            UserContentService.Changed += () => Dispatcher.BeginInvoke(() => _ = ReloadAsync());
            PackContext.PackChanged += () => Dispatcher.BeginInvoke(() => { _mainCacheDir = ""; _ = ReloadAsync(); });
            Loaded += async (s, e) => { _mainCacheDir = ""; await ReloadAsync(); };
        }

        public void SelectTab(int tab)
        {
            _kind = (ContentKind)Math.Clamp(tab, 0, 2);
            TabMods.Tag = _kind == ContentKind.Mod ? "active" : null;
            TabShaders.Tag = _kind == ContentKind.Shader ? "active" : null;
            TabPacks.Tag = _kind == ContentKind.ResourcePack ? "active" : null;
            (TxtTitle.Text, TxtSubtitle.Text) = _kind switch
            {
                ContentKind.Mod => ("Modlar", "Açıp kapatabileceğin mod listesi."),
                ContentKind.Shader => ("Gölgelendiriciler", "Tek seferde bir gölgelendirici açık olabilir."),
                _ => ("Kaynak Paketleri", "Açtıkların oyunda sırayla uygulanır."),
            };
            if (IsLoaded) _ = ReloadAsync();
        }

        private void Tab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && int.TryParse(b.CommandParameter?.ToString(), out int t)) SelectTab(t);
        }

        private async Task ReloadAsync()
        {
            string dir = PackContext.PackDir;
            if (PackContext.LoaderKey == "horizon" && _kind == ContentKind.Mod)
                TxtSubtitle.Text = "Horizon motorunda .jar modlar çalışmaz; paketin .hmod içerikleri otomatik açılır.";
            UserModsOffNote.Visibility = SettingsService.Load().UserModsEnabled ? Visibility.Collapsed : Visibility.Visible;

            var kind = _kind;
            // İkon: Modrinth/CurseForge adresi, yoksa dosyanın içindeki kendi ikonu; ikisi de yoksa ikon gösterilmez.
            // Dosyalar okunduğu için satırlar arka planda hazırlanır.
            var rows = await Task.Run(() => UserContentService.Reconcile(dir)
                .Where(x => x.Kind == kind).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).Select(x => new ContentRow
                {
                    Title = x.Name,
                    Subtitle = (x.Source switch { "modrinth" => "Modrinth", "curseforge" => "CurseForge", _ => "Yerel dosya" })
                               + (string.IsNullOrWhiteSpace(x.Description) ? (string.IsNullOrWhiteSpace(x.Version) ? "" : $" · {x.Version}") : $" · {x.Description}"),
                    Icon = !string.IsNullOrWhiteSpace(x.IconUrl) ? x.IconUrl
                         : UserContentService.ReadIcon(UserContentService.ActualPath(dir, x), x.Kind),
                    Enabled = x.Enabled,
                    Entry = x
                }).ToList());

            if (kind == ContentKind.Mod)
            {
                if (_mainCacheDir != dir)
                {
                    _mainCache = await Task.Run(() => UserContentService.MainPackRows(dir));
                    _mainCacheDir = dir;
                }
                rows.AddRange(_mainCache);
            }

            _rows = rows;
            ApplyFilter();
            int user = rows.Count(r => !r.Locked), main = rows.Count(r => r.Locked);
            TxtCount.Text = _kind == ContentKind.Mod ? $"{main} ana paket · {user} senin" : $"{rows.Count(r => r.Enabled)} açık / {rows.Count}";
        }

        private void ApplyFilter()
        {
            var q = TxtFilter.Text.Trim();
            FilterHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var list = q.Length == 0 ? _rows
                : _rows.Where(r => r.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || r.Subtitle.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
            RowsList.ItemsSource = list;
            TxtEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtEmpty.Text = q.Length > 0 ? "Aramana uygun sonuç yok."
                : _kind switch
                {
                    ContentKind.Mod => "Paket henüz kurulmadı. Oyna sayfasından KUR'a bas ya da Mağaza'dan mod ekle.",
                    ContentKind.Shader => "Henüz gölgelendirici yok. Mağaza'dan ekleyebilir ya da .zip dosyası seçebilirsin.\n(Gölgelendiriciler için pakette Iris/Oculus olmalı.)",
                    _ => "Henüz kaynak paketi yok. Mağaza'dan ekleyebilir ya da .zip dosyası seçebilirsin."
                };
        }

        private void Filter_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void Toggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox cb || cb.Tag is not ContentRow row || row.Entry == null) return;
            try { UserContentService.SetEnabled(PackContext.PackDir, row.Entry, cb.IsChecked == true); }
            catch (Exception ex) { FWDialog.Error($"Değiştirilemedi: {ex.Message}"); _ = ReloadAsync(); }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not ContentRow row || row.Entry == null) return;
            if (!FWDialog.Confirm($"'{row.Title}' kaldırılsın mı? Dosya silinir.", "Kaldır", "Kaldır", "Vazgeç")) return;
            try { UserContentService.Remove(PackContext.PackDir, row.Entry); }
            catch (Exception ex) { FWDialog.Error($"Kaldırılamadı: {ex.Message}"); }
        }

        private void FromStore_Click(object sender, RoutedEventArgs e)
            => (Window.GetWindow(this) as MainWindow)?.NavigateToStore(_kind);

        private void FromFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Multiselect = true,
                Filter = _kind == ContentKind.Mod ? "Minecraft modu (*.jar)|*.jar" : "Paket (*.zip)|*.zip",
                Title = _kind == ContentKind.Mod ? "Mod dosyası seç" : "Paket dosyası seç"
            };
            if (dlg.ShowDialog() != true) return;
            int ok = 0;
            foreach (var f in dlg.FileNames)
            {
                try
                {
                    if (_kind == ContentKind.Mod && UserContentService.ReadModMeta(f).Ids.Count == 0 &&
                        !FWDialog.Confirm($"'{System.IO.Path.GetFileName(f)}' bir mod gibi görünmüyor (mod kimliği bulunamadı). Yine de eklensin mi?", "Emin misin?", "Ekle", "Atla"))
                        continue;
                    UserContentService.AddFile(PackContext.PackDir, f, _kind);
                    ok++;
                }
                catch (Exception ex) { FWDialog.Error($"{System.IO.Path.GetFileName(f)} eklenemedi: {ex.Message}"); }
            }
            if (ok > 0) (Window.GetWindow(this) as MainWindow)?.ShowToast($"{ok} dosya eklendi.");
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
            => Links.OpenFolder(UserContentService.FolderFor(PackContext.PackDir, _kind));
    }
}
