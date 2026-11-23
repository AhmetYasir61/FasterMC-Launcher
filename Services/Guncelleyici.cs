using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace FWLauncherV2.Services
{
    /// <summary>
    /// Arka plan guncelleme denetcisi:
    ///  - launcher.json (surum + exe adresi): yeni launcher surumu var mi
    ///  - modpacks.json: launcher acildiktan sonra paket bilgisi degisti mi
    /// Bir olay varsa <see cref="Olay"/> tetiklenir; sag alttaki baloncuk bunu gosterir.
    /// </summary>
    public static class Guncelleyici
    {
        public const string LauncherJson = "https://firewing.store/fmc/launcher.json";
        public const string ModpackJson = "https://firewing.store/fmc/modpacks.json";

        public enum Tur { Yok, Launcher, Paket }
        public static event Action<Tur, string>? Olay;

        static string? _ilkPaketOzet;
        static string? _yeniLauncherUrl, _yeniLauncherSha;
        static System.Threading.Timer? _zamanlayici;

        public static Version Surum => Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);

        /// <summary>Launcher'in kurulu oldugu yer (%AppData%\FasterMCLauncher\FasterMCLauncher.exe).</summary>
        public static string KurulumExe => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FasterMCLauncher", "FasterMCLauncher.exe");

        public static void Baslat()
        {
            _zamanlayici ??= new System.Threading.Timer(async _ => await KontrolAsync(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(90));
        }

        static async Task KontrolAsync()
        {
            try
            {
                var http = HttpClientProvider.Api;
                // 1) launcher surumu
                try
                {
                    using var lj = JsonDocument.Parse(await http.GetStringAsync(LauncherJson + "?t=" + DateTime.UtcNow.Ticks));
                    var r = lj.RootElement;
                    if (Version.TryParse(r.GetProperty("surum").GetString(), out var yeni) && yeni > Surum)
                    {
                        _yeniLauncherUrl = r.GetProperty("url").GetString();
                        _yeniLauncherSha = r.TryGetProperty("sha256", out var s) ? s.GetString() : null;
                        Olay?.Invoke(Tur.Launcher, $"Launcher güncellemesi hazır (v{yeni})");
                        return;
                    }
                }
                catch { /* launcher.json yoksa sessiz */ }

                // 2) modpaketi bilgisi
                var mp = await http.GetStringAsync(ModpackJson + "?t=" + DateTime.UtcNow.Ticks);
                var ozet = Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(mp)));
                if (_ilkPaketOzet == null) { _ilkPaketOzet = ozet; return; }
                if (ozet != _ilkPaketOzet) Olay?.Invoke(Tur.Paket, "Modpaketine güncelleme geldi");
            }
            catch (Exception ex) { Logger.Warn("[Guncelleyici] " + ex.Message); }
        }

        /// <summary>Onar: surumden bagimsiz, sitedeki guncel launcher'i indirip yerine koyar.</summary>
        public static async Task<bool> OnarAsync()
        {
            using var lj = JsonDocument.Parse(await HttpClientProvider.Api.GetStringAsync(LauncherJson + "?t=" + DateTime.UtcNow.Ticks));
            _yeniLauncherUrl = lj.RootElement.GetProperty("url").GetString();
            _yeniLauncherSha = lj.RootElement.TryGetProperty("sha256", out var s) ? s.GetString() : null;
            return await LauncherGuncelleAsync();
        }

        /// <summary>Paket listesi yeniden yuklendi: yeni ozet taban olur (baloncuk kapanir).</summary>
        public static void PaketYenilendi() => _ilkPaketOzet = null;

        /// <summary>
        /// Yeni exe'yi indirir; kucuk bir betik launcher kapanana kadar bekler, exe'yi degistirir ve yeniden acar.
        /// </summary>
        public static async Task<bool> LauncherGuncelleAsync(IProgress<double>? ilerleme = null)
        {
            if (string.IsNullOrEmpty(_yeniLauncherUrl)) return false;
            string hedef = KurulumExe, gecici = hedef + ".yeni";
            Directory.CreateDirectory(Path.GetDirectoryName(hedef)!);
            using (var yanit = await HttpClientProvider.Download.GetAsync(_yeniLauncherUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                yanit.EnsureSuccessStatusCode();
                long toplam = yanit.Content.Headers.ContentLength ?? -1, inen = 0;
                await using var kaynak = await yanit.Content.ReadAsStreamAsync();
                await using var dosya = File.Create(gecici);
                var tampon = new byte[1 << 16]; int n;
                while ((n = await kaynak.ReadAsync(tampon)) > 0)
                {
                    await dosya.WriteAsync(tampon.AsMemory(0, n)); inen += n;
                    if (toplam > 0) ilerleme?.Report(inen / (double)toplam);
                }
            }
            if (!string.IsNullOrEmpty(_yeniLauncherSha))
            {
                await using var f = File.OpenRead(gecici);
                if (!Convert.ToHexString(await SHA256.HashDataAsync(f)).Equals(_yeniLauncherSha, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(gecici);
                    throw new Exception("İndirilen launcher doğrulanamadı (SHA-256).");
                }
            }
            int pid = Environment.ProcessId;
            string bat = Path.Combine(Path.GetTempPath(), "fastermc_guncelle.cmd");
            File.WriteAllText(bat,
                "@echo off\r\n" +
                $":bekle\r\ntasklist /FI \"PID eq {pid}\" | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto bekle)\r\n" +
                $"move /Y \"{gecici}\" \"{hedef}\" >nul\r\n" +
                $"start \"\" \"{hedef}\"\r\n" +
                "del \"%~f0\"\r\n");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            return true;
        }
    }
}
