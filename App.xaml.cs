using System;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using FWLauncherV2.Dialogs;
using FWLauncherV2.Services;

namespace FWLauncherV2
{
    public partial class App : Application
    {
        protected override void OnExit(ExitEventArgs e)
        {
            // Launcher KAPANIRSA açık oyun da kapanır (tek hesap kuralı; logout'la aynı yol).
            // Mod tarafı ayrıca launcher PID'ini izler — launcher çökse bile oyun kendini kapatır.
            try { Views.MainView.KillGameAndClearAuth(); } catch { }
            base.OnExit(e);
        }

        private static System.Threading.Mutex? _tek;
        private static System.Threading.EventWaitHandle? _gosterSinyali;

        /// <summary>
        /// Tek kopya: launcher zaten aciksa yenisi acilmaz, acik olan one getirilir.
        /// Kurulum: exe %AppData%\FasterMCLauncher disindan (ornegin Indirilenler) acildiysa kendini oraya kopyalar,
        /// masaustu kisayolu olusturur, kurulu kopyayi baslatir ve indirilen dosyayi siler.
        /// </summary>
        const string KayitAnahtari = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FasterMCLauncher";
        static string Klasor => Path.GetDirectoryName(Guncelleyici.KurulumExe)!;
        static string BaslatKisayol => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "FasterMC Launcher.lnk");
        static string MasaustuKisayol => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "FasterMC Launcher.lnk");

        /// <summary>Windows "Yuklu uygulamalar" / Denetim Masasi "Program kaldir veya degistir" kaydi (kullanici kapsami).</summary>
        static void UygulamaKaydi()
        {
            try
            {
                string exe = Guncelleyici.KurulumExe;
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KayitAnahtari);
                k.SetValue("DisplayName", "FasterMC Launcher");
                k.SetValue("DisplayVersion", Guncelleyici.Surum.ToString(3));
                k.SetValue("Publisher", "FireWing / FasterMC");
                k.SetValue("DisplayIcon", exe + ",0");
                k.SetValue("InstallLocation", Klasor);
                k.SetValue("URLInfoAbout", "https://firewing.store");
                k.SetValue("UninstallString", "\"" + exe + "\" --kaldir");
                k.SetValue("QuietUninstallString", "\"" + exe + "\" --kaldir --sessiz");
                k.SetValue("ModifyPath", "\"" + exe + "\" --onar");
                k.SetValue("NoModify", 0, Microsoft.Win32.RegistryValueKind.DWord);
                k.SetValue("NoRepair", 0, Microsoft.Win32.RegistryValueKind.DWord);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                try { k.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), Microsoft.Win32.RegistryValueKind.DWord); } catch { }
                if (!File.Exists(BaslatKisayol)) KisayolOlustur(exe, BaslatKisayol);
                if (!File.Exists(Path.Combine(Klasor, "FasterMC Launcher Kaldir.cmd"))) KaldirmaBetigi();
            }
            catch (Exception ex) { Logger.Warn("Uygulama kaydi yazilamadi: " + ex.Message); }
        }

        /// <summary>Kaldir: oyun + launcher kapanir; %AppData%\FasterMCLauncher (paketler dahil), kisayollar ve kayit silinir.</summary>
        static void Kaldir(bool sessiz)
        {
            if (!sessiz)
            {
                var c = MessageBox.Show("FasterMC Launcher ve kurulu tüm modpaketleri (oyun dosyaları, ayarlar) bu bilgisayardan silinecek.\n\nDevam edilsin mi?",
                    "FasterMC Launcher — Kaldır", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (c != MessageBoxResult.Yes) return;
            }
            try { Views.MainView.KillGameAndClearAuth(); } catch { }
            // acik baska launcher kopyasi klasoru kilitlemesin
            foreach (var pr in System.Diagnostics.Process.GetProcessesByName("FasterMCLauncher"))
                if (pr.Id != Environment.ProcessId) try { pr.Kill(true); pr.WaitForExit(5000); } catch { }
            foreach (var l in new[] { MasaustuKisayol, BaslatKisayol, IndirilenKisayol }) try { File.Delete(l); } catch { }
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(KayitAnahtari, false); } catch { }
            int pid = Environment.ProcessId;
            string bat = Path.Combine(Path.GetTempPath(), "fastermc_kaldir.cmd");
            File.WriteAllText(bat,
                "@echo off\r\n" +
                ":bekle\r\ntasklist /FI \"PID eq " + pid + "\" | find \"" + pid + "\" >nul && (timeout /t 1 /nobreak >nul & goto bekle)\r\n" +
                "rmdir /s /q \"" + Klasor + "\"\r\n" +
                "del \"%~f0\"\r\n");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"") { CreateNoWindow = true, UseShellExecute = false });
            if (!sessiz) MessageBox.Show("FasterMC Launcher kaldırıldı.", "FasterMC Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>Kaldir / Onar (Denetim Masasi / Yuklu uygulamalar ya da indirilenlerdeki betik).</summary>
        async System.Threading.Tasks.Task<bool> KaldirVeyaOnar(string[] args)
        {
            bool sessiz = args.Contains("--sessiz");
            if (args.Contains("--kaldir")) { Kaldir(sessiz); return true; }
            if (!args.Contains("--onar")) return false;
            var c = MessageBox.Show("EVET: Onar — güncel launcher yeniden indirilip kurulur (oyun dosyaların korunur).\nHAYIR: Kaldır — launcher ve tüm dosyaları silinir.",
                "FasterMC Launcher — Kaldır veya Onar", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (c == MessageBoxResult.No) Kaldir(true);
            else if (c == MessageBoxResult.Yes)
            {
                try { await Guncelleyici.OnarAsync(); }
                catch (Exception ex) { MessageBox.Show("Onarılamadı: " + ex.Message, "FasterMC Launcher", MessageBoxButton.OK, MessageBoxImage.Error); }
            }
            return true;
        }

        static string IndirilenKisayol => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "FasterMC Launcher.lnk");

        /// <summary>Kaldirma betigi: YALNIZ kurulum klasorunde (Denetim Masasi kaydi da ayni isi yapar).</summary>
        static void KaldirmaBetigi()
        {
            try
            {
                string f = Path.Combine(Klasor, "FasterMC Launcher Kaldir.cmd");
                string exe = Guncelleyici.KurulumExe;
                File.WriteAllText(f, "@echo off\r\nrem FasterMC Launcher'i ve tum dosyalarini kaldirir.\r\n" +
                    "if exist \"" + exe + "\" (start \"\" \"" + exe + "\" --kaldir) else (echo Launcher zaten kaldirilmis.& pause)\r\n");
            }
            catch { }
        }

        private bool TekKopyaVeKurulum(string[] args)
        {
            string exe = Environment.ProcessPath ?? "";
            string kur = Guncelleyici.KurulumExe;
            bool kurulu = string.Equals(Path.GetFullPath(exe), Path.GetFullPath(kur), StringComparison.OrdinalIgnoreCase);
            int ki = Array.IndexOf(args, "--kuruldu");

            _tek = new System.Threading.Mutex(true, "FasterMCLauncher_Tek", out bool ilk);
            if (!ilk)
            {
                bool aldi = false;
                if (ki >= 0)   // kurulumdan hemen sonra: eski (indirilen) kopyanin kapanmasini bekle
                {
                    try { aldi = _tek.WaitOne(10000); } catch (System.Threading.AbandonedMutexException) { aldi = true; }
                }
                if (!aldi)
                {
                    try { System.Threading.EventWaitHandle.OpenExisting("FasterMCLauncher_Goster").Set(); } catch { }
                    return false;   // zaten acik: one getirildi, bu kopya kapanir
                }
            }

#if !DEBUG
            if (!kurulu && !string.IsNullOrEmpty(exe))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(kur)!);
                    File.Copy(exe, kur, true);
                    KisayolOlustur(kur, MasaustuKisayol);
                    KisayolOlustur(kur, BaslatKisayol);
                    KisayolOlustur(kur, IndirilenKisayol);   // indirilen exe silinir, yerinde baslatma kisayolu kalir
                    KaldirmaBetigi();
                    _tek.ReleaseMutex(); _tek.Dispose(); _tek = null;
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(kur) { UseShellExecute = true, Arguments = $"--kuruldu \"{exe}\"" });
                    return false;
                }
                catch (Exception ex) { Logger.Warn("Kurulum yapilamadi, yerinde calisiyor: " + ex.Message); }
            }
