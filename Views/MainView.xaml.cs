using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installers;
using CmlLib.Core.ProcessBuilder;
using CurseForge.APIClient;
using CurseForge.APIClient.Models.Files;
using FWLauncherV2.Dialogs;
using FWLauncherV2.Models;
using FWLauncherV2.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using File = System.IO.File;

namespace FWLauncherV2.Views
{
    public partial class MainView : UserControl
    {
        // Modpack doğrudan bu klasöre kurulur (ayrı "modpacks/{id}" alt klasörü açılmaz).
        private readonly string launcherPath = SettingsService.LauncherDirectory;
        private const string ModpackUrl = "https://firewing.store/fmc/modpacks.json";
        private const string CurseForgeApiKey = Gizli.CurseForgeApiKey;   // Gizli/Gizli.local.cs (git disi)

        private readonly HttpClient httpClient = HttpClientProvider.Download;
        private readonly ApiClient cfApiClient;

        private ModpackInfo? currentPack;
        private List<ModpackInfo> allPacks = new();
        private bool _packSelectorReady;   // SelectionChanged'in ilk doldurmada tetiklenmesini önler
        private UserSettings currentUserSettings = new();
        private string currentUsername = "Oyuncu";
        private bool updateAvailable;

        private bool? _serverOnline;          // null = kontrol ediliyor, true/false = sonuç
        private bool _operationInProgress;    // kurulum/oyun başlatma sürerken true
        private readonly DispatcherTimer _serverTimer;

        public MainView()
        {
            InitializeComponent();
            cfApiClient = new ApiClient(CurseForgeApiKey);

            // Sunucu durumunu periyodik yenile (açılırsa OYNA kilidi otomatik açılır).
            _serverTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
            _serverTimer.Tick += (s, e) => { if (currentPack != null) _ = CheckServerStatusAsync(); };

            Loaded += MainView_Loaded;
            Unloaded += (s, e) => _serverTimer.Stop();
            UserContentService.Changed += () => Dispatcher.BeginInvoke(UpdateModsBadge);
        }

        private bool _initialized;
        private const string DefaultDescription =
            "Keşfet, fethet, yüksel. Tek tıkla sunucuya bağlan; modların, gölgelendiricilerin ve kaynak paketlerin hazır.";

        public void SetCurrentUser(string username)
        {
            currentUsername = string.IsNullOrWhiteSpace(username) ? "Oyuncu" : username;
            TxtWelcome.Text = $"Hoş geldin, {currentUsername}!";
        }

        private async void MainView_Loaded(object sender, RoutedEventArgs e)
        {
            // Kart bir kez yüklenir; sayfa geçişlerinde (Loaded tekrar tetiklenirse) yeniden indirilmez.
            if (_initialized) { _serverTimer.Start(); return; }
            _initialized = true;
            LoadUserSettings();
            ApplyNewsStripVisibility();
            _ = LoadNewsAsync();
            await LoadModpackInfo();
            _serverTimer.Start();
        }

        // ===================== HABER ŞERİDİ =====================
        private static readonly string[] CardGradients =
            { "#FFED8519,#FF955008", "#FFB89914,#FF393734", "#FFEC8C28,#FF6B3E0E", "#FFF59E0B,#FF6B3F05", "#FFF68A19,#FF8A4C0B" };

        public void ApplyNewsStripVisibility()
            => NewsStrip.Visibility = Visibility.Collapsed;   // FasterMC: ana ekranda paket kartlari tam yukseklik

        private async Task LoadNewsAsync()
        {
            try
            {
                var ads = await ShopService.GetAdsAsync();
                int i = 0;
                NewsList.ItemsSource = ads.Select(a =>
                {
                    var parts = CardGradients[i++ % CardGradients.Length].Split(',');
                    var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(parts[0]),
                                                        (Color)ColorConverter.ConvertFromString(parts[1]), 35);
                    brush.Freeze();
                    bool hasImg = !string.IsNullOrWhiteSpace(a.ImageUrl);
                    return new
                    {
                        Ad = a,
                        Brush = (Brush)brush,
                        Image = hasImg ? a.ImageUrl! : (i == 1 ? "pack://application:,,,/Assets/Images/lava_dragon.jpg" : null),
                        ImageVisibility = hasImg || i == 1 ? Visibility.Visible : Visibility.Collapsed
                    };
                }).ToList();
                TxtNoNews.Visibility = ads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex) { Logger.Warn($"Haber şeridi yüklenemedi: {ex.Message}"); }
        }

