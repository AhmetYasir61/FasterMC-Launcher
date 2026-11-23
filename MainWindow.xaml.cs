using FWLauncherV2.Dialogs;
using FWLauncherV2.Models;
using FWLauncherV2.Services;
using FWLauncherV2.Views;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FWLauncherV2
{
    public partial class MainWindow : Window
    {
        private AccountView? _account;
        private ModsView? _mods;
        private StoreView? _store;
        private ReklamlarView? _reklam;
        private MapView? _map;
        private DestekView? _destek;
        private SettingsView? _settings;
        private ControlPanelView? _controlPanel;
        private string currentUsername = "Oyuncu";

        private readonly DispatcherTimer _modTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
        private double _warnRemainingMs;
        private bool _banned;
        private string _modReason = "";
        private int _modPollCounter;

        private AppSettings _links = new();
        private const string DefaultDiscordUrl = "https://discord.gg/RrBTdgrUM2";

        // Sayfa renkleri (Aurora & Mercan): her sayfa kendi gradyanını taşır.
        private static readonly Dictionary<string, (Color a, Color b)> PageColors = new()
        {
            ["play"] = (C("#FF8D3A"), C("#FFAA3B")),
            ["account"] = (C("#B89914"), C("#EACF5E")),
            ["mods"] = (C("#F69B3C"), C("#FFD74B")),
            ["store"] = (C("#EC8C28"), C("#F9DA6D")),
            ["news"] = (C("#F59E0B"), C("#FCD06A")),
            ["map"] = (C("#C5A622"), C("#E6C435")),
            ["support"] = (C("#F68A19"), C("#EEC722")),
            ["settings"] = (C("#F8D762"), C("#F9DD67")),
            ["panel"] = (C("#EF8C44"), C("#FB923C")),
            ["login"] = (C("#FF9F3A"), C("#FF9F3A")),
        };

        private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

        public MainWindow()
        {
            InitializeComponent();
            // F11 / Alt+Enter / Win+Yukari: tam ekran kisayollari islemesin
            PreviewKeyDown += (_, e) =>
            {
                var k = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
                bool alt = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Alt) != 0;
                bool win = System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LWin) || System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RWin);
                if (k == System.Windows.Input.Key.F11 || (alt && k == System.Windows.Input.Key.Enter) || (win && k == System.Windows.Input.Key.Up))
                    e.Handled = true;
            };
            UserSession.Changed += OnSessionChanged;
            PackContext.ServerChanged += OnServerChanged;
            Services.Guncelleyici.Olay += GuncellemeOlayi;
            Services.Guncelleyici.Baslat();
            Closed += (s, e) =>
            {
                UserSession.Changed -= OnSessionChanged;
                PackContext.ServerChanged -= OnServerChanged;
                _modTimer.Stop();
                _tray?.Dispose();
                _tray = null;
            };
            _tray = new TrayIcon(RestoreFromTray, () => MainView.KillGameAndClearAuth(), () => Application.Current.Shutdown());

            _modTimer.Tick += ModTimer_Tick;
            _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
            Loaded += async (s, e) =>
            {
                // Pencere açılışı: yumuşak belirme + hafif büyüme
                var sc = new ScaleTransform(0.97, 0.97);
                RootBorder.RenderTransformOrigin = new Point(0.5, 0.5);
                RootBorder.RenderTransform = sc;
                RootBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = Ease });
                sc.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = Ease });
                sc.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = Ease });
                UpdateClock();
                RefreshAvatar();
                await CheckModeration();
                _modTimer.Start();
                _links = await ShopService.GetSettingsAsync();
            };
        }

        // ===================== ÜST ÇUBUK =====================
        private void UpdateClock() => TxtClock.Text = DateTime.Now.ToString("HH:mm");

        private void OnServerChanged() => Dispatcher.BeginInvoke(() =>
        {
            switch (PackContext.ServerOnline)
            {
                case true:
                    TxtPlayers.Text = PackContext.OnlinePlayers.ToString();
                    TxtMaxPlayers.Text = PackContext.MaxPlayers > 0 ? $" / {PackContext.MaxPlayers}" : "";
                    PlayersBar.Value = PackContext.MaxPlayers > 0 ? 100.0 * PackContext.OnlinePlayers / PackContext.MaxPlayers : 0;
                    TxtPlayersCaption.Text = "oyuncu çevrimiçi";
                    break;
                case false:
                    TxtPlayers.Text = "0";
                    TxtMaxPlayers.Text = "";
                    PlayersBar.Value = 0;
                    TxtPlayersCaption.Text = "sunucu kapalı";
                    break;
                default:
                    TxtPlayers.Text = "—";
                    TxtPlayersCaption.Text = "sunucu kontrol ediliyor";
                    break;
            }
        });

        private void RefreshAvatar() => Dispatcher.BeginInvoke(() =>
            PlayerHead.Apply(ImgAvatarHead, TxtUserInitial, currentUsername));

        private void Discord_Click(object sender, RoutedEventArgs e)
            => Links.Open(string.IsNullOrWhiteSpace(_links.DiscordUrl) ? DefaultDiscordUrl : _links.DiscordUrl, "Discord bağlantısı");
        private void Website_Click(object sender, RoutedEventArgs e) => Links.Open(_links.WebsiteUrl, "Web sitesi adresi");
        private void Youtube_Click(object sender, RoutedEventArgs e) => Links.Open(_links.YoutubeUrl, "YouTube kanalı");
        private void Logo_Click(object sender, MouseButtonEventArgs e) => NavigateToMainView(currentUsername);
        private void UserChip_Click(object sender, MouseButtonEventArgs e) => NavigateToAccount();

        /// <summary>Sağ alttan kısa bilgi bildirimi.</summary>
        // ===================== GUNCELLEME BALONCUGU =====================
        private Services.Guncelleyici.Tur _balonTur;

        private void GuncellemeOlayi(Services.Guncelleyici.Tur tur, string metin) => Dispatcher.Invoke(() =>
        {
            if (GuncelleBalon.Visibility == Visibility.Visible && _balonTur == tur) return;
            _balonTur = tur;
            bool launcher = tur == Services.Guncelleyici.Tur.Launcher;
            TxtBalon.Text = metin;
            TxtBalonAlt.Text = launcher ? "Tek tıkla güncelle, launcher yeniden açılır" : "Launcher'ı kapatmadan yenile";
            TxtBalonTus.Text = launcher ? "GÜNCELLE" : "YENİLE";
            TxtBalonIkon.Text = launcher ? "⬇" : "⟳";
            GuncelleBalon.Visibility = Visibility.Visible;
            // sag alttan kayarak + hafif ziplayarak gelir
            BalonKay.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(260, 0, TimeSpan.FromMilliseconds(520)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 } });
            GuncelleBalon.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
        });

        private async void GuncelleBalon_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_balonTur == Services.Guncelleyici.Tur.Launcher)
            {
                TxtBalonAlt.Text = "İndiriliyor...";
                try
                {
                    var ilerleme = new Progress<double>(o => TxtBalonAlt.Text = $"İndiriliyor... %{o * 100:0}");
                    if (await Services.Guncelleyici.LauncherGuncelleAsync(ilerleme)) Application.Current.Shutdown();
                }
                catch (Exception ex) { TxtBalonAlt.Text = "Güncellenemedi: " + ex.Message; }
                return;
            }
            // modpaketi: launcher'i kapatmadan paket listesini yenile
            TxtBalonAlt.Text = "Yenileniyor...";
            await PlayCard.YenidenYukleAsync();
            Services.Guncelleyici.PaketYenilendi();
            GuncelleBalon.Visibility = Visibility.Collapsed;
            ShowToast("Modpaketi bilgileri yenilendi. Güncellemek için kartındaki GÜNCELLE'ye bas.");
        }

        public void ShowToast(string text)
        {
            TxtToast.Text = text;
            Toast.Visibility = Visibility.Visible;
            AnimateIn(Toast, 18, 260);
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        // ===================== ANİMASYONLAR =====================
        private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        /// <summary>Öğeyi aşağıdan yukarı kayarak ve belirerek getirir (sayfa geçişleri, bildirim).</summary>
        private static void AnimateIn(FrameworkElement el, double fromY = 14, int ms = 240)
        {
            var tt = el.RenderTransform as TranslateTransform;
            if (tt == null || tt.IsFrozen) { tt = new TranslateTransform(); el.RenderTransform = tt; }
            var d = TimeSpan.FromMilliseconds(ms);
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = Ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, d) { EasingFunction = Ease });
        }

        public AppSettings LinkSettings => _links;

        // ===================== SİSTEM TEPSİSİ =====================
        private TrayIcon? _tray;

        /// <summary>Oyun açılınca: pencereyi gizle (görev çubuğundan da), tepsi simgesinden ulaşılabilir kalsın.</summary>
        public void HideToTray()
        {
            Hide();
            _tray?.NotifyHidden();
        }

        /// <summary>Tepsiden veya oyun kapanınca: gizlilikten çıkar ve öne getir.</summary>
        public void RestoreFromTray()
        {
            if (!IsVisible) Show();
            WindowState = WindowState.Normal;
            Topmost = true;   // Windows odak kuralını aşıp gerçekten öne getir
            Activate();
            Topmost = false;
            Focus();
        }