#endif
            if (ki >= 0 && ki + 1 < args.Length) IndirileniSil(args[ki + 1]);
            if (kurulu) UygulamaKaydi();

            // ikinci tiklama sinyali: pencereyi one getir
            _gosterSinyali = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, "FasterMCLauncher_Goster");
            var t = new System.Threading.Thread(() =>
            {
                while (_gosterSinyali.WaitOne())
                    Dispatcher.Invoke(() =>
                    {
                        var w = Windows.OfType<Window>().FirstOrDefault(x => x is MainWindow || x is LoginWindow) ?? MainWindow;
                        if (w == null) return;
                        w.Show();
                        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                        w.Activate(); w.Topmost = true; w.Topmost = false; w.Focus();
                    });
            }) { IsBackground = true, Name = "fmc-goster" };
            t.Start();
            return true;
        }

        private static void KisayolOlustur(string hedef, string lnk)
        {
            try
            {
                var tur = Type.GetTypeFromProgID("WScript.Shell");
                if (tur == null) return;
                dynamic kabuk = Activator.CreateInstance(tur)!;
                dynamic k = kabuk.CreateShortcut(lnk);
                k.TargetPath = hedef;
                k.WorkingDirectory = Path.GetDirectoryName(hedef);
                k.IconLocation = hedef + ",0";
                k.Description = "FasterMC Launcher";
                k.Save();
            }
            catch (Exception ex) { Logger.Warn("Kisayol olusturulamadi: " + ex.Message); }
        }

        /// <summary>Yalnizca Indirilenler klasorundeki indirilmis launcher silinir (baska yerdeki dosyaya dokunulmaz).</summary>
        private static void IndirileniSil(string yol)
        {
            try
            {
                string indirilen = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (!File.Exists(yol) || !Path.GetFullPath(yol).StartsWith(Path.GetFullPath(indirilen), StringComparison.OrdinalIgnoreCase)) return;
                if (!Path.GetExtension(yol).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                    $"/c timeout /t 3 /nobreak >nul & del /f /q \"{yol}\"") { CreateNoWindow = true, UseShellExecute = false });
            }
            catch { }
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (await KaldirVeyaOnar(e.Args)) { Shutdown(); return; }
            if (!TekKopyaVeKurulum(e.Args)) { Shutdown(); return; }

            // Global hata yakalama: çökme yerine günlüğe yaz ve kullanıcıya bilgi ver.
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            Logger.Info("Launcher başlatılıyor.");