        private void News_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not AdItem ad) return;
            if (!string.IsNullOrWhiteSpace(ad.Url)) { Links.Open(ad.Url); return; }
            (Window.GetWindow(this) as MainWindow)?.NavigateToReklamlar();
        }

        private void NewsNext_Click(object sender, RoutedEventArgs e)
        {
            double target = NewsScroll.HorizontalOffset + 322;
            if (target >= NewsScroll.ScrollableWidth + 1) target = 0;
            NewsScroll.ScrollToHorizontalOffset(target);
        }

        private void NewsScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            NewsScroll.ScrollToHorizontalOffset(NewsScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        }

        private void LoadUserSettings()
        {
            currentUserSettings = SettingsService.Load();

            if (string.IsNullOrWhiteSpace(currentUserSettings.LastUsername))
                currentUserSettings.LastUsername = currentUsername;

            // Java yolu boşsa, launcher'ın indirdiği yerel Java'lardan birini bulmayı dene.
            if (string.IsNullOrWhiteSpace(currentUserSettings.JavaPath) || !File.Exists(currentUserSettings.JavaPath))
            {
                var javaFolder = Path.Combine(launcherPath, "javalar");
                if (Directory.Exists(javaFolder))
                {
                    var foundJava = Directory.GetFiles(javaFolder, "java.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (!string.IsNullOrEmpty(foundJava))
                        currentUserSettings.JavaPath = foundJava;
                }
            }
        }

        /// <summary>Guncelleme baloncugu: launcher'i kapatmadan paket bilgisini yeniden ceker.</summary>
        public Task YenidenYukleAsync() => LoadModpackInfo();

        private async Task LoadModpackInfo()
        {
            UpdateStatus("Paket bilgileri alınıyor...", 0);
            try
            {
                var jsonString = await httpClient.GetStringAsync(ModpackUrl);
                var onlinePacks = JsonSerializer.Deserialize<List<ModpackInfo>>(jsonString);
                allPacks = onlinePacks?.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList() ?? new();

                // Sitede Horizon paketi tanımlı değilse deneysel paket sunulur (içerik zip'i ve sunucu olmadan oynanır).
                if (false && !allPacks.Any(p => p.IsHorizon))   // FasterMC: yalnizca FasterMC paketi
                    allPacks.Add(new ModpackInfo
                    {
                        Id = "horizon",
                        Name = "FireWing Horizon (Deneysel)",
                        Version = "1",
                        Engine = "horizon",
                        Description = "Rust ile yazılmış Horizon motoru, Minecraft 26.3. Java mod ve eklentiler bu motorda çalışmaz.",
                    });

                if (allPacks.Count == 0)
                    throw new Exception("Mod paketi bilgisi bulunamadı.");

                // Son seçilen paketi hatırla (yoksa ilk paket).
                currentPack = allPacks.FirstOrDefault(p => p.Id == currentUserSettings.SelectedPackId) ?? allPacks[0];

                // Sürüm seçici: 2+ paket varsa göster (paketler web editöründeki 'Sürümler' sekmesinden yönetilir).
                _packSelectorReady = false;
                CmbPack.Items.Clear();
                foreach (var p in allPacks)
                {
                    // Aynı ada sahip paketleri Id ile ayırt et ("PokeWing Network (RolePlay)").
                    bool dup = allPacks.Count(x => x.Name == p.Name) > 1;
                    CmbPack.Items.Add(dup && !string.IsNullOrWhiteSpace(p.Id) ? $"{p.Name} ({p.Id})" : p.Name);
                }
                CmbPack.SelectedIndex = allPacks.IndexOf(currentPack);
                KartlariKur();
                CmbPack.Visibility = allPacks.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                _packSelectorReady = true;

                TxtPackName.Text = currentPack.Name;
                TxtDescription.Text = string.IsNullOrWhiteSpace(currentPack.Description) ? DefaultDescription : currentPack.Description;
                UpdateBadges();
                UpdateUI();
                UpdateStatus("Hazır.", 100);
                _ = CheckServerStatusAsync();
            }
            catch (Exception ex)
            {
                Logger.Error("Mod paketi bilgisi alınamadı.", ex);
                UpdateStatus("Hata: Paket bilgisi alınamadı.", 0);
                FWDialog.Warn(
                    $"Mod paketi bilgisi alınamadı. İnternet bağlantınızı kontrol edin.\n\nAyrıntı: {ex.Message}",
                    "Bağlantı Hatası");
            }
        }

        /// <summary>
        /// Paketin kurulum klasörü. Tüm paketler (ilki dahil) packs\{Id} altına kurulur;
        /// her paketin dünyası/modları/ayarları ayrıdır.
        /// </summary>
        private string PackDir(ModpackInfo p)
        {
            var safe = new string((string.IsNullOrWhiteSpace(p.Id) ? p.Name : p.Id)
                .Where(char.IsLetterOrDigit).ToArray());
            if (safe.Length == 0) safe = "pack";
            return Path.Combine(launcherPath, "packs", safe);
        }

        private string CurrentPackDir => currentPack == null ? launcherPath : PackDir(currentPack);

        /// <summary>Sürüm seçici değişince: paketi kaydet, rozet/durum/kurulum bilgisini tazele.</summary>
        private void CmbPack_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!_packSelectorReady || CmbPack.SelectedIndex < 0 || CmbPack.SelectedIndex >= allPacks.Count)
                return;
            currentPack = allPacks[CmbPack.SelectedIndex];
            currentUserSettings.SelectedPackId = currentPack.Id;
            try { SettingsService.Save(currentUserSettings); } catch (Exception ex) { Logger.Warn($"Paket seçimi kaydedilemedi: {ex.Message}"); }
            TxtPackName.Text = currentPack.Name;
            TxtDescription.Text = string.IsNullOrWhiteSpace(currentPack.Description) ? DefaultDescription : currentPack.Description;
            _serverOnline = null;   // yeni paketin sunucusu kontrol edilene dek OYNA bekletilir
            UpdateBadges();
            UpdateUI();
            _ = CheckServerStatusAsync();
            Logger.Info($"Paket seçildi: {currentPack.Name} · klasör={CurrentPackDir}");
        }

        private void UpdateUI()
        {
            if (currentPack == null)
                return;

            updateAvailable = false;

            if (currentPack.IsHorizon)
            {
                bool ready = HorizonPackReady(out updateAvailable);
                BtnUpdate.IsEnabled = true;
                BtnUpdate.Content = updateAvailable ? "Güncelle" : (ready ? "Onar" : "Kur");
                BtnUpdate.ToolTip = ready ? "Horizon'ı ve paketi yeni sürüme getir." : "Horizon'ı indir ve kendi Minecraft dosyalarından hazırla.";
                ApplyPlayGate();
                return;
            }

            // Kurulu mu? (manifest + launcher_info var) ve sürüm eski mi?
            var manifestPath = Path.Combine(CurrentPackDir, "manifest.json");
            var localInfoPath = Path.Combine(CurrentPackDir, "launcher_info.json");
            bool installed = File.Exists(manifestPath) && File.Exists(localInfoPath);

            if (installed)
            {
                try
                {
                    var localInfo = JsonSerializer.Deserialize<LocalPackInfo>(File.ReadAllText(localInfoPath));
                    if (localInfo != null && localInfo.Version != currentPack.Version)
                        updateAvailable = true;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"launcher_info.json okunamadı: {ex.Message}");
                }
            }

            // Paket henüz yayında değilse (ZipUrl boş) kurulum/oynama kapalı kalır.
            bool packReady = !string.IsNullOrWhiteSpace(currentPack.ZipUrl);
            BtnUpdate.IsEnabled = packReady;
            BtnUpdate.Content = !packReady ? "Hazırlanıyor" : (updateAvailable ? "Güncelle" : (installed ? "Onar" : "Kur"));
            BtnUpdate.ToolTip = !packReady ? "Bu paket henüz yayınlanmadı — dosyaları eklenince otomatik açılır."
                              : updateAvailable ? "Yeni sürüm var — paketi güncelle (ayarların korunur)."
                              : installed ? "Dosyaları doğrula, eksikleri tamamla (onar)."
                              : "Paketi kur.";
            ApplyPlayGate();
        }

        /// <summary>
        /// OYNA butonunu sunucu durumuna göre kilitler/açar. GÜNCELLE bundan etkilenmez.
        /// Sunucu çevrimdışıysa oynanamaz; çevrimiçi olunca otomatik açılır.
        /// </summary>
        private void ApplyPlayGate()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(ApplyPlayGate);
                return;
            }

            if (_operationInProgress) return;

            switch (_serverOnline)
            {
                case true:
                    BtnPlay.IsEnabled = true;
                    BtnPlay.Content = "OYNA";
                    BtnPlay.ToolTip = null;
                    break;
                case false:
                    BtnPlay.IsEnabled = false;
                    BtnPlay.Content = "SUNUCU KAPALI";
                    BtnPlay.ToolTip = "Sunucu çevrimdışı — oynamak için sunucunun açık olması gerekir.";
                    break;
                default: // null = kontrol ediliyor
                    BtnPlay.IsEnabled = false;
                    BtnPlay.Content = "OYNA";
                    BtnPlay.ToolTip = "Sunucu durumu kontrol ediliyor...";
                    break;
            }
        }

        private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (currentPack?.IsHorizon == true) await InstallHorizonWithUiAsync();
            else await HandleInstallation(updateAvailable);
        }

        private async void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (currentPack == null)
                return;

            if (currentPack.IsHorizon)
            {
                await PlayHorizonAsync();
                return;
            }

            // Paket dosyaları henüz yayınlanmamışsa (ZipUrl boş) kurulum denenemez.
            if (string.IsNullOrWhiteSpace(currentPack.ZipUrl) &&
                !File.Exists(Path.Combine(CurrentPackDir, "manifest.json")))
            {
                FWDialog.Warn("Bu paket henüz hazırlanıyor — dosyaları yayınlanınca oynanabilir olacak.",
                    "Paket Hazır Değil");
                return;
            }

            // Güncelleme varsa önce güncellenmeli: eski sürümle sunucuya girilemez.
            if (updateAvailable)
            {
                FWDialog.Warn("Bu paketin yeni sürümü var. Oynamadan önce güncellemelisin.", "Güncelleme Gerekli");
                return;
            }

            // Kalıcı kart olduğu için ayarları her tıklamada taze oku (Java/RAM değişmiş olabilir).
            LoadUserSettings();

            // Sunucu çevrimdışıysa oynanamaz (KUR/GÜNCELLE engellenmez).
            if (_serverOnline == false)
            {
                FWDialog.Warn("Sunucu şu anda çevrimdışı. Lütfen sunucu açıldığında tekrar deneyin.",
                    "Sunucu Çevrimdışı");
                return;
            }

            try
            {
                if (!EnsureJavaSelected() || !EnsureRamSelected())
                    return;

                _operationInProgress = true;
                BtnPlay.IsEnabled = false;
                BtnPlay.Content = "Başlatılıyor...";
                BtnPlay.ToolTip = "Oyun hazırlanıyor";

                UpdateStatus("Oyun başlatılıyor...", 0);
                Logger.Info($"OYNA tıklandı · java={currentUserSettings.JavaPath} · ram={currentUserSettings.LastRamInGB}GB");

                int ramMB = currentUserSettings.LastRamInGB * 1024;
                string javaPath = currentUserSettings.JavaPath!;
                string packDir = CurrentPackDir;

                bool started = await AutoCheckAndLaunch(packDir, ramMB, javaPath);

                if (!started)
                {
                    ResetPlayButton();
                    return;
                }

                // Oyun ayrı bir süreç olarak çalışıyor; ayara göre launcher'ı küçült.
                BtnPlay.Content = "Çalışıyor";
                BtnPlay.ToolTip = "Oyun çalışıyor";
                UpdateStatus("Oyun çalışıyor...", 100);

                // Oyun açıkken launcher arka planda gizlenir (görev çubuğunda da görünmez); oyun kapanınca geri gelir.
                var window = Window.GetWindow(this);
                if (window != null && currentUserSettings.MinimizeOnLaunch)
                {
                    if (window is MainWindow mw) mw.HideToTray(); else window.Hide();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Oyun başlatılırken hata.", ex);
                FWDialog.Error($"Oyun başlatılırken hata oluştu:\n{ex.Message}");
                ResetPlayButton();
            }
        }

        private bool EnsureJavaSelected()
        {
            if (!string.IsNullOrWhiteSpace(currentUserSettings.JavaPath) && File.Exists(currentUserSettings.JavaPath))
                return true;

            // Java seçilmemiş/geçersiz: sistemde otomatik bulmayı dene (kurulumda da bu kullanılıyor).
            string auto = AutoFindJava(GetPackMcVersion());
            if (!string.IsNullOrWhiteSpace(auto) && File.Exists(auto))
            {
                currentUserSettings.JavaPath = auto;
                try { SettingsService.Save(currentUserSettings); } catch (Exception ex) { Logger.Warn($"Java yolu kaydedilemedi: {ex.Message}"); }
                Logger.Info($"Java otomatik bulundu ve ayarlandı: {auto}");
                return true;
            }

            FWDialog.Warn("Sisteminizde uygun bir Java bulunamadı. Lütfen Ayarlardan Java 21 yolunu seçin.",
                "Java Bulunamadı");
            (Window.GetWindow(this) as MainWindow)?.NavigateToSettings();
            return false;
        }

        private bool EnsureRamSelected()
        {
            if (currentUserSettings.LastRamInGB <= 0)
            {
                FWDialog.Warn("Lütfen önce Ayarlardan geçerli bir RAM değeri seçin.", "RAM Ayarlanmamış");
                (Window.GetWindow(this) as MainWindow)?.NavigateToSettings();
                return false;
            }
            return true;
        }

        private void ResetPlayButton()
        {
            _operationInProgress = false;
            BtnPlay.IsEnabled = true;
            BtnPlay.Content = "OYNA";
            BtnPlay.ToolTip = null;
            ApplyPlayGate(); // sunucu çevrimdışıysa tekrar kilitle
        }

        private async Task<bool> AutoCheckAndLaunch(string packDir, int ramMB, string javaPath)
        {
            var manifestPath = Path.Combine(packDir, "manifest.json");

            if (!File.Exists(manifestPath))
            {
                FWDialog.Error("Manifest dosyası eksik, lütfen paketi yeniden yükleyin.");
                return false;
            }

            var manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(manifestPath));
            if (manifest?.Minecraft == null || string.IsNullOrWhiteSpace(manifest.Minecraft.Version))
            {
                FWDialog.Error("Manifest dosyası geçersiz, lütfen paketi yeniden yükleyin.");
                return false;
            }

            // Bozuk mod dosyasi (0 bayt / yarim indirme) varsa oyun ilk saniyede kapanir: once paketi onar.
            var modsDir = Path.Combine(packDir, "mods");
            var bozuk = Directory.Exists(modsDir)
                ? Directory.GetFiles(modsDir, "*.jar").Where(f => new FileInfo(f).Length == 0).ToList()
                : new List<string>();
            if (bozuk.Count > 0)
            {
                Logger.Warn($"{bozuk.Count} bozuk mod dosyası bulundu, paket onarılıyor: {string.Join(", ", bozuk.Select(Path.GetFileName).Take(10))}");
                foreach (var f in bozuk) TryDeleteFile(f);
                UpdateStatus($"{bozuk.Count} bozuk mod onarılıyor...", 5);
                try { await InstallOrUpdatePackAsync(packDir); }
                catch (Exception ex)
                {
                    Logger.Error("Paket onarımı başarısız.", ex);
                    FWDialog.Error($"Bozuk mod dosyaları onarılamadı: {ex.Message}");
                    return false;
                }
            }

            var launcher = new MinecraftLauncher(new MinecraftPath(packDir));
            string? versionName = await ResolveInstalledVersionAsync(launcher, manifest);

            if (versionName == null)
            {
                UpdateStatus("Mod yükleyici bulunamadı, eksikler tamamlanıyor...", 85);
                try
                {
                    // Eksik parçaları tamamla (configleri ve mevcut modları bozmadan).
                    await InstallOrUpdatePackAsync(packDir);
                }
                catch (Exception ex)
                {
                    Logger.Error("Mod yükleyici otomatik kurulumu başarısız.", ex);
                    FWDialog.Error($"Mod yükleyici kurulumu başarısız oldu: {ex.Message}");
                    return false;
                }
            }
            else if (!VanillaLibsComplete())
            {
                // Mod yükleyici kurulu ama vanilla kütüphaneleri eksik (lwjgl vb.) → onar.
                try
                {
                    UpdateStatus("Eksik oyun dosyaları indiriliyor...", 0);
                    await InstallVanillaAsync(launcher, manifest.Minecraft.Version);
                }
                catch (Exception ex)
                {
                    Logger.Error("Eksik oyun dosyaları indirilemedi.", ex);
                    FWDialog.Error($"Oyun dosyaları doğrulanamadı: {ex.Message}");
                    return false;
                }
            }

            return await LaunchGame(packDir);
        }

        /// <summary>Kurulu sürümler arasından paketin mod yükleyicisine (Forge/NeoForge/Vanilla) ait olanı bulur.</summary>
        private async Task<string?> ResolveInstalledVersionAsync(MinecraftLauncher launcher, Manifest manifest)
        {
            var loader = manifest.Minecraft.ModLoaders.FirstOrDefault(m => m.Primary)
                         ?? manifest.Minecraft.ModLoaders.FirstOrDefault();
            var kind = ModLoaderService.Detect(loader?.Id);
            string loaderVersion = ModLoaderService.ExtractVersion(loader?.Id ?? "");
            string mc = manifest.Minecraft.Version;

            var versions = await launcher.GetAllVersionsAsync();
            return ModLoaderService.FindInstalledVersion(kind, mc, loaderVersion, versions.Select(v => v.Name));
        }

        /// <summary>
        /// Vanilla kütüphanelerinin (özellikle lwjgl) indirilmiş olup olmadığını hızlıca kontrol eder.
        /// Eksikse oyun başlarken "org.lwjgl.system.Struct" hatasıyla çöker.
        /// </summary>
        private bool VanillaLibsComplete()
        {
            try
            {
                var lwjgl = Path.Combine(CurrentPackDir, "libraries", "org", "lwjgl");
                return Directory.Exists(lwjgl)
                    && Directory.EnumerateFiles(lwjgl, "*.jar", SearchOption.AllDirectories).Any();
            }
            catch
            {
                return false;
            }
        }

        private async Task HandleInstallation(bool isUpdate)
        {
            if (currentPack == null)
                return;

            await RunAction(async () =>
            {
                var packDir = CurrentPackDir;
                UpdateStatus(isUpdate ? "Paket güncelleniyor..." : "Yeni kurulum başlıyor...", 0);
                await InstallOrUpdatePackAsync(packDir);
                UpdateStatus(isUpdate ? "Güncelleme tamamlandı!" : "Kurulum tamamlandı!", 100);
                UpdateBadges();
                FWDialog.Success(
                    isUpdate
                        ? "Güncelleme tamamlandı. Mevcut ayarların korundu."
                        : "Kurulum başarıyla tamamlandı. Artık 'OYNA'ya basabilirsiniz.");
                UpdateUI();
            });
        }

        /// <summary>
        /// Paketi kurar veya günceller — klasörü SİLMEDEN. Mevcut configler korunur; yalnızca
        /// eksik modlar indirilir, pakete ait olmayan modlar silinir, kurulu sürümler atlanır.
        /// Hem ilk kurulum hem güncelleme hem onarım için kullanılır.
        /// </summary>
        private async Task InstallOrUpdatePackAsync(string packDirectory)
        {
            if (currentPack == null)
                throw new InvalidOperationException("Mod paketi bilgisi yüklenmedi.");

            Directory.CreateDirectory(packDirectory);
            var modsDirectory = Path.Combine(packDirectory, "mods");
            Directory.CreateDirectory(modsDirectory);

            // 1) Zip'i indir, manifesti oku ve yeni dosyaları configleri bozmadan uygula.
            UpdateStatus("Paket bilgileri indiriliyor...", 0);
            var tempZipPath = Path.Combine(Path.GetTempPath(), $"{currentPack.Id}.zip");
            await DownloadFileAsync(currentPack.ZipUrl, tempZipPath, "Paket indiriliyor", 0, 18);

            UpdateStatus("Paket dosyaları denetleniyor...", 20);
            Manifest manifest;
            var overrideMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var archive = ZipFile.OpenRead(tempZipPath))
            {
                var manifestEntry = archive.GetEntry("manifest.json")
                    ?? throw new Exception("Pakette manifest.json bulunamadı.");

                using (var stream = manifestEntry.Open())
                {
                    manifest = await JsonSerializer.DeserializeAsync<Manifest>(stream)
                        ?? throw new Exception("manifest.json okunamadı.");
                }

                // manifest.json bir meta veridir -> her zaman güncellenir.
                manifestEntry.ExtractToFile(Path.Combine(packDirectory, "manifest.json"), true);

                foreach (var entry in archive.Entries.Where(en => en.FullName.StartsWith("overrides/")))
                {
                    var relative = entry.FullName.Substring("overrides/".Length);
                    if (string.IsNullOrEmpty(relative) || string.IsNullOrEmpty(entry.Name))
                        continue;

                    // overrides/mods içindeki jar'ları "korunacak" listesine ekle (manifestte olmasalar da silinmesin).
                    var normalized = relative.Replace('\\', '/');
                    if (normalized.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)
                        && normalized.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                        overrideMods.Add(Path.GetFileName(normalized));

                    var destPath = Path.Combine(packDirectory, relative);

                    // KORUMA: dosya zaten varsa üzerine yazma (kullanıcının config ayarları bozulmasın).
                    // Mod jar'lari istisna: boyutu farkliysa (yeni surum / bozuk) yenisi yazilir.
                    bool modJar = normalized.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)
                                  && normalized.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
                    if (File.Exists(destPath) && !(modJar && new FileInfo(destPath).Length != entry.Length))
                        continue;

                    var destDir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(destDir))
                        Directory.CreateDirectory(destDir);
                    entry.ExtractToFile(destPath, true);
                }
            }
            TryDeleteFile(tempZipPath);

            if (string.IsNullOrWhiteSpace(manifest.Minecraft.Version))
                throw new Exception("Manifest içinde Minecraft sürümü belirtilmemiş.");

            // 2) Oyun (vanilla + mod yükleyici) zaten kurulu mu? Kuruluysa uzun kurulumları atla.
            var launcher = new MinecraftLauncher(new MinecraftPath(packDirectory));
            var loaderEntry = manifest.Minecraft.ModLoaders.FirstOrDefault(m => m.Primary)
                              ?? manifest.Minecraft.ModLoaders.FirstOrDefault();
            var kind = ModLoaderService.Detect(loaderEntry?.Id);
            var loaderVer = ModLoaderService.ExtractVersion(loaderEntry?.Id ?? "");
            var versionNames = (await launcher.GetAllVersionsAsync()).Select(v => v.Name).ToList();

            bool loaderInstalled = kind == ModLoaderKind.Vanilla
                ? versionNames.Any(n => n == manifest.Minecraft.Version)
                : ModLoaderService.FindInstalledVersion(kind, manifest.Minecraft.Version, loaderVer, versionNames, exactOnly: true) != null;

            // Vanilla'yı HER ZAMAN doğrula (idempotent: yalnızca eksik dosyaları indirir).
            // ÖNEMLİ: NeoForge/Forge --installClient vanilla kütüphanelerini (lwjgl vb.) indirmez;
            // bu yüzden vanilla atlanırsa oyun "org.lwjgl.system.Struct" ile çöker.
            await InstallVanillaAsync(launcher, manifest.Minecraft.Version);

            // 3) Modları akıllıca senkronize et (eksikleri indir, fazlaları sil, mevcutları koru).
            await SyncModsAsync(manifest, modsDirectory, overrideMods);

            // 4) Mod yükleyiciyi yalnızca gerekiyorsa kur.
            if (!loaderInstalled)
                await InstallLoaderIfNeededAsync(manifest, packDirectory);
            else
                UpdateStatus("Mod yükleyici zaten kurulu.", 97);

            // 5) Yerel sürüm bilgisini güncelle.
            await File.WriteAllTextAsync(Path.Combine(packDirectory, "launcher_info.json"),
                JsonSerializer.Serialize(new LocalPackInfo { Id = currentPack.Id, Version = currentPack.Version }));

            UpdateStatus("Hazır!", 100);
        }

        private async Task InstallVanillaAsync(MinecraftLauncher launcher, string mcVersion)
        {
            UpdateStatus("Vanilla Minecraft kuruluyor...", 30);

            // Gerçek indirme ilerlemesini [30, 45] aralığına yansıt.
            var lastReport = DateTime.MinValue;
            var byteProgress = new Progress<ByteProgress>(b =>
            {
                double ratio = b.ToRatio();
                if (double.IsNaN(ratio) || ratio < 0) return;
                if (ratio > 1) ratio = 1;

                if ((DateTime.Now - lastReport).TotalMilliseconds < 100 && ratio < 1) return;
                lastReport = DateTime.Now;

                SetProgress(30 + ratio * 15);
                SetStatusText($"Vanilla Minecraft kuruluyor...  (%{ratio * 100:0})");
            });

            var fileProgress = new Progress<InstallerProgressChangedEventArgs>(_ => { });

            // Mojang sunucuları uzun indirmelerde bağlantıyı yarıda kesebiliyor (TLS EOF, bağlantı sıfırlama).
            // CmlLib inmiş dosyaları atladığı için yeniden denemek kaldığı yerden devam eder.
            const int maxAttempts = 5;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await launcher.InstallAsync(mcVersion, fileProgress, byteProgress, CancellationToken.None);
                    break;
                }
                catch (Exception ex) when (attempt < maxAttempts && IsTransientNetworkError(ex))
                {
                    Logger.Warn($"Vanilla indirmesi kesildi ({attempt}/{maxAttempts}), yeniden deneniyor: {ex.Message}");
                    SetStatusText($"Bağlantı koptu, yeniden deneniyor... ({attempt}/{maxAttempts - 1})");
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
                }
            }
            UpdateStatus("Vanilla Minecraft kuruldu.", 45);
        }

        private static bool IsTransientNetworkError(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                // 404 gibi kalıcı HTTP hataları yeniden denenmez; yalnız 5xx ve bağlantı hataları.
                if (e is HttpRequestException hre && hre.StatusCode is { } code)
                    return (int)code >= 500;
                if (e is IOException or HttpRequestException or System.Net.Sockets.SocketException or TaskCanceledException)
                    return true;
                if (e is AggregateException agg && agg.InnerExceptions.Any(IsTransientNetworkError))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Mod klasörünü manifeste göre akıllıca senkronize eder: pakete ait olmayan modları siler,
        /// eksikleri indirir, zaten var olanları yeniden indirmez. Configleri etkilemez.
        /// </summary>
        private async Task SyncModsAsync(Manifest manifest, string modsDirectory, HashSet<string> keepExtra)
        {
            UpdateStatus("Modlar denetleniyor...", 45);

            // İstenen mod kümesi: manifest dosya kimlikleri -> (dosya adı, indirme adresi)
            var desired = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var expectedSize = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (manifest.Files.Count > 0)
            {
                var body = new GetModFilesRequestBody { FileIds = manifest.Files.Select(f => (int)f.FileID).ToList() };
                var resp = await cfApiClient.GetFilesAsync(body);
                foreach (var mf in resp.Data)
                {
                    if (string.IsNullOrWhiteSpace(mf.FileName)) continue;
                    // Yazari "ucuncu taraf indirmesi"ni kapatan modlarda API adres vermez: CurseForge CDN adresi dosya kimliginden kurulur.
                    desired[mf.FileName] = !string.IsNullOrWhiteSpace(mf.DownloadUrl) ? mf.DownloadUrl
                        : $"https://edge.forgecdn.net/files/{mf.Id / 1000}/{mf.Id % 1000}/{Uri.EscapeDataString(mf.FileName)}";
                    if (mf.FileLength > 0) expectedSize[mf.FileName] = mf.FileLength;
                }
            }

            // 1) Pakete ait olmayan modları sil (manifestte ve override modlarında olmayan .jar'lar).
            int removed = 0;
            foreach (var existing in Directory.GetFiles(modsDirectory, "*.jar"))
            {
                var name = Path.GetFileName(existing);
                if (desired.ContainsKey(name) || keepExtra.Contains(name))
                    continue;

                TryDeleteFile(existing);
                removed++;
                Logger.Info($"Pakete ait olmayan mod silindi: {name}");
            }

            // 2) Eksik modları indir (mevcut olanları atla).
            int total = desired.Count, i = 0, downloaded = 0, skipped = 0;
            foreach (var kv in desired)
            {
                i++;
                var dest = Path.Combine(modsDirectory, kv.Key);
                var progress = 45 + (int)(35 * ((double)i / Math.Max(1, total)));

                // Zaten var ve saglamsa tekrar indirme. Bos (0 bayt) ya da boyutu tutmayan dosya bozuktur: yeniden indir.
                if (File.Exists(dest))
                {
                    long len = new FileInfo(dest).Length;
                    bool saglam = len > 0 && (!expectedSize.TryGetValue(kv.Key, out var bek) || bek == len);
                    if (saglam) { skipped++; continue; }
                    Logger.Warn($"Bozuk mod dosyası yeniden indirilecek: {kv.Key} ({len} bayt)");
                    TryDeleteFile(dest);
                }

                if (string.IsNullOrWhiteSpace(kv.Value))
                {
                    Logger.Warn($"Mod indirme bağlantısı yok (üçüncü taraf engeli olabilir): {kv.Key}");
                    continue;
                }

                UpdateStatus($"Mod indiriliyor ({i}/{total}): {kv.Key}", progress);
                try { await DownloadFileAsync(kv.Value, dest); downloaded++; }
                catch (Exception ex) { Logger.Warn($"Mod indirilemedi: {kv.Key} - {ex.Message}"); }
            }

            UpdateStatus($"Modlar güncel · {downloaded} indirildi, {skipped} mevcut, {removed} silindi", 80);
            Logger.Info($"Mod senkronizasyonu: {downloaded} indirildi, {skipped} mevcut, {removed} silindi.");
        }

        /// <summary>Paketin mod yükleyicisini (Forge veya NeoForge) headless modda kurar. Vanilla ise atlar.</summary>
        private async Task InstallLoaderIfNeededAsync(Manifest manifest, string packDirectory)
        {
            if (currentPack == null)
                return;

            var loader = manifest.Minecraft.ModLoaders.FirstOrDefault(m => m.Primary)
                         ?? manifest.Minecraft.ModLoaders.FirstOrDefault();
            var kind = ModLoaderService.Detect(loader?.Id);
            if (kind == ModLoaderKind.Vanilla)
                return; // Saf vanilla paketi; ek kurulum gerekmez.

            string loaderVersion = ModLoaderService.ExtractVersion(loader!.Id);
            string displayName = ModLoaderService.DisplayName(kind);

            UpdateStatus($"{displayName} indiriliyor...", 80);

            // ?? yalnızca null'da devreye girer; JSON'da boş string ("") gelirse fallback'e düşmez.
            // Bu yüzden boş/whitespace olan değerleri de atlayıp sıradaki kaynağa geçiyoruz:
            // açık LoaderInstallerUrl -> ForgeInstallerUrl -> manifest loader sürümünden üretilen resmî maven URL'si.
            string url = currentPack.LoaderInstallerUrl ?? "";
            if (string.IsNullOrWhiteSpace(url))
                url = currentPack.ForgeInstallerUrl ?? "";
            if (string.IsNullOrWhiteSpace(url))
                url = ModLoaderService.BuildInstallerUrl(kind, manifest.Minecraft.Version, loaderVersion);

            if (string.IsNullOrWhiteSpace(url))
                throw new Exception($"{displayName} yükleyici adresi belirlenemedi.");

            string installerJar = Path.Combine(Path.GetTempPath(), $"{kind}-installer.jar".ToLowerInvariant());
            await DownloadFileAsync(url, installerJar, $"{displayName} indiriliyor", 80, 88);

            string javaExe = currentUserSettings.JavaPath ?? "";
            if (string.IsNullOrWhiteSpace(javaExe) || !File.Exists(javaExe))
                javaExe = AutoFindJava(manifest.Minecraft.Version);

            if (string.IsNullOrWhiteSpace(javaExe) || !File.Exists(javaExe))
                throw new Exception("Java bulunamadı! Lütfen geçerli bir Java yolu ayarlayın.");

            // Forge/NeoForge yükleyicisi bir launcher_profiles.json bekler.
            EnsureFakeLauncherProfile(packDirectory, manifest.Minecraft.Version);

            UpdateStatus($"{displayName} kuruluyor...", 90);
            int exitCode = await RunJavaInstallerAsync(javaExe, installerJar, packDirectory);
            if (exitCode != 0)
                throw new Exception($"{displayName} kurulumu başarısız oldu (kod: {exitCode}).");

            TryDeleteFile(installerJar);
            UpdateStatus($"{displayName} kurulumu tamamlandı.", 97);
        }

        private static void EnsureFakeLauncherProfile(string gameRoot, string mcVersion)
        {
            string path = Path.Combine(gameRoot, "launcher_profiles.json");
            if (File.Exists(path))
                return;

            string json = $$"""
            {
                "profiles": {
                    "FWLauncherV2": {
                        "created": "{{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}}",
                        "icon": "Furnace",
                        "lastUsed": "{{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}}",
                        "lastVersionId": "{{mcVersion}}",
                        "name": "FWLauncherV2",
                        "type": "custom"
                    }
                },
                "settings": {},
                "version": 3
            }
            """;
            File.WriteAllText(path, json);
        }

        private static async Task<int> RunJavaInstallerAsync(string javaExe, string installerJar, string gameRoot)
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = $"-jar \"{installerJar}\" --installClient \"{gameRoot}\"",
                WorkingDirectory = gameRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (s, e) => { if (e.Data != null) Logger.Info("[LOADER] " + e.Data); };
            proc.ErrorDataReceived += (s, e) => { if (e.Data != null) Logger.Warn("[LOADER-ERR] " + e.Data); };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(); // UI'yi dondurmaz
            return proc.ExitCode;
        }

        internal static string AutoFindJava(string minecraftVersion = "")
        {
            try
            {
                int requiredMajor = 17;
                if (!string.IsNullOrWhiteSpace(minecraftVersion))
                {
                    if (minecraftVersion.StartsWith("1.21"))
                        requiredMajor = 21;
                    else if (minecraftVersion.StartsWith("1.20") || minecraftVersion.StartsWith("1.17")
                             || minecraftVersion.StartsWith("1.18") || minecraftVersion.StartsWith("1.19"))
                        requiredMajor = 17;
                    else if (minecraftVersion.StartsWith("1.12") || minecraftVersion.StartsWith("1.16")
                             || minecraftVersion.StartsWith("1.8"))
                        requiredMajor = 8;
                }

                string major = requiredMajor.ToString();

                // 1) Launcher'ın kendi indirdiği Java
                var javaFolder = Path.Combine(SettingsService.LauncherDirectory, "javalar");
                if (Directory.Exists(javaFolder))
                {
                    var found = Directory.GetFiles(javaFolder, "java.exe", SearchOption.AllDirectories)
                        .FirstOrDefault(f => f.Contains(major));
                    if (!string.IsNullOrEmpty(found)) return found;
                }

                // 2) Yaygın JDK kurulum konumları (Oracle, Adoptium, Microsoft, Zulu, Corretto...)
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                var roots = new[]
                {
                    Path.Combine(pf, "Java"),
                    Path.Combine(pf, "Eclipse Adoptium"),
                    Path.Combine(pf, "Microsoft"),
                    Path.Combine(pf, "Zulu"),
                    Path.Combine(pf, "Amazon Corretto"),
                    Path.Combine(pf, "BellSoft"),
                    Path.Combine(pf86, "Java"),
                };

                foreach (var root in roots)
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var jdk in Directory.GetDirectories(root))
                    {
                        if (!Path.GetFileName(jdk).Contains(major)) continue;
                        var p = Path.Combine(jdk, "bin", "java.exe");
                        if (File.Exists(p)) return p;
                    }
                }

                // 3) JAVA_HOME ortam değişkeni
                var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
                if (!string.IsNullOrWhiteSpace(javaHome))
                {
                    var p = Path.Combine(javaHome, "bin", "java.exe");
                    if (File.Exists(p)) return p;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Otomatik Java araması başarısız: {ex.Message}");
            }

            return "";
        }

        /// <summary>Kurulu paketin Minecraft sürümünü döndürür (Java major'unu seçmek için).</summary>
        private string GetPackMcVersion()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(currentPack?.McVersion))
                    return currentPack!.McVersion!;

                var manifestPath = Path.Combine(CurrentPackDir, "manifest.json");
                if (File.Exists(manifestPath))
                {
                    var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath));
                    if (!string.IsNullOrWhiteSpace(m?.Minecraft?.Version))
                        return m!.Minecraft.Version;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Paket MC sürümü okunamadı: {ex.Message}");
            }
            return "";
        }

        /// <summary>Launcher'ın başlattığı çalışan oyun süreci (logout'ta kapatılır — tek hesap kuralı).</summary>
        public static Process? RunningGame;

        /// <summary>
        /// Launcher'dan çıkış: açık oyun sürecini kapatır + pokewing_auth.json'u siler.
        /// (Mod tarafı da dosyayı izler: silinince oyun kendini kapatır — süreç kapatma başarısız olsa bile çift güvence.)
        /// </summary>
        private static bool _killedByLauncher;

        public static void KillGameAndClearAuth()
        {
            try
            {
                if (RunningGame != null && !RunningGame.HasExited)
                {
                    _killedByLauncher = true;
                    RunningGame.Kill(entireProcessTree: true);
                    Logger.Info("Çıkış yapıldı: çalışan oyun süreci kapatıldı.");
                }
            }
            catch (Exception ex) { Logger.Warn($"Oyun süreci kapatılamadı: {ex.Message}"); }
            RunningGame = null;
            try
            {
                // Auth dosyası artık her paketin kendi çalışma dizinine yazılıyor.
                // Hangi paketin çalıştığını bilmediğimiz için kök + tüm packs\* alt klasörlerini temizle.
                var root = SettingsService.LauncherDirectory;
                var targets = new List<string> { Path.Combine(root, "pokewing_auth.json") };
                var packsDir = Path.Combine(root, "packs");
                if (Directory.Exists(packsDir))
                    targets.AddRange(Directory.GetDirectories(packsDir)
                        .Select(d => Path.Combine(d, "pokewing_auth.json")));
                foreach (var auth in targets)
                    if (File.Exists(auth)) File.Delete(auth);
            }
            catch (Exception ex) { Logger.Warn($"Auth dosyası silinemedi: {ex.Message}"); }
        }

        private async Task<bool> LaunchGame(string packDir)
        {
            if (currentPack == null)
                return false;

            var launcher = new MinecraftLauncher(new MinecraftPath(packDir));
            var manifest = JsonSerializer.Deserialize<Manifest>(
                await File.ReadAllTextAsync(Path.Combine(packDir, "manifest.json")));

            if (manifest?.Minecraft == null || string.IsNullOrWhiteSpace(manifest.Minecraft.Version))
            {
                FWDialog.Error("Manifest dosyası geçersiz.");
                return false;
            }

            string mc = manifest.Minecraft.Version;
            string? versionName = await ResolveInstalledVersionAsync(launcher, manifest);

            if (versionName == null)
            {
                FWDialog.Error("Mod yükleyici sürümü bulunamadı!");
                return false;
            }

            var jvmArgs = new List<MArgument>
            {
                new MArgument("-Dfile.encoding=UTF-8"),
                new MArgument("-Duser.language=en"),
                new MArgument("-Duser.country=US"),
                new MArgument("-XX:+UseG1GC"),
                new MArgument("-XX:+UnlockExperimentalVMOptions"),
                new MArgument("-XX:+AlwaysPreTouch"),
                new MArgument("-XX:ParallelGCThreads=4"),
                new MArgument("-Dsun.jnu.encoding=UTF-8"),
                new MArgument("-Djava.locale.providers=COMPAT,SPI"),
                new MArgument("-Dusing.aikars.flags=true")
            };

            var gameArgs = new List<MArgument>();

            // Kullanıcının ek JVM bayrakları (Ayarlar → Java ve Bellek).
            if (!string.IsNullOrWhiteSpace(currentUserSettings.ExtraJvmArgs))
                foreach (var a in currentUserSettings.ExtraJvmArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (a.StartsWith("-")) jvmArgs.Add(new MArgument(a));

            var opts = new MLaunchOption
            {
                Session = MSession.CreateOfflineSession(currentUsername),
                JavaPath = currentUserSettings.JavaPath,
                MaximumRamMb = currentUserSettings.LastRamInGB * 1024,
                ExtraJvmArguments = jvmArgs,
                FullScreen = currentUserSettings.FullScreen,
                ScreenWidth = Math.Clamp(currentUserSettings.GameWidth, 640, 7680),
                ScreenHeight = Math.Clamp(currentUserSettings.GameHeight, 360, 4320)
            };
            ApplyOptionsTxt(packDir, currentUserSettings.FullScreen);

            // Kullanıcı modları + gölgelendirici/kaynak paketi ayarları: iki klasör birleşir (ana paket kazanır).
            try
            {
                var report = UserContentService.PrepareForLaunch(packDir, currentUserSettings.UserModsEnabled);
                if (report.Conflicts.Count > 0)
                    (Window.GetWindow(this) as MainWindow)?.ShowToast(
                        "Ana pakette zaten olduğu için atlanan modlar: " + string.Join(", ", report.Conflicts.Take(4)) +
                        (report.Conflicts.Count > 4 ? $" (+{report.Conflicts.Count - 4})" : ""));
            }
            catch (Exception ex) { Logger.Warn($"Kullanıcı modları hazırlanamadı: {ex.Message}"); }

            // ---- Sunucu bağlantısı ----
            // OTOMATİK BAĞLANMA KAPALI: oyun artık doğrudan sunucuya girmez, PokeWing ana menüsüne düşer.
            // Bunun yerine sunucu bilgisini mod'un menüsüne aktarıyoruz: config/pokewing_servers.json →
            // menüdeki "BAĞLAN" ve "Sunucular" doğru sunucuya gider (oyuncu kendisi girer).
            if (!string.IsNullOrWhiteSpace(currentPack.ServerIp))
            {
                try
                {
                    string host = currentPack.ServerIp.Trim();
                    int port = currentPack.ServerPort > 0 ? currentPack.ServerPort : 25565;
                    if (host.Contains(':'))
                    {
                        var parts = host.Split(':');
                        host = parts[0];
                        if (int.TryParse(parts[1], out int p)) port = p;
                    }
                    string addr = $"{host}:{port}";
                    string name = string.IsNullOrWhiteSpace(currentPack.Name) ? "FasterMC Dungeons" : currentPack.Name;
                    string cfgDir = Path.Combine(packDir, "config");
                    Directory.CreateDirectory(cfgDir);
                    var serverCfg = new
                    {
                        primary = addr,
                        servers = new[] { new { name, desc = "FasterMC Dungeons [MMORPG]", ip = addr } }
                    };
                    File.WriteAllText(Path.Combine(cfgDir, "pokewing_servers.json"),
                        JsonSerializer.Serialize(serverCfg, new JsonSerializerOptions { WriteIndented = true }));
                    Logger.Info($"PokeWing menü sunucusu yazıldı: {addr} (otomatik bağlanma KAPALI)");
                }
                catch (Exception ex) { Logger.Warn($"pokewing_servers.json yazılamadı: {ex.Message}"); }
            }

            // ---- Sunucu modu (cobblemon/roleplay): paket tanımında Mode doluysa kilitli yaz,
            //      boşsa (ve dosya bizim kilitli yazdığımızsa) sil → mod site ayarını kullanır. ----
            try
            {
                string cfgDir2 = Path.Combine(packDir, "config");
                Directory.CreateDirectory(cfgDir2);
                string modeFile = Path.Combine(cfgDir2, "pokewing_mode.json");
                string? packMode = currentPack?.Mode?.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(packMode))
                {
                    File.WriteAllText(modeFile, $"{{\"mode\":\"{packMode}\",\"lock\":true}}");
                    Logger.Info($"Sunucu modu (paketten) yazıldı: {packMode}");
                }
                else if (File.Exists(modeFile) && File.ReadAllText(modeFile).Contains("\"lock\":true"))
                {
                    File.Delete(modeFile);   // paket modu belirtmiyor → siteden gelsin
                }
            }
            catch (Exception ex) { Logger.Warn($"pokewing_mode.json yazılamadı: {ex.Message}"); }

            opts.ExtraGameArguments = gameArgs;

            // ---- Launcher-token kimlik dosyası (AuthMe yerine): mod bunu okuyup sunucuya doğrulatır ----
            // Sunucu bu token + HWID'yi validate_session.php ile kontrol eder → sahte launcher/token giremez.
            try
            {
                var auth = new
                {
                    username = currentUsername,
                    token = currentUserSettings.SessionToken ?? "",
                    hwid = HardwareId.Get(),
                    launcherPid = Environment.ProcessId,   // mod bekçisi izler: launcher ölürse oyun kapanır
                    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                // ÖNEMLİ: Auth dosyası oyunun ÇALIŞMA dizinine (packDir) yazılmalı; mod onu orada arar.
                // Tüm paketler packs\{Id} altında çalışır.
                // Eskiden hep launcherPath'e yazılıyordu → alt-klasör paketlerinde (ör. RolePlay)
                // mod dosyayı bulamayıp "Doğrulanamadı" ile kick atıyordu.
                File.WriteAllText(Path.Combine(packDir, "pokewing_auth.json"),
                    System.Text.Json.JsonSerializer.Serialize(auth));
            }
            catch (Exception ex) { Logger.Warn($"Auth dosyası yazılamadı: {ex.Message}"); }

            Logger.Info($"Oyun süreci oluşturuluyor · sürüm={versionName}");
            var process = await launcher.BuildProcessAsync(versionName, opts);
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.UseShellExecute = false;
            // NeoForge erken yükleme ekranı: kırmızı Mojang teması yerine KOYU tema (PokeWing'e uygun).
            process.StartInfo.EnvironmentVariables["FML_EARLY_WINDOW_DARK"] = "true";

            AttachExitHandler(process, packDir);

            process.Start();
            RunningGame = process;   // logout'ta kapatılabilsin (tek hesap kuralı)
            LocalStatsService.MarkLaunch(currentUsername);
            Logger.Info($"Oyun süreci başlatıldı · PID={process.Id}");

            UpdateStatus("Oyun başlatıldı!", 100);
            return true;
        }

        /// <summary>Oyun kapandığında launcher'ı geri getirir, süreyi kaydeder ve OYNA butonunu sıfırlar.</summary>
        private void AttachExitHandler(Process process, string packDir)
        {
            process.EnableRaisingEvents = true;
            var startedAt = DateTime.Now;
            string user = currentUsername;
            process.Exited += (s, e) =>
            {
                RunningGame = null;
                int exitCode = 0;
                try { exitCode = process.ExitCode; } catch { }
                var played = DateTime.Now - startedAt;
                // Sıfırdan farklı çıkış kodu = çökme (oyuncu menüden çıkınca kod 0 olur; launcher'ın kendisi kapattıysa sayılmaz).
                bool crashed = exitCode != 0 && !_killedByLauncher;
                _killedByLauncher = false;
                LocalStatsService.AddSession(user, played, crashed);
                try
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        var window = Window.GetWindow(this);
                        if (window != null)
                        {
                            // Gizlendiyse her durumda görünür yap (yoksa launcher'a ulaşılamaz).
                            if (!window.IsVisible) window.Show();
                            if (currentUserSettings.RestoreOnExit)
                            {
                                if (window is MainWindow mw) mw.RestoreFromTray();
                                else { window.WindowState = WindowState.Normal; window.Activate(); }
                            }
                            else if (!window.IsActive)
                            {
                                window.WindowState = WindowState.Minimized;
                            }
                        }
                        ResetPlayButton();
                        UpdateStatus(crashed ? $"Oyun beklenmedik şekilde kapandı (kod {exitCode})." : "Hazır.", crashed ? 0 : 100);
                        if (crashed && SettingsService.Load().CrashHelper) OfferCrashLog(packDir, exitCode);
                    });
                }
                catch (Exception ex)
                {
                    // Launcher kapanmışsa dispatcher erişilemez olabilir; yok say.
                    Logger.Warn($"Oyun kapanış olayı işlenemedi: {ex.Message}");
                }
            };
        }

        // ---- Horizon motoru ----

        private void OnHorizonProgress((string Text, int Percent) p) => UpdateStatus(p.Text, p.Percent);

        private string? HorizonTagToPlay() =>
            !string.IsNullOrWhiteSpace(currentPack?.HorizonTag) && HorizonEngine.IsPrepared(currentPack.HorizonTag)
                ? currentPack.HorizonTag
                : HorizonEngine.NewestPrepared();

        /// <summary>Motor hazır mı; paketin sürümü kurulu sürümden farklıysa güncelleme var demektir.</summary>
        private bool HorizonPackReady(out bool update)
        {
            update = false;
            if (HorizonTagToPlay() == null) return false;
            var infoPath = Path.Combine(CurrentPackDir, "launcher_info.json");
            if (!File.Exists(infoPath)) { update = true; return true; }
            try { update = JsonSerializer.Deserialize<LocalPackInfo>(File.ReadAllText(infoPath))?.Version != currentPack?.Version; }
            catch (JsonException ex) { Logger.Warn($"launcher_info.json okunamadı: {ex.Message}"); }
            return true;
        }

        /// <summary>Microsoft kodunu tarayıcıda açar ve panoya kopyalar; giriş bitene dek durum satırında gösterir.</summary>
        private void ShowDeviceCode(string code, string uri) => Dispatcher.Invoke(() =>
        {
            try { Clipboard.SetText(code); } catch (System.Runtime.InteropServices.COMException) { }
            UpdateStatus($"Microsoft girişi: {uri} adresine {code} kodunu gir (kod panoya kopyalandı).", 0);
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception ex) { Logger.Warn($"Tarayıcı açılamadı: {ex.Message}"); }
        });

        /// <summary>
        /// Kurulum: sahiplik kapısı → imzalı sürüm → paket içeriği → oyuncunun kendi Minecraft'ından hazırlama.
        /// Mojang dosyaları yalnızca sahip olduğu doğrulanmış hesap için indirilir.
        /// </summary>
        private async Task InstallHorizonAsync()
        {
            var pack = currentPack ?? throw new InvalidOperationException("Mod paketi bilgisi yüklenmedi.");
            string packDir = CurrentPackDir;
            Directory.CreateDirectory(packDir);
            var progress = new Progress<(string Text, int Percent)>(OnHorizonProgress);

            await MicrosoftAccount.EnsureOwnerAsync(ShowDeviceCode);

            UpdateStatus("Horizon sürümü aranıyor...", 0);
            var release = await HorizonEngine.FindReleaseAsync(pack.HorizonTag);
            await HorizonEngine.InstallAsync(release, progress);

            if (!string.IsNullOrWhiteSpace(pack.ZipUrl))
            {
                var zip = Path.Combine(Path.GetTempPath(), $"{pack.Id}-horizon.zip");
                await DownloadFileAsync(pack.ZipUrl, zip, "Paket indiriliyor", 31, 32);
                HorizonEngine.InstallPackZip(zip, packDir);
                TryDeleteFile(zip);
            }
            HorizonEngine.EnablePackMods(packDir);

            if (!HorizonEngine.IsPrepared(release.Tag))
            {
                var pins = HorizonEngine.ReadPins(release.Tag);
                string? minecraft = HorizonEngine.FindMinecraft(pins);
                if (minecraft == null)
                {
                    minecraft = Path.Combine(HorizonEngine.Root, "minecraft");
                    Directory.CreateDirectory(minecraft);
                    await InstallVanillaAsync(new MinecraftLauncher(new MinecraftPath(minecraft)), pins.Version);
                }
                string? javaHome = HorizonEngine.FindJavaHome(pins.JavaMajor, minecraft);
                if (javaHome == null)
                    throw new Exception($"Hazırlama için bir kez Java {pins.JavaMajor} gerekiyor, bulunamadı. Kurup tekrar dene; oyun çalışırken Java kullanılmaz.");
                await HorizonEngine.PrepareAsync(release.Tag, minecraft, javaHome, progress);
                if (!HorizonEngine.IsPrepared(release.Tag))
                    throw new Exception("Hazırlama bitti ama kaynaklar oluşmadı.");
            }

            File.WriteAllText(Path.Combine(packDir, "launcher_info.json"),
                JsonSerializer.Serialize(new LocalPackInfo { Id = pack.Id, Version = pack.Version }));
        }

        private async Task InstallHorizonWithUiAsync()
        {
            LoadUserSettings();
            _operationInProgress = true;
            SetButtonsEnabled(false);
            try
            {
                await InstallHorizonAsync();
                UpdateStatus("Horizon hazır!", 100);
                FWDialog.Success("Horizon kuruldu. Artık 'OYNA'ya basabilirsiniz.");
            }
            catch (NotOwnerException ex)
            {
                FWDialog.Warn(ex.Message, "Microsoft Hesabı");
                UpdateStatus("Horizon kurulmadı.", 0);
            }
            catch (Exception ex)
            {
                Logger.Error("Horizon kurulumu başarısız.", ex);
                FWDialog.Error(FWDialog.Describe(ex));
                UpdateStatus("İşlem başarısız oldu.", 0);
            }
            finally
            {
                _operationInProgress = false;
                SetButtonsEnabled(true);
                UpdateUI();
            }
        }

        private async Task PlayHorizonAsync()
        {
            LoadUserSettings();
            if (_serverOnline == false && !string.IsNullOrWhiteSpace(currentPack?.ServerIp))
            {
                FWDialog.Warn("Sunucu şu anda çevrimdışı. Lütfen sunucu açıldığında tekrar deneyin.", "Sunucu Çevrimdışı");
                return;
            }

            _operationInProgress = true;
            BtnPlay.IsEnabled = false;
            BtnPlay.Content = "Başlatılıyor...";
            try
            {
                if (HorizonTagToPlay() == null) await InstallHorizonAsync();
                string tag = HorizonTagToPlay() ?? throw new Exception("Horizon kurulamadı.");
                string packDir = CurrentPackDir;

                var session = await MicrosoftAccount.EnsureOwnerAsync(ShowDeviceCode);
                UserContentService.PrepareForLaunch(packDir, currentUserSettings.UserModsEnabled);
                HorizonEngine.EnablePackMods(packDir);

                string? server = null;
                if (!string.IsNullOrWhiteSpace(currentPack?.ServerIp))
                    server = currentPack.ServerIp.Contains(':') ? currentPack.ServerIp.Trim()
                        : $"{currentPack.ServerIp.Trim()}:{(currentPack.ServerPort > 0 ? currentPack.ServerPort : 25565)}";

                var options = await HorizonEngine.LaunchOptionsAsync(tag);
                var info = HorizonEngine.BuildStart(tag, packDir,
                    Math.Clamp(currentUserSettings.GameWidth, 640, 7680), Math.Clamp(currentUserSettings.GameHeight, 360, 4320),
                    currentUserSettings.FullScreen, server, session != null, options);

                var process = new Process { StartInfo = info };
                AttachExitHandler(process, packDir);
                process.Start();
                if (session != null)
                {
                    await process.StandardInput.WriteAsync(HorizonEngine.SessionJson(session));
                    process.StandardInput.Close();
                }
                RunningGame = process;
                LocalStatsService.MarkLaunch(currentUsername);
                Logger.Info($"Horizon başlatıldı · sürüm={tag} · PID={process.Id}");

                BtnPlay.Content = "Çalışıyor";
                UpdateStatus("Oyun çalışıyor...", 100);
                var window = Window.GetWindow(this);
                if (window != null && currentUserSettings.MinimizeOnLaunch)
                {
                    if (window is MainWindow mw) mw.HideToTray(); else window.Hide();
                }
            }
            catch (NotOwnerException ex)
            {
                FWDialog.Warn(ex.Message, "Microsoft Hesabı");
                ResetPlayButton();
            }
            catch (Exception ex)
            {
                Logger.Error("Horizon başlatılırken hata.", ex);
                FWDialog.Error($"Oyun başlatılırken hata oluştu:\n{ex.Message}");
                ResetPlayButton();
            }
        }

        /// <summary>
        /// Dosyayı parça parça (streaming) indirir. progStart/progEnd verilirse indirme yüzdesi
        /// genel ilerleme çubuğuna [progStart, progEnd] aralığında yansıtılır.
        /// </summary>
        private async Task DownloadFileAsync(string url, string dest, string? label = null,
            int progStart = -1, int progEnd = -1)
        {
            const int maxAttempts = 4;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await DownloadFileOnceAsync(url, dest, label, progStart, progEnd);
                    return;
                }
                catch (Exception ex) when (attempt < maxAttempts && IsTransientNetworkError(ex))
                {
                    Logger.Warn($"İndirme kesildi ({attempt}/{maxAttempts}) {url}: {ex.Message}");
                    SetStatusText($"Bağlantı koptu, yeniden deneniyor... ({attempt}/{maxAttempts - 1})");
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
                }
            }
        }

        private async Task DownloadFileOnceAsync(string url, string dest, string? label, int progStart, int progEnd)
        {
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            using var resp = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            long total = resp.Content.Headers.ContentLength ?? -1L;
            bool tracksProgress = progStart >= 0 && progEnd > progStart;

            // Once gecici dosyaya yaz; tamamlanip dogrulaninca yerine koy (disk dolar / baglanti koparsa yarim dosya kalmaz).
            string tmp = dest + ".indiriliyor";
            try
            {
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {

            var buffer = new byte[81920];
            long readTotal = 0;
            int read;
            var lastReport = DateTime.MinValue;

            while ((read = await src.ReadAsync(buffer)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read));
                readTotal += read;

                if (total > 0 && (DateTime.Now - lastReport).TotalMilliseconds > 120)
                {
                    lastReport = DateTime.Now;
                    double pct = (double)readTotal / total;

                    if (label != null)
                    {
                        double mb = readTotal / 1048576.0;
                        double totMb = total / 1048576.0;
                        SetStatusText($"{label}  ·  {mb:0.0} / {totMb:0.0} MB  (%{pct * 100:0})");
                    }

                    if (tracksProgress)
                        SetProgress(progStart + pct * (progEnd - progStart));
                }
            }
            await fs.FlushAsync();
            if (readTotal == 0 || (total > 0 && readTotal != total))
                throw new IOException($"Eksik indirme: {readTotal}/{total} bayt");
            }
            File.Move(tmp, dest, overwrite: true);
            }
            catch
            {
                TryDeleteFile(tmp);
                throw;
            }

            if (tracksProgress)
                SetProgress(progEnd);
        }

        private async Task RunAction(Func<Task> action)
        {
            // Ayarları taze oku (kalıcı kart) + Java yoksa otomatik bul, o da yoksa uyar.
            LoadUserSettings();
            if (!EnsureJavaSelected())
                return;

            _operationInProgress = true;
            SetButtonsEnabled(false);
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Logger.Error("İşlem sırasında hata.", ex);
                FWDialog.Error(FWDialog.Describe(ex));
                UpdateStatus("İşlem başarısız oldu.", 0);
            }
            finally
            {
                _operationInProgress = false;
                SetButtonsEnabled(true);
            }
        }

        public void OpenCurrentModsFolder()
        {
            try
            {
                if (currentPack == null)
                {
                    FWDialog.Warn("Mod paketi yüklenmedi!");
                    return;
                }

                string packDir = CurrentPackDir;
                string modsDir = Path.Combine(packDir, "mods");

                if (!Directory.Exists(modsDir))
                    Directory.CreateDirectory(modsDir);

                Process.Start("explorer.exe", modsDir);
            }
            catch (Exception ex)
            {
                Logger.Error("Mod klasörü açılamadı.", ex);
                FWDialog.Error("Mod klasörü açılamadı:\n" + ex.Message);
            }
        }

        /// <summary>options.txt içindeki tam ekran satırını ayarla uyumlu yazar (oyun ilk açılışta da doğru başlasın).</summary>
        private static void ApplyOptionsTxt(string packDir, bool fullScreen)
        {
            try
            {
                var opt = Path.Combine(packDir, "options.txt");
                var lines = File.Exists(opt) ? File.ReadAllLines(opt).ToList() : new List<string>();
                int i = lines.FindIndex(l => l.StartsWith("fullscreen:"));
                var line = "fullscreen:" + (fullScreen ? "true" : "false");
                if (i >= 0) lines[i] = line; else lines.Add(line);
                File.WriteAllLines(opt, lines);
            }
            catch (Exception ex) { Logger.Warn($"options.txt yazılamadı: {ex.Message}"); }
        }

        /// <summary>Çökmede son günlüğü / çökme raporunu açmayı önerir.</summary>
        private static void OfferCrashLog(string packDir, int exitCode)
        {
            string? report = null;
            try
            {
                var crashDir = Path.Combine(packDir, "crash-reports");
                if (Directory.Exists(crashDir))
                    report = Directory.GetFiles(crashDir, "*.txt").OrderByDescending(File.GetLastWriteTime)
                        .FirstOrDefault(f => (DateTime.Now - File.GetLastWriteTime(f)).TotalMinutes < 10);
                report ??= Path.Combine(packDir, "logs", "latest.log");
            }
            catch { }

            if (report != null && File.Exists(report) &&
                FWDialog.Confirm($"Oyun hata koduyla kapandı (kod {exitCode}).\nNedenini görmek için {(report.Contains("crash-reports") ? "çökme raporunu" : "son günlüğü")} açmak ister misin?\n\nDestek talebi açarken bu dosyayı ekleyebilirsin.",
                                 "Oyun çöktü", "Aç", "Kapat"))
            {
                try { Process.Start(new ProcessStartInfo(report) { UseShellExecute = true }); }
                catch (Exception ex) { Logger.Warn($"Günlük açılamadı: {ex.Message}"); }
            }
        }

        private void UpdateModsBadge()
        {
            try
            {
                var dir = Path.Combine(CurrentPackDir, "mods");
                int main = Directory.Exists(dir)
                    ? Directory.GetFiles(dir, "*.jar").Count(f => !Path.GetFileName(f).StartsWith(UserContentService.MergePrefix, StringComparison.OrdinalIgnoreCase))
                    : 0;
                int user = UserContentService.LoadIndex(CurrentPackDir).Count(x => x.Kind == ContentKind.Mod && x.Enabled);
                TxtModsBadge.Text = main + user > 0 ? $"Mod: {main + user} açık" : "Mod: kurulmadı";
                TxtModsBadge.ToolTip = $"Ana paket: {main} · Senin modların: {user}";
            }
            catch { TxtModsBadge.Text = "Mod: —"; }
        }

        private void UpdateBadges()
        {
            if (currentPack == null)
                return;

            if (currentPack.IsHorizon)
            {
                TxtLoaderBadge.Text = "Horizon · Rust";
                TxtVersionBadge.Text = "26.3";
                BadgesPanel.Visibility = Visibility.Visible;
                PackContext.SetPack(currentPack, CurrentPackDir, "26.3", "Horizon");
                UpdateModsBadge();
                return;
            }

            string loaderText = currentPack.Loader ?? "";
            string versionText = currentPack.McVersion ?? "";

            // Kuruluysa manifestten kesin loader/sürüm bilgisini al.
            try
            {
                var manifestPath = Path.Combine(CurrentPackDir, "manifest.json");
                if (File.Exists(manifestPath))
                {
                    var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath));
                    if (manifest?.Minecraft != null)
                    {
                        if (string.IsNullOrWhiteSpace(versionText))
                            versionText = manifest.Minecraft.Version;
                        if (string.IsNullOrWhiteSpace(loaderText))
                        {
                            var loader = manifest.Minecraft.ModLoaders.FirstOrDefault(m => m.Primary)
                                         ?? manifest.Minecraft.ModLoaders.FirstOrDefault();
                            loaderText = ModLoaderService.DisplayName(ModLoaderService.Detect(loader?.Id));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Rozet bilgisi okunamadı: {ex.Message}");
            }

            bool hasLoader = !string.IsNullOrWhiteSpace(loaderText);
            bool hasVersion = !string.IsNullOrWhiteSpace(versionText);

            if (hasLoader) TxtLoaderBadge.Text = loaderText;
            if (hasVersion) TxtVersionBadge.Text = versionText;
            BadgesPanel.Visibility = (hasLoader || hasVersion) ? Visibility.Visible : Visibility.Collapsed;

            PackContext.SetPack(currentPack, CurrentPackDir, versionText, loaderText);
            UpdateModsBadge();
        }

        private async Task CheckServerStatusAsync()
        {
            if (currentPack == null || string.IsNullOrWhiteSpace(currentPack.ServerIp))
            {
                // Sunucu tanımlı değilse oyna kilidi uygulanmaz.
                SetServerStatus(false, "Sunucu yapılandırılmamış", "");
                _serverOnline = true;
                ApplyPlayGate();
                return;
            }

            string host = currentPack.ServerIp.Trim();
            int port = currentPack.ServerPort > 0 ? currentPack.ServerPort : 25565;
            if (host.Contains(':'))
            {
                var parts = host.Split(':');
                host = parts[0];
                if (int.TryParse(parts[1], out int p)) port = p;
            }

            string ipText = $"{host}:{port}";

            // İlk kontrolde "kontrol ediliyor" durumunu göster (daha önce sonuç yoksa).
            if (_serverOnline == null)
            {
                SetServerStatus(null, "Sunucu durumu kontrol ediliyor...", ipText);
                ApplyPlayGate();
            }

            var status = await MinecraftServerPinger.PingAsync(host, port);

            if (status.Online)
            {
                string text = status.MaxPlayers > 0
                    ? $"Çevrimiçi · {status.OnlinePlayers}/{status.MaxPlayers} oyuncu"
                    : "Sunucu çevrimiçi";
                SetServerStatus(true, text, ipText);
                _serverOnline = true;
                PackContext.SetServer(true, status.OnlinePlayers, status.MaxPlayers);
            }
            else
            {
                SetServerStatus(false, "Sunucu çevrimdışı", ipText);
                _serverOnline = false;
                PackContext.SetServer(false, 0, 0);
            }

            ApplyPlayGate();
        }

        private void SetServerStatus(bool? online, string text, string ip)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetServerStatus(online, text, ip));
                return;
            }

            TxtServerStatus.Text = text;
            TxtServerIp.Text = ip;
            ServerDot.Fill = online switch
            {
                true => (Brush)FindResource("OnlineBrush"),
                false => (Brush)FindResource("TextMutedBrush"),
                _ => (Brush)FindResource("WarningBrush")
            };
            KartDurumAynala();
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Logger.Warn($"Geçici dosya silinemedi ({path}): {ex.Message}"); }
        }

        private void SetButtonsEnabled(bool enable)
        {
            BtnUpdate.IsEnabled = enable;
            if (enable)
            {
                BtnPlay.IsEnabled = true;
                ApplyPlayGate(); // Play, sunucu durumuna göre tekrar kilitlenebilir
            }
            else
            {
                BtnPlay.IsEnabled = false;
            }
        }

        private void UpdateStatus(string text, int progress)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => UpdateStatus(text, progress));
                return;
            }

            LblStatus.Text = text;
            ProgressBar.Value = progress;
            KartDurumAynala();
        }

        private void SetStatusText(string text)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetStatusText(text));
                return;
            }

            LblStatus.Text = text;
            KartDurumAynala();
        }

        private void SetProgress(double value)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetProgress(value));
                return;
            }

            ProgressBar.Value = Math.Clamp(value, 0, 100);
        }
    }
}