#if DEBUG
        /// <summary>Önizleme için sayfa aç (App --onizleme --sayfa X).</summary>
        public void DebugOpen(string page)
        {
            switch (page)
            {
                case "hesap": NavigateToAccount(); break;
                case "modlar": NavigateToMods(); break;
                case "magaza": NavigateToStore(); break;
                case "haberler": NavigateToReklamlar(); break;
                case "harita": NavigateToMap(); break;
                case "destek": NavigateToDestek(); break;
                case "ayarlar": NavigateToSettings(); break;
                case "panel": _controlPanel ??= new ControlPanelView(); ShowCenter(_controlPanel, "panel", NavAdmin); break;
                case "tepsi":   // gizle → 4 sn sonra geri getir (tepsi testi)
                    HideToTray();
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                    t.Tick += (s, e) => { t.Stop(); RestoreFromTray(); };
                    t.Start();
                    break;
            }
        }
#endif

        // ===================== MODERASYON (uyarı / ban) =====================
        private async Task CheckModeration()
        {
            var st = await ShopService.ModerationStatusAsync();
            if (!st.Success) return;
            _banned = st.Banned;
            _warnRemainingMs = st.RemainingMs;
            _modReason = st.Reason ?? "";
            ApplyModeration();
        }

        private void ModTimer_Tick(object? sender, EventArgs e)
        {
            UpdateClock();
            if (_warnRemainingMs > 0)
            {
                _warnRemainingMs = Math.Max(0, _warnRemainingMs - 1000);
                ApplyModeration();
            }
            if (++_modPollCounter >= 20) { _modPollCounter = 0; _ = CheckModeration(); }
        }

        private void ApplyModeration()
        {
            if (_banned)
            {
                TxtModIcon.Text = "⛔";
                TxtModTitle.Text = "Hesabın yasaklandı";
                TxtModMsg.Text = string.IsNullOrWhiteSpace(_modReason)
                    ? "Yasağın kalkana dek launcher'da hiçbir şeye erişemezsin. Bir yetkiliyle iletişime geç."
                    : _modReason;
                TxtModTimer.Text = "";
                ModOverlay.Visibility = Visibility.Visible;
            }
            else if (_warnRemainingMs > 0)
            {
                TxtModIcon.Text = "⚠";
                TxtModTitle.Text = "Uyarıldınız";
                TxtModMsg.Text = string.IsNullOrWhiteSpace(_modReason)
                    ? "Launcher geçici olarak kilitlendi. Süre dolunca açılacak."
                    : _modReason;
                TxtModTimer.Text = "Kalan süre: " + FormatRemaining(_warnRemainingMs);
                ModOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                ModOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private static string FormatRemaining(double ms)
        {
            var ts = TimeSpan.FromMilliseconds(ms);
            if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}g {ts.Hours}sa {ts.Minutes}dk";
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}sa {ts.Minutes}dk {ts.Seconds}sn";
            if (ts.TotalMinutes >= 1) return $"{ts.Minutes}dk {ts.Seconds}sn";
            return $"{ts.Seconds}sn";
        }

        private void OnSessionChanged() => Dispatcher.Invoke(UpdateSessionUi);

        private void UpdateSessionUi()
        {
            RunRank.Text = UserSession.RankText;
            NavAdmin.Visibility = UserSession.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        }

        // ===================== SAYFA GEÇİŞLERİ =====================
        public void NavigateToMainView(string username)
        {
            if (!EnsureCanLeaveCenter())
                return;

            if (string.IsNullOrWhiteSpace(username))
                username = SettingsService.Load().LastUsername;
            if (string.IsNullOrWhiteSpace(username))
                username = "Oyuncu";

            bool firstTime = currentUsername != username || !_sessionLoaded;
            currentUsername = username;
            UpdateUserChip(username);
            PlayCard.SetCurrentUser(username);

            if (firstTime)
            {
                _sessionLoaded = true;
                LocalStatsService.MarkLogin(username);
                // Sunucudan rol + coin bilgisini çek (Changed olayı UI'yi günceller).
                _ = UserSession.LoadAsync();
            }

            CenterContent.Content = null;
            CenterContent.Visibility = Visibility.Collapsed;
            bool wasHidden = PlayCard.Visibility != Visibility.Visible;
            PlayCard.Visibility = Visibility.Visible;
            if (wasHidden) AnimateIn(PlayCard);
            Activate("play", NavHome);
        }

        private bool _sessionLoaded;

        public void NavigateToAccount()
        {
            if (!EnsureCanLeaveCenter()) return;
            _account ??= new AccountView();
            _account.SetUser(currentUsername);
            ShowCenter(_account, "account", NavAccount);
        }

        public void NavigateToMods(int tab = 0)
        {
            if (!EnsureCanLeaveCenter()) return;
            _mods ??= new ModsView();
            ShowCenter(_mods, "mods", NavMods);
            _mods.SelectTab(tab);
        }

        public void NavigateToStore(ContentKind kind = ContentKind.Mod)
        {
            if (!EnsureCanLeaveCenter()) return;
            _store ??= new StoreView();
            ShowCenter(_store, "store", NavStore);
            _store.SetKind(kind);
        }

        public void NavigateToControlPanel()
        {
            if (!UserSession.IsAdmin) return;
            if (!EnsureCanLeaveCenter()) return;
            _controlPanel ??= new ControlPanelView();
            ShowCenter(_controlPanel, "panel", NavAdmin);
        }

        public void NavigateToReklamlar()
        {
            if (!EnsureCanLeaveCenter()) return;
            _reklam ??= new ReklamlarView();
            ShowCenter(_reklam, "news", NavReklam);
        }

        public void NavigateToMap()
        {
            if (!EnsureCanLeaveCenter()) return;
            _map ??= new MapView();
            ShowCenter(_map, "map", NavMap);
        }

        public void NavigateToDestek()
        {
            if (!EnsureCanLeaveCenter()) return;
            _destek ??= new DestekView();
            ShowCenter(_destek, "support", NavDestek);
        }

        public void NavigateToSettings()
        {
            if (CenterContent.Content is SettingsView && CenterContent.Visibility == Visibility.Visible)
                return;

            _settings ??= new SettingsView(currentUsername);
            _settings.Reload();
            ShowCenter(_settings, "settings", NavSettings);
        }

        private bool EnsureCanLeaveCenter()
            => CenterContent.Visibility != Visibility.Visible || CenterContent.Content is not SettingsView sv || sv.CanLeave();

        private void ShowCenter(UserControl view, string page, Button activeNav)
        {
            bool changed = !ReferenceEquals(CenterContent.Content, view) || CenterContent.Visibility != Visibility.Visible;
            CenterContent.Content = view;
            CenterContent.Visibility = Visibility.Visible;
            PlayCard.Visibility = Visibility.Collapsed;
            if (changed) AnimateIn(CenterContent);
            Activate(page, activeNav);
        }

        private void Activate(string page, Button activeNav)
        {
            ApplyPageTheme(page);
            foreach (var b in new[] { NavHome, NavAccount, NavMods, NavStore, NavReklam, NavMap, NavDestek, NavSettings, NavAdmin })
                b.Tag = ReferenceEquals(b, activeNav) ? "active" : null;
        }

        /// <summary>Sayfanın rengini Page* kaynaklarına yazar (DynamicResource ile tüm stiller güncellenir).</summary>
        public static void ApplyPageTheme(string page)
        {
            if (page != "login" && !SettingsService.Load().PageColors) page = "play";
            if (!PageColors.TryGetValue(page, out var pc)) pc = PageColors["play"];
            var r = Application.Current.Resources;
            r["PageAccentColor"] = pc.a;
            r["PageAccent2Color"] = pc.b;
            r["PageAccentBrush"] = Frozen(new SolidColorBrush(pc.a));
            r["PageSoftBrush"] = Frozen(new SolidColorBrush(Color.FromArgb(0x30, pc.a.R, pc.a.G, pc.a.B)));
            r["PageStrokeBrush"] = Frozen(new SolidColorBrush(Color.FromArgb(0x70, pc.a.R, pc.a.G, pc.a.B)));
            r["PageGradient"] = Frozen(new LinearGradientBrush(pc.a, pc.b, 0));
            r["PageGradientD"] = Frozen(new LinearGradientBrush(pc.a, pc.b, 45));
            var glow = new RadialGradientBrush
            {
                Center = new Point(0.85, 0.05), GradientOrigin = new Point(0.85, 0.05), RadiusX = 0.9, RadiusY = 0.9
            };
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x38, pc.a.R, pc.a.G, pc.a.B), 0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, pc.a.R, pc.a.G, pc.a.B), 1));
            r["PageGlowBrush"] = Frozen(glow);
        }

        private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

        private void UpdateUserChip(string username)
        {
            TxtSidebarUser.Text = username;
            TxtUserInitial.Text = string.IsNullOrWhiteSpace(username)
                ? "?"
                : char.ToUpperInvariant(username[0]).ToString();
            RefreshAvatar();   // avatar kutusuna oyuncu kafası
        }

        // ===================== MENÜ TIKLAMALARI =====================
        private void NavHome_Click(object sender, RoutedEventArgs e) => NavigateToMainView(currentUsername);
        private void NavAccount_Click(object sender, RoutedEventArgs e) => NavigateToAccount();
        private void NavMods_Click(object sender, RoutedEventArgs e) => NavigateToMods();
        private void NavStore_Click(object sender, RoutedEventArgs e) => NavigateToStore();
        private void NavReklam_Click(object sender, RoutedEventArgs e) => NavigateToReklamlar();
        private void NavMap_Click(object sender, RoutedEventArgs e) => NavigateToMap();
        private void NavDestek_Click(object sender, RoutedEventArgs e) => NavigateToDestek();
        private void NavSettings_Click(object sender, RoutedEventArgs e) => NavigateToSettings();
        private void NavAdmin_Click(object sender, RoutedEventArgs e) => NavigateToControlPanel();

        // ===================== PENCERE KONTROLLERİ =====================
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Düğmeler, logo ve kullanıcı alanı kendi tıklamasını alsın (sürükleme yutmasın).
            if (e.OriginalSource is DependencyObject d && (FindParent<Button>(d) != null || HasHandCursor(d))) return;
            if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }
            if (e.LeftButton == MouseButtonState.Pressed && WindowState != WindowState.Maximized)
            {
                try { DragMove(); } catch { /* nadir durumlarda yok say */ }
            }
        }

        private static bool HasHandCursor(DependencyObject? d)
        {
            while (d != null)
            {
                if (d is FrameworkElement fe && fe.Cursor == Cursors.Hand) return true;
                d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T t) return t;
                d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
            => Application.Current.Shutdown();

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
            => this.WindowState = WindowState.Minimized;

        private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

        // FasterMC: tam ekran / geri al yok — pencere hep normal boyutta.
        private void ToggleMaximize() { }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            // Hangi yoldan gelirse gelsin (Win+Yukari, ekran ustune surukleme, kisayol) buyutmeyi geri al.
            if (WindowState == WindowState.Maximized)
            {
                Dispatcher.BeginInvoke(new Action(() => WindowState = WindowState.Normal));
                return;
            }
            if (WindowState == WindowState.Maximized)
            {
                RootBorder.Margin = new Thickness(0);
                RootBorder.CornerRadius = new CornerRadius(0);
                InnerBorder.CornerRadius = new CornerRadius(0);
                BtnMaximize.Content = "❐"; // geri al
            }
            else
            {
                RootBorder.Margin = new Thickness(16);
                RootBorder.CornerRadius = new CornerRadius(0);
                InnerBorder.CornerRadius = new CornerRadius(0);
                BtnMaximize.Content = "□"; // büyüt
            }
        }

        // ===== Borderless pencere büyütülünce görev çubuğunu kapatmasın =====
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0024) // WM_GETMINMAXINFO
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                IntPtr monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
                if (monitor != IntPtr.Zero)
                {
                    var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    GetMonitorInfo(monitor, ref info);
                    mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
                    mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
                    mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
                    mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
                    Marshal.StructureToPtr(mmi, lParam, true);
                }
                handled = true;
            }
            return IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECTW { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECTW rcMonitor; public RECTW rcWork; public int dwFlags; }

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        private void BtnLogout_Click(object sender, RoutedEventArgs e)
        {
            if (MainView.RunningGame != null &&
                !FWDialog.Confirm("Oyun açık. Çıkış yaparsan oyun da kapanacak. Devam edilsin mi?", "Çıkış", "Çıkış yap", "Vazgeç"))
                return;

            // Tek hesap kuralı: çıkışta açık oyun süreci kapatılır + auth dosyası silinir
            // (mod da dosyayı izler — silinince oyun kendini kapatır, çift güvence).
            Views.MainView.KillGameAndClearAuth();
            SessionManager.ClearSession();
            new LoginWindow().Show();
            this.Close();
        }
    }
}
