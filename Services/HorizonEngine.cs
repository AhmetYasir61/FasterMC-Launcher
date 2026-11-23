using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace FWLauncherV2.Services
{
    /// <summary>Oyunun oturumu (Launch API'nin session JSON'u). Yalnızca stdin ile verilir, diske yazılmaz.</summary>
    public sealed record HorizonSession(string Name, string Uuid, string AccessToken);

    /// <summary>GitHub'daki bir Horizon sürümü: etiket + Windows zip'i ve imzalı sağlama listesi.</summary>
    public sealed record HorizonRelease(string Tag, string ZipUrl, string SumsUrl, string SigUrl);

    /// <summary>
    /// Horizon motoru: Minecraft Java 26.3'ün Rust ile yazılmış, JVM'siz sürümü (github.com/horizon-rust/horizon).
    /// Kurulum sırası: imzalı sürümü indir → oyuncunun KENDİ Minecraft 26.3 dosyalarından kaynakları hazırla →
    /// horizon-client.exe'yi Launch API (docs/api/launch.md) ile başlat.
    /// Java yalnızca hazırlamada, bir kez çalışır; oyun çalışırken Java yoktur.
    /// </summary>
    public static class HorizonEngine
    {
        private const string Repo = "horizon-rust/horizon";
        private const string Asset = "horizon-windows-x64.zip";
        private const string Sums = "SHA256SUMS";
        private const string Signature = "SHA256SUMS.sig";
        private const string Complete = ".installed";
        private const string ResourcesManifest = "horizon-resources.json";
        private const string ModManifest = "horizon-mod.toml";

        /// <summary>crates/horizon-release-sign/release-keys.txt: Horizon sürümlerini imzalayan Ed25519 anahtarları.</summary>
        private static readonly string[] TrustedKeys = { "MU4oiIQcCxkZvbwSjjXs7SySCuM6ywRQ3nGaJYZG0gw=" };

        private static readonly Regex DatedNightly = new(@"^nightly-\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);

        public static string Root => Path.Combine(SettingsService.LauncherDirectory, "horizon");
        public static string VersionDir(string tag) => Path.Combine(Root, "versions", tag);
        private static string ClientExe(string tag) => Path.Combine(VersionDir(tag), "horizon-client.exe");

        public static string ResourcesDir(string tag) =>
            Path.Combine(VersionDir(tag), ".local", "horizon-client", ReadPins(tag).Version);

        public static bool IsInstalled(string tag) =>
            File.Exists(Path.Combine(VersionDir(tag), Complete)) && File.Exists(ClientExe(tag));

        public static bool IsPrepared(string tag) =>
            IsInstalled(tag) && File.Exists(Path.Combine(ResourcesDir(tag), ResourcesManifest));

        /// <summary>En yeni kurulu ve hazır sürüm; oyna tuşu bunu kullanır (ağ gerekmez).</summary>
        public static string? NewestPrepared()
        {
            var dir = Path.Combine(Root, "versions");
            if (!Directory.Exists(dir)) return null;
            return Directory.GetDirectories(dir).Select(Path.GetFileName)
                .Where(t => t != null && IsPrepared(t)).OrderByDescending(t => t, StringComparer.Ordinal).FirstOrDefault();
        }

        // ---- Sürüm bulma ve kurulum ----

        /// <summary>Verilen etiket, boşsa en yeni tarihli Nightly. "nightly" etiketi değişken olduğu için kullanılmaz.</summary>
        public static async Task<HorizonRelease> FindReleaseAsync(string? tag)
        {
            string json = await HttpClientProvider.Api.GetStringAsync($"https://api.github.com/repos/{Repo}/releases?per_page=100");
            using var doc = JsonDocument.Parse(json);
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                string name = r.GetProperty("tag_name").GetString() ?? "";
                bool wanted = string.IsNullOrWhiteSpace(tag) ? DatedNightly.IsMatch(name) : name == tag;
                if (!wanted) continue;
                var assets = r.GetProperty("assets").EnumerateArray().ToDictionary(
                    a => a.GetProperty("name").GetString() ?? "", a => a.GetProperty("browser_download_url").GetString() ?? "");
                if (assets.TryGetValue(Asset, out var zip) && assets.TryGetValue(Sums, out var sums)
                    && assets.TryGetValue(Signature, out var sig))
                    return new HorizonRelease(name, zip, sums, sig);
            }
            throw new Exception(string.IsNullOrWhiteSpace(tag)
                ? "Windows için yayınlanmış Horizon sürümü bulunamadı."
                : $"Horizon sürümü bulunamadı: {tag} (Nightly'ler yaklaşık 7 gün saklanır).");
        }

        /// <summary>İmzayı ve SHA-256'yı doğrulamadan hiçbir dosyayı açmaz.</summary>
        public static async Task InstallAsync(HorizonRelease release, IProgress<(string Text, int Percent)> progress)
        {
            string dir = VersionDir(release.Tag);
            if (IsInstalled(release.Tag)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(dir)!);

            string staging = dir + ".partial";
            string zip = staging + ".zip";
            try
            {
                byte[] sums = await HttpClientProvider.Api.GetByteArrayAsync(release.SumsUrl);
                string sig = await HttpClientProvider.Api.GetStringAsync(release.SigUrl);
                if (!VerifySignature(release.Tag, sums, sig))
                    throw new Exception("Horizon sürümünün imzası doğrulanamadı; kurulum durduruldu.");
                string expected = ExpectedSha256(Encoding.UTF8.GetString(sums), Asset)
                    ?? throw new Exception($"{Sums} içinde {Asset} yok.");

                await DownloadAsync(release.ZipUrl, zip, "Horizon indiriliyor", 0, 30, progress);
                string actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(zip)));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("İndirilen Horizon dosyası bozuk (SHA-256 eşleşmedi).");

                progress.Report(("Horizon açılıyor...", 31));
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                await Task.Run(() => ExtractStrippingTop(zip, staging));
                File.WriteAllText(Path.Combine(staging, Complete), release.Tag);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.Move(staging, dir);
            }
            finally
            {
                TryDelete(zip);
                if (Directory.Exists(staging)) { try { Directory.Delete(staging, true); } catch { } }
            }
        }

        /// <summary>
        /// Her satır bir Ed25519 imzası; "horizon release &lt;kanal&gt;\n" + SHA256SUMS üzerine atılır. Nightly'lerin kanalı,
        /// etiketleri ne olursa olsun "nightly"dir; diğer sürümlerde kanal etiketin kendisidir.
        /// </summary>
        public static bool VerifySignature(string tag, byte[] sums, string signatures)
        {
            string channel = tag == "nightly" || tag.StartsWith("nightly-", StringComparison.Ordinal) ? "nightly" : tag;
            byte[] message = Encoding.UTF8.GetBytes($"horizon release {channel}\n").Concat(sums).ToArray();
            foreach (var line in signatures.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                byte[] sig;
                try { sig = Convert.FromBase64String(line); } catch (FormatException) { continue; }
                if (sig.Length != 64) continue;
                foreach (var key in TrustedKeys)
                {
                    var verifier = new Ed25519Signer();
                    verifier.Init(false, new Ed25519PublicKeyParameters(Convert.FromBase64String(key), 0));
                    verifier.BlockUpdate(message, 0, message.Length);
                    if (verifier.VerifySignature(sig)) return true;
                }
            }
            return false;
        }

        /// <summary>"&lt;hex&gt;  &lt;dosya&gt;" veya "&lt;hex&gt; *&lt;dosya&gt;" satırlarından dosyanın SHA-256'sı.</summary>
        public static string? ExpectedSha256(string sums, string file)
        {
            foreach (var line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1].Trim().TrimStart('*') == file) return parts[0];
            }
            return null;
        }

        private static void ExtractStrippingTop(string zip, string dest)
        {
            using var archive = ZipFile.OpenRead(zip);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                int slash = entry.FullName.IndexOf('/');
                if (slash < 0) continue;
                ExtractSafely(entry, entry.FullName[(slash + 1)..], dest);
            }
        }

        /// <summary>Zip içindeki yol hedef klasörden çıkamaz (zip slip).</summary>
        private static void ExtractSafely(ZipArchiveEntry entry, string relative, string dest)
        {
            string root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new Exception($"Zip içinde geçersiz yol: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }

        // ---- Hazırlama (oyuncunun kendi Minecraft dosyalarından) ----

        public sealed record Pins(string Version, string ClientSha1, string ServerSha1, int JavaMajor);

        public static Pins ReadPins(string tag)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(VersionDir(tag), "research-lock.json")));
            var mc = doc.RootElement.GetProperty("minecraft");
            return new Pins(mc.GetProperty("version").GetString()!, mc.GetProperty("client_sha1").GetString()!,
                mc.GetProperty("server_sha1").GetString()!, mc.GetProperty("java_major").GetInt32());
        }

        /// <summary>Kurulu Minecraft klasörleri: resmî launcher, CurseForge, launcher'ın sahip için indirdiği kopya.</summary>
        public static IEnumerable<string> MinecraftFolders()
        {
            yield return Path.Combine(Root, "minecraft");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
                yield return Path.Combine(drive.RootDirectory.FullName, "curseforge", "minecraft", "Install");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "curseforge", "minecraft", "Install");
        }

        /// <summary>İstemci jar'ı Horizon'ın sabitlediği SHA-1 ile eşleşen ilk klasör.</summary>
        public static string? FindMinecraft(Pins pins, IEnumerable<string>? folders = null)
        {
            foreach (var dir in folders ?? MinecraftFolders())
            {
                string jar = Path.Combine(dir, "versions", pins.Version, $"{pins.Version}.jar");
                string json = Path.Combine(dir, "versions", pins.Version, $"{pins.Version}.json");
                if (!File.Exists(jar) || !File.Exists(json)) continue;
                using var stream = File.OpenRead(jar);
                if (Convert.ToHexString(SHA1.HashData(stream)).Equals(pins.ClientSha1, StringComparison.OrdinalIgnoreCase))
                    return dir;
            }
            return null;
        }

        /// <summary>Sürümün istediği Java: Minecraft'ın kendi çalışma zamanı, JAVA_HOME, kurulu JDK/JRE'ler.</summary>
        public static string? FindJavaHome(int major, string minecraft)
        {
            var candidates = new List<string>();
            foreach (var runtimes in new[] { Path.Combine(minecraft, "runtime"), Path.Combine(minecraft, "java") })
                if (Directory.Exists(runtimes))
                    candidates.AddRange(Directory.EnumerateFiles(runtimes, "release", SearchOption.AllDirectories)
                        .Select(f => Path.GetDirectoryName(f)!));
            if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } home) candidates.Add(home);
            candidates.Add(Path.Combine(Root, "java"));
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Microsoft", "Zulu" })
            {
                string dir = Path.Combine(programFiles, vendor);
                if (Directory.Exists(dir)) candidates.AddRange(Directory.GetDirectories(dir));
            }
            return candidates.FirstOrDefault(c => JavaMajorOf(c) == major && File.Exists(Path.Combine(c, "bin", "java.exe")));
        }

        private static int JavaMajorOf(string home)
        {
            try
            {
                var match = Regex.Match(File.ReadAllText(Path.Combine(home, "release")), "JAVA_VERSION=\"(\\d+)");
                return match.Success ? int.Parse(match.Groups[1].Value) : 0;
            }
            catch (IOException) { return 0; }
            catch (UnauthorizedAccessException) { return 0; }
        }

        /// <summary>
        /// Hazırlama: gerekli sunucu jar'ını Mojang'dan indirip doğrular, sonra sürümün kendi horizon-prepare'ini çalıştırır.
        /// Sesler isteğe bağlıdır; kopyalanamazsa oyun sessiz açılır, kurulum bozulmaz.
        /// </summary>
        public static async Task PrepareAsync(string tag, string minecraft, string javaHome, IProgress<(string Text, int Percent)> progress)
        {
            var pins = ReadPins(tag);
            string jar = await ServerJarAsync(pins, minecraft, progress);
            progress.Report(("Oyun dosyaları hazırlanıyor (birkaç dakika sürebilir)...", 45));
            await RunPrepareAsync(tag, javaHome, new[] { "--prepare", "--server-jar", jar, "--minecraft-dir", minecraft, "--root", VersionDir(tag) });
            progress.Report(("Sesler kopyalanıyor...", 90));
            try { await RunPrepareAsync(tag, javaHome, new[] { "sounds", "--minecraft-dir", minecraft, "--root", VersionDir(tag) }); }
            catch (Exception ex) { Logger.Warn($"Horizon sesleri kopyalanamadı: {ex.Message}"); }
        }

        private static async Task<string> ServerJarAsync(Pins pins, string minecraft, IProgress<(string Text, int Percent)> progress)
        {
            string jar = Path.Combine(Root, "server-jars", $"{pins.Version}.jar");
            if (File.Exists(jar) && Sha1Of(jar).Equals(pins.ServerSha1, StringComparison.OrdinalIgnoreCase)) return jar;
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(minecraft, "versions", pins.Version, $"{pins.Version}.json")));
            string url = doc.RootElement.GetProperty("downloads").GetProperty("server").GetProperty("url").GetString()
                ?? throw new Exception("Sürüm dosyasında sunucu indirme adresi yok.");
            Directory.CreateDirectory(Path.GetDirectoryName(jar)!);
            string partial = jar + ".partial";
            await DownloadAsync(url, partial, "Sunucu dosyası indiriliyor", 32, 44, progress);
            if (!Sha1Of(partial).Equals(pins.ServerSha1, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(partial);
                throw new Exception("İndirilen sunucu dosyası Horizon'ın beklediği sürüm değil (SHA-1 eşleşmedi).");
            }
            File.Move(partial, jar, true);
            return jar;
        }

        private static string Sha1Of(string file)
        {
            using var stream = File.OpenRead(file);
            return Convert.ToHexString(SHA1.HashData(stream));
        }

        private static async Task RunPrepareAsync(string tag, string javaHome, IEnumerable<string> args)
        {
            var info = new ProcessStartInfo(Path.Combine(VersionDir(tag), "horizon-prepare.exe"))
            {
                WorkingDirectory = VersionDir(tag),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) info.ArgumentList.Add(a);
            info.EnvironmentVariables["JAVA_HOME"] = javaHome;
            info.EnvironmentVariables["PATH"] = Path.Combine(javaHome, "bin") + ";" + info.EnvironmentVariables["PATH"];

            using var process = Process.Start(info) ?? throw new Exception("horizon-prepare başlatılamadı.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string log = await output + await error;
            Logger.Info($"horizon-prepare çıkış kodu {process.ExitCode}");
            if (process.ExitCode != 0)
                throw new Exception($"Hazırlama başarısız (kod {process.ExitCode}):\n{string.Join('\n', log.Split('\n').TakeLast(6))}");
        }

        // ---- Paket içeriği ----

        /// <summary>
        /// Horizon paketi zip'i: yalnızca horizon-mods/ (.hmod) ve resourcepacks/ açılır. .jar çalışmadığı için alınmaz.
        /// Paketin .hmod'ları horizon-mods.txt içinde açılır; oyuncunun diğer satırlarına dokunulmaz.
        /// </summary>
        public static void InstallPackZip(string zip, string packDir)
        {
            using var archive = ZipFile.OpenRead(zip);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                string path = entry.FullName.Replace('\\', '/');
                bool wanted = (path.StartsWith("horizon-mods/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".hmod", StringComparison.OrdinalIgnoreCase))
                              || path.StartsWith("resourcepacks/", StringComparison.OrdinalIgnoreCase);
                if (wanted) ExtractSafely(entry, path, packDir);
            }
        }

        public static void EnablePackMods(string packDir)
        {
            string modsDir = Path.Combine(packDir, "horizon-mods");
            if (!Directory.Exists(modsDir)) return;
            foreach (var hmod in Directory.GetFiles(modsDir, "*.hmod")) UnpackMod(hmod, modsDir);

            string file = Path.Combine(packDir, "horizon-mods.txt");
            var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : new List<string>();
            foreach (var dir in Directory.GetDirectories(modsDir))
            {
                string manifest = Path.Combine(dir, ModManifest);
                if (!File.Exists(manifest)) continue;
                string? id = ModIdOf(File.ReadAllText(manifest));
                if (id == null) { Logger.Warn($"{ModManifest} okunamadı: {dir}"); continue; }
                lines.RemoveAll(l => l.Trim().StartsWith($"mod.{id}:", StringComparison.Ordinal));
                lines.Add($"mod.{id}:true");
            }
            File.WriteAllLines(file, lines);
        }

        /// <summary>
        /// Horizon zip modda her eşya tanımı için arşivi yeniden açar: 2000 eşyalık modda açılış dakikalarca siyah ekranda
        /// kalır. Klasör modda bu maliyet yoktur, bu yüzden .hmod klasöre açılıp silinir.
        /// </summary>
        private static void UnpackMod(string hmod, string modsDir)
        {
            using (var archive = ZipFile.OpenRead(hmod))
            {
                var manifest = archive.GetEntry(ModManifest);
                string? id = null;
                if (manifest != null)
                {
                    using var reader = new StreamReader(manifest.Open());
                    id = ModIdOf(reader.ReadToEnd());
                }
                if (id == null) { Logger.Warn($"{ModManifest} okunamadı: {hmod}"); return; }
                string target = Path.Combine(modsDir, id);
                if (Directory.Exists(target)) Directory.Delete(target, true);
                foreach (var entry in archive.Entries)
                    if (!string.IsNullOrEmpty(entry.Name)) ExtractSafely(entry, entry.FullName, target);
            }
            File.Delete(hmod);
        }

        private static string? ModIdOf(string manifest)
        {
            var match = Regex.Match(manifest, "^\\s*id\\s*=\\s*\"([a-z0-9_]{1,64})\"", RegexOptions.Multiline);
            return match.Success ? match.Groups[1].Value : null;
        }

        // ---- Başlatma ----

        /// <summary>horizon-client'ın Launch API'sinde söz verdiği seçenekler; yalnızca bunlar geçilir (bilinmeyen = kod 2).</summary>
        public static async Task<HashSet<string>> LaunchOptionsAsync(string tag)
        {
            var info = new ProcessStartInfo(ClientExe(tag), "--launch-api")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var process = Process.Start(info) ?? throw new Exception("horizon-client başlatılamadı.");
            string json = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            using var doc = JsonDocument.Parse(json);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in doc.RootElement.GetProperty("options").EnumerateArray())
                names.Add(option.ValueKind == JsonValueKind.String ? option.GetString()! : option.GetProperty("name").GetString()!);
            return names;
        }

        public static ProcessStartInfo BuildStart(string tag, string packDir, int width, int height, bool fullScreen,
            string? server, bool withSession, HashSet<string> options)
        {
            var info = new ProcessStartInfo(ClientExe(tag))
            {
                WorkingDirectory = packDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = withSession,
            };
            void Add(string name, params string[] values)
            {
                if (!options.Contains(name)) return;
                info.ArgumentList.Add(name);
                foreach (var v in values) info.ArgumentList.Add(v);
            }
            Add("--resources", ResourcesDir(tag));
            Add("--game-dir", packDir);
            Add("--home", packDir);
            Add("--width", width.ToString());
            Add("--height", height.ToString());
            if (fullScreen) Add("--fullscreen");
            if (!string.IsNullOrWhiteSpace(server)) Add("--quickPlayMultiplayer", server);
            if (withSession) Add("--session-stdin");
            return info;
        }

        public static string SessionJson(HorizonSession session) => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["name"] = session.Name,
            ["uuid"] = session.Uuid,
            ["access_token"] = session.AccessToken,
            ["user_type"] = "msa",
        });

        // ---- Yardımcılar ----

        private static async Task DownloadAsync(string url, string dest, string label, int from, int to, IProgress<(string Text, int Percent)> progress)
        {
            using var response = await HttpClientProvider.Download.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0;
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = File.Create(dest);
            var buffer = new byte[81920];
            long done = 0;
            int read, last = -1;
            while ((read = await input.ReadAsync(buffer)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                int percent = total > 0 ? (int)(done * 100 / total) : 0;
                if (percent == last) continue;
                last = percent;
                progress.Report(($"{label}...  (%{percent})", from + (to - from) * percent / 100));
            }
        }

        private static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { }
        }
    }
}