#if DEBUG
            // Geliştirme önizlemesi: girişi atlayıp arayüzü açar (yalnız Debug derlemesinde vardır).
            if (e.Args.Contains("--onizleme"))
            {
                var pv = new MainWindow();
                pv.NavigateToMainView("Önizleme");
                pv.Show();
                var page = e.Args.SkipWhile(a => a != "--sayfa").Skip(1).FirstOrDefault();
                if (page != null) pv.DebugOpen(page);
                return;
            }
#endif

            // 🖥️ Donanım kapısı: en az 6 GB sistem RAM'i şart — altındaki makinede launcher hiç açılmaz.
            double totalGb = GetTotalRamGb();
            if (totalGb > 0 && totalGb < 5.5)   // 6 GB modüller ~5.9 GB raporlanır; 5.5 eşiği güvenli
            {
                Logger.Warn($"Yetersiz RAM: {totalGb:0.0} GB — launcher kapatılıyor.");
                MessageBox.Show(
                    $"Bu bilgisayar FasterMC için uygun değil veya yeterince güçlü değil.\n\n" +
                    $"Gereken: en az 6 GB RAM\nBu bilgisayar: {totalGb:0.0} GB RAM",
                    "FasterMC — Sistem Yetersiz", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }

            // 🎬 LoL-tarzı animasyonlu splash — gösterilirken arka planda bütünlük + auto-login çalışır.
            var splash = new SplashWindow();
            splash.Show();

            // Marka dosyalarını (logo + ikon) launcher klasörüne kopyala.
            EnsureBrandingFiles();

            // 🛡️ Bütünlük kontrolü (splash animasyonu oynarken)
            splash.SetStatus("Bütünlük doğrulanıyor...");
            splash.SetProgress(0.3);
            await Task.Delay(1400);   // animasyon nefes alsın (logo belirme + parlama)
            bool tampered = false;
            try { tampered = await IntegrityGuard.CheckAsync(); }
            catch { /* kontrol hatası kullanıcıyı engellemesin */ }

            if (tampered)
            {
                // ❌ Doğrulanamadı — uyarı sesi + splash kapanır + uygulama biter
                SplashSound.PlayFail();
                splash.SetStatus("§ Bütünlük doğrulanamadı!");
                await Task.Delay(1500);
                splash.Close();
                Dialogs.FWDialog.Error("Launcher bütünlüğü doğrulanamadı. Güvenlik nedeniyle kapatılıyor.\n" +
                                       "Orijinal launcher'ı tekrar indir.", "Güvenlik");
                Shutdown();
                return;
            }

            // ✅ Doğrulandı — başarı sesi (rüzgâr/başarı efekti)
            SplashSound.PlaySuccess();

            // Oturum kontrolü
            splash.SetStatus("Oturum kontrol ediliyor...");
            splash.SetProgress(0.7);
            await Task.Delay(1000);   // durum değişimi görünsün
            string? username = null;
            try { username = await SessionManager.TryAutoLogin(); }
            catch (Exception ex) { Logger.Error("Otomatik giriş hatası.", ex); }

            splash.SetStatus("Hoş geldin!");
            splash.SetProgress(0.95);

            // Minimum splash süresi (animasyon tamamlansın — LoL hissi, yavaş geçiş)
            await Task.Delay(1600);

            // Splash'ı kapat + gerçek pencereyi aç
            string? finalUser = username;
            await splash.FadeOutAndClose(() =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(finalUser))
                    {
                        Logger.Info($"Otomatik giriş başarılı: {finalUser}");
                        var main = new MainWindow();
                        main.NavigateToMainView(finalUser);
                        main.Show();
                    }
                    else
                    {
                        new LoginWindow().Show();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("Pencere açılışı hatası.", ex);
                    new LoginWindow().Show();
                }
            });
        }

        /// <summary>Gömülü logo (.png) ve ikon (.ico) dosyalarını launcher klasörüne kopyalar.</summary>
        // ---- Toplam fiziksel RAM (GB) — GlobalMemoryStatusEx; okunamazsa 0 döner (engelleme yapılmaz) ----
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                         ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

        private static double GetTotalRamGb()
        {
            try
            {
                var st = new MemoryStatusEx { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatusEx>() };
                if (GlobalMemoryStatusEx(ref st)) return st.ullTotalPhys / 1073741824.0;
            }
            catch { }
            return 0;
        }

        private static void EnsureBrandingFiles()
        {
            try
            {
                var dir = SettingsService.LauncherDirectory;
                Directory.CreateDirectory(dir);
                // Eski adlar (mod/kısayol uyumluluğu için) + yeni FireWing marka dosyaları
                CopyResource("pack://application:,,,/PokeWingLauncher.png", Path.Combine(dir, "PokeWingLauncher.png"));
                CopyResource("pack://application:,,,/icon/FWLauncherV2.ico", Path.Combine(dir, "PokeWingLauncher.ico"));
                CopyResource("pack://application:,,,/Assets/Images/logo_full.png", Path.Combine(dir, "FasterMCLauncher.png"));
                CopyResource("pack://application:,,,/icon/FWLauncherV2.ico", Path.Combine(dir, "FasterMCLauncher.ico"));
            }
            catch (Exception ex)
            {
                Logger.Warn($"Marka dosyaları kopyalanamadı: {ex.Message}");
            }
        }

        private static void CopyResource(string packUri, string destPath)
        {
            if (File.Exists(destPath))
                return;

            var info = Application.GetResourceStream(new Uri(packUri, UriKind.Absolute));
            if (info?.Stream == null)
                return;

            using var stream = info.Stream;
            using var fs = File.Create(destPath);
            stream.CopyTo(fs);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Logger.Error("İşlenmeyen arayüz hatası.", e.Exception);
            FWDialog.Error($"Beklenmeyen bir hata oluştu:\n{e.Exception.Message}", "FasterMC Launcher");
            e.Handled = true; // uygulamanın çökmesini engelle
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
            => Logger.Error("İşlenmeyen uygulama hatası.", e.ExceptionObject as Exception);

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            Logger.Error("Gözlenmeyen görev (Task) hatası.", e.Exception);
            e.SetObserved();
        }
    }
}
