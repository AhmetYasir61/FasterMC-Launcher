using FWLauncherV2.Dialogs;
using FWLauncherV2.Models;
using FWLauncherV2.Services;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FWLauncherV2.Views
{
    public partial class SettingsView : UserControl
    {
        private bool isDirty;
        private bool isLoading;

        private UserSettings currentUserSettings;
        private readonly string currentUsername;
        private readonly int maxRamGB;

        public SettingsView(string currentUsername)
        {
            isLoading = true;   // InitializeComponent sırasında tetiklenen olaylar "değişti" sayılmasın
            InitializeComponent();

            this.currentUsername = string.IsNullOrWhiteSpace(currentUsername) ? "Oyuncu" : currentUsername;
            currentUserSettings = SettingsService.Load();
            maxRamGB = SettingsService.MaxRamGB();

            var v = Assembly.GetExecutingAssembly().GetName().Version;
            TxtAppVersion.Text = $"FasterMC Launcher {v?.Major}.{v?.Minor} beta";
            TxtOsVersion.Text = $"Windows {Environment.OSVersion.Version}";

            LoadIntoUi();
        }

        /// <summary>Sayfaya her girişte diskteki güncel ayarları göster (kaydedilmemiş değişiklik yoksa).</summary>
        public void Reload()
        {
            if (isDirty) return;
            currentUserSettings = SettingsService.Load();
            LoadIntoUi();
        }

        private void LoadIntoUi()
        {
            isLoading = true;
            var s = currentUserSettings;

            ChkPageColors.IsChecked = s.PageColors;
            ChkNewsStrip.IsChecked = s.ShowNewsStrip;

            ChkFullScreen.IsChecked = s.FullScreen;
            TxtWidth.Text = s.GameWidth.ToString();
            TxtHeight.Text = s.GameHeight.ToString();
            ChkMinimize.IsChecked = s.MinimizeOnLaunch;
            ChkRestore.IsChecked = s.RestoreOnExit;

            TxtJavaPath.Text = s.JavaPath ?? "";
            RamSlider.Minimum = SettingsService.MinRamGB;
            RamSlider.Maximum = maxRamGB;
            int ram = Math.Clamp(s.LastRamInGB, SettingsService.MinRamGB, maxRamGB);
            RamSlider.Value = ram;
            TxtRamValue.Text = $"{ram} GB";
            UpdateRAMBar(ram, maxRamGB);
            TxtJvmArgs.Text = s.ExtraJvmArgs ?? "";

            ChkUserMods.IsChecked = s.UserModsEnabled;
            ChkCrash.IsChecked = s.CrashHelper;
            TxtCurseKey.Text = s.CurseForgeApiKey ?? "";

            SetDirty(false);
            isLoading = false;
            UpdateCacheSize();
        }

        // ===================== SEKMELER =====================
        private void Tab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || !int.TryParse(b.CommandParameter?.ToString(), out int t)) return;
            var tabs = new[] { TabLook, TabWindow, TabJava, TabMods, TabFiles };
            var panels = new UIElement[] { PanelLook, PanelWindow, PanelJava, PanelMods, PanelFiles };
            for (int i = 0; i < tabs.Length; i++)
            {
                tabs[i].Tag = i == t ? "active" : null;
                panels[i].Visibility = i == t ? Visibility.Visible : Visibility.Collapsed;
            }
            if (t == 4) UpdateCacheSize();
        }

        private void SetDirty(bool d)
        {
            isDirty = d;
            if (TxtDirty != null) TxtDirty.Visibility = d ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Changed_Click(object sender, RoutedEventArgs e) { if (!isLoading) SetDirty(true); }
        private void Changed_Text(object sender, TextChangedEventArgs e) { if (!isLoading) SetDirty(true); }

        // ===================== JAVA / RAM =====================
        private void JavaLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
            => Links.Open(e.Uri.AbsoluteUri);

        private void BtnSelectJava_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "Java executable (java.exe)|java.exe" };
            if (dialog.ShowDialog() == true)
            {
                TxtJavaPath.Text = dialog.FileName;
                SetDirty(true);
            }
        }

        private void AutoJava_Click(object sender, RoutedEventArgs e)
        {
            var found = MainView.AutoFindJava(PackContext.McVersion);
            if (string.IsNullOrWhiteSpace(found))
            {
                FWDialog.Warn("Sistemde uygun bir Java bulunamadı. Java 21'i indirip kurduktan sonra tekrar dene.", "Java bulunamadı");
                return;
            }
            TxtJavaPath.Text = found;
            SetDirty(true);
        }

        private void RamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtRamValue != null)
                TxtRamValue.Text = $"{(int)RamSlider.Value} GB";

            UpdateRAMBar((int)RamSlider.Value, maxRamGB);

            if (!isLoading)
                SetDirty(true);
        }

        private void UpdateRAMBar(int selectedGB, int maxAllowedGB)
        {
            if (RamUsageBar == null || maxAllowedGB <= 0) return;

            double percentage = (double)selectedGB / maxAllowedGB * 100;
            RamUsageBar.Value = Math.Min(percentage, 100);

            // Renk: yarıya kadar tema rengi, yarıdan fazlası sarı, toplam-1 GB ve üstü kırmızı.
            string toplam = $"Toplam: {SettingsService.TotalRamMb() / 1024.0:0.#} GB";
            int onerilen = SettingsService.RecommendedMaxRamGB();
            switch (SettingsService.RamRiskLevel(selectedGB))
            {
                case 2:
                    SetRamColor(RamRed);
                    TxtRamHint.Text = $"{toplam} · DİKKAT: bilgisayarın neredeyse tüm RAM'ini oyuna veriyorsun. Windows için yer kalmaz; oyun ve bilgisayar donabilir, çökebilir. Önerilen: en fazla {onerilen} GB.";
                    break;
                case 1:
                    SetRamColor(RamYellow);
                    TxtRamHint.Text = $"{toplam} · Yüksek değer: oynarken tarayıcı/Discord gibi programları kapatman önerilir. Önerilen: en fazla {onerilen} GB.";
                    break;
                default:
                    SetRamColor(null);
                    TxtRamHint.Text = $"{toplam} · önerilen üst sınır {onerilen} GB.";
                    break;
            }
        }

        private static readonly Brush RamRed = Frozen(Color.FromRgb(0xE5, 0x39, 0x35));
        private static readonly Brush RamYellow = Frozen(Color.FromRgb(0xF2, 0xB1, 0x34));
        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        /// <summary>null = tema rengine dön.</summary>
        private void SetRamColor(Brush? brush)
        {
            if (brush == null)
            {
                TxtRamValue.SetResourceReference(TextBlock.ForegroundProperty, "PageAccentBrush");
                TxtRamHint.ClearValue(TextBlock.ForegroundProperty);
                RamUsageBar.ClearValue(Control.ForegroundProperty);
            }
            else
            {
                TxtRamValue.Foreground = brush;
                TxtRamHint.Foreground = brush;
                RamUsageBar.Foreground = brush;
            }
        }

        // ===================== DOSYALAR =====================
        private void Folder_Click(object sender, RoutedEventArgs e)
        {
            string game = PackContext.PackDir;
            string path = ((sender as Button)?.Tag as string) switch
            {
                "root" => SettingsService.LauncherDirectory,
                "game" => game,
                "mods" => Path.Combine(game, "mods"),
                "usermods" => UserContentService.UserModsDir(game),
                "shaderpacks" => Path.Combine(game, "shaderpacks"),
                "resourcepacks" => Path.Combine(game, "resourcepacks"),
                "screenshots" => Path.Combine(game, "screenshots"),
                "logs" => Path.Combine(game, "logs"),
                "launcherlogs" => Path.Combine(SettingsService.LauncherDirectory, "logs"),
                "crash" => Path.Combine(game, "crash-reports"),
                _ => SettingsService.LauncherDirectory
            };
            Links.OpenFolder(path);
        }

        private static string CacheDir => Path.Combine(SettingsService.LauncherDirectory, "cache");

        private void UpdateCacheSize()
        {
            try
            {
                long bytes = Directory.Exists(CacheDir)
                    ? new DirectoryInfo(CacheDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
                TxtCacheSize.Text = $"Mağaza indirmeleri ve harita önbelleği · {bytes / 1048576.0:0.0} MB";
            }
            catch { }
        }

        private void ClearCache_Click(object sender, RoutedEventArgs e)
        {
            if (!FWDialog.Confirm("Önbellek temizlensin mi? (Oyun dosyaları ve modların etkilenmez.)", "Önbellek", "Temizle", "Vazgeç")) return;
            int failed = 0;
            if (Directory.Exists(CacheDir))
                foreach (var f in Directory.EnumerateFiles(CacheDir, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(f); } catch { failed++; }
                }
            UpdateCacheSize();
            (Window.GetWindow(this) as MainWindow)?.ShowToast(failed > 0 ? $"Önbellek temizlendi ({failed} dosya kullanımda olduğu için kaldı)." : "Önbellek temizlendi.");
        }

        // ===================== KAYDET =====================
        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (!TrySave())
                return;
            (Window.GetWindow(this) as MainWindow)?.ShowToast("Ayarlar kaydedildi ✓");
        }

        private void Revert_Click(object sender, RoutedEventArgs e)
        {
            currentUserSettings = SettingsService.Load();
            LoadIntoUi();
        }

        private bool TrySave()
        {
            try
            {
                // Diskteki en güncel ayarların üzerine yaz (oturum, seçili paket, skin gibi alanlar korunur).
                var s = SettingsService.Load();
                s.PageColors = ChkPageColors.IsChecked == true;
                s.ShowNewsStrip = ChkNewsStrip.IsChecked == true;
                s.FullScreen = ChkFullScreen.IsChecked == true;
                s.GameWidth = int.TryParse(TxtWidth.Text.Trim(), out int w) ? Math.Clamp(w, 640, 7680) : 854;
                s.GameHeight = int.TryParse(TxtHeight.Text.Trim(), out int h) ? Math.Clamp(h, 360, 4320) : 480;
                s.MinimizeOnLaunch = ChkMinimize.IsChecked == true;
                s.RestoreOnExit = ChkRestore.IsChecked == true;
                s.LastRamInGB = (int)RamSlider.Value;
                s.JavaPath = TxtJavaPath.Text?.Trim();
                s.ExtraJvmArgs = TxtJvmArgs.Text?.Trim();
                s.UserModsEnabled = ChkUserMods.IsChecked == true;
                s.CrashHelper = ChkCrash.IsChecked == true;
                s.CurseForgeApiKey = string.IsNullOrWhiteSpace(TxtCurseKey.Text) ? null : TxtCurseKey.Text.Trim();
                s.LastUsername = currentUsername;

                SettingsService.Save(s);
                currentUserSettings = s;
                LoadIntoUi();

                MainWindow.ApplyPageTheme("settings");
                (Window.GetWindow(this) as MainWindow)?.PlayCard.ApplyNewsStripVisibility();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("Ayarlar kaydedilemedi.", ex);
                FWDialog.Error($"Ayarlar kaydedilirken bir hata oluştu:\n{ex.Message}");
                return false;
            }
        }

        // ------------------------------
        // Public metotlar
        // ------------------------------
        public UserSettings GetCurrentSettings() => currentUserSettings;

        public int GetTotalRAM() => SettingsService.TotalRamMb();

        public bool CanLeave()
        {
            if (!isDirty)
                return true;

            var result = FWDialog.Ask3(
                "Ayarlar kaydedilmedi. Çıkmadan önce kaydetmek ister misiniz?",
                "Kaydedilmemiş Değişiklik", "Kaydet", "Kaydetme", "İptal");

            switch (result)
            {
                case true: return TrySave();
                case false:
                    currentUserSettings = SettingsService.Load();
                    LoadIntoUi();
                    return true;
                default: return false;
            }
        }
    }
}
