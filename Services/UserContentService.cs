using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FWLauncherV2.Services
{
    public enum ContentKind { Mod, Shader, ResourcePack }

    /// <summary>usermods.json'daki bir kayıt: oyuncunun kendi eklediği mod / gölgelendirici / kaynak paketi.</summary>
    public class UserContentEntry
    {
        [JsonPropertyName("file")] public string File { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("kind")] public ContentKind Kind { get; set; }
        [JsonPropertyName("source")] public string Source { get; set; } = "local";   // modrinth | curseforge | local
        [JsonPropertyName("projectId")] public string? ProjectId { get; set; }
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("iconUrl")] public string? IconUrl { get; set; }
        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
        [JsonPropertyName("installedAt")] public DateTime InstalledAt { get; set; } = DateTime.Now;
    }

    /// <summary>Modlar sayfasında gösterilen satır (ana paket veya kullanıcı içeriği).</summary>
    public class ContentRow
    {
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";
        /// <summary>Modun kendi ikonu: Modrinth/CurseForge adresi (string) ya da jar/zip içinden okunan görsel.
        /// Yoksa null kalır ve ikon kutusu hiç gösterilmez.</summary>
        public object? Icon { get; set; }
        public Visibility IconVisibility => Icon == null ? Visibility.Collapsed : Visibility.Visible;
        public bool Enabled { get; set; }
        public bool Locked { get; set; }
        public UserContentEntry? Entry { get; set; }
        public Visibility DeleteVisibility => Locked ? Visibility.Collapsed : Visibility.Visible;
        public bool CanToggle => !Locked;
    }

    public class MergeReport
    {
        public int Added { get; set; }
        public List<string> Conflicts { get; } = new();
    }

    /// <summary>
    /// Klasör düzeni (her paketin oyun klasöründe):
    ///   mods/        ← ANA PAKET (salt okunur; launcher senkronize eder)
    ///   usermods/    ← OYUNCU MODLARI (yazılabilir; kapalılar .jar.disabled)
    ///   usermods/usermods.json ← kaynak, sürüm, hash kayıtları
    ///   shaderpacks/, resourcepacks/ ← gölgelendirici ve kaynak paketleri
    /// Başlatma anında açık kullanıcı modları mods/ içine "um__" önekiyle kopyalanır;
    /// aynı mod ID ana pakette varsa ana paket kazanır (kullanıcı modu atlanır).
    /// </summary>
    public static class UserContentService
    {
        public const string MergePrefix = "um__";
        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static string UserModsDir(string packDir) => Path.Combine(packDir, "usermods");
        public static string FolderFor(string packDir, ContentKind k) => k switch
        {
            ContentKind.Mod => UserModsDir(packDir),
            ContentKind.Shader => Path.Combine(packDir, "shaderpacks"),
            _ => Path.Combine(packDir, "resourcepacks"),
        };
        private static string IndexPath(string packDir) => Path.Combine(UserModsDir(packDir), "usermods.json");

        public static event Action? Changed;
        private static void Raise() { try { Changed?.Invoke(); } catch { } }

        // ===================== KAYIT DOSYASI =====================
        public static List<UserContentEntry> LoadIndex(string packDir)
        {
            try
            {
                var p = IndexPath(packDir);
                if (File.Exists(p))
                    return JsonSerializer.Deserialize<List<UserContentEntry>>(File.ReadAllText(p), Json) ?? new();
            }
            catch (Exception ex) { Logger.Warn($"usermods.json okunamadı: {ex.Message}"); }
            return new();
        }

        public static void SaveIndex(string packDir, List<UserContentEntry> list)
        {
            Directory.CreateDirectory(UserModsDir(packDir));
            File.WriteAllText(IndexPath(packDir), JsonSerializer.Serialize(list, Json));
        }

        /// <summary>Kayıtları klasörlerle eşitler: elle atılan dosyaları "Yerel dosya" olarak ekler, silinenleri çıkarır.</summary>
        public static List<UserContentEntry> Reconcile(string packDir)
        {
            var list = LoadIndex(packDir);
            bool dirty = false;

            // silinen dosyalar
            int before = list.Count;
            list.RemoveAll(e => !File.Exists(ActualPath(packDir, e)));
            dirty |= list.Count != before;

            foreach (ContentKind k in Enum.GetValues(typeof(ContentKind)))
            {
                var dir = FolderFor(packDir, k);
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir))
                {
                    var name = Path.GetFileName(f);
                    if (name.Equals("usermods.json", StringComparison.OrdinalIgnoreCase)) continue;
                    bool disabled = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                    var baseName = disabled ? name[..^".disabled".Length] : name;
                    if (k == ContentKind.Mod && !baseName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
                    if (k != ContentKind.Mod && !baseName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    if (list.Any(e => e.Kind == k && e.File.Equals(baseName, StringComparison.OrdinalIgnoreCase))) continue;

                    list.Add(new UserContentEntry
                    {
                        File = baseName,
                        Name = k == ContentKind.Mod ? (ReadModMeta(f).Name ?? Path.GetFileNameWithoutExtension(baseName)) : Path.GetFileNameWithoutExtension(baseName),
                        Kind = k,
                        Source = "local",
                        Enabled = k == ContentKind.Mod ? !disabled : false,
                        Sha1 = Sha1Of(f)
                    });
                    dirty = true;
                }
            }

            if (dirty) { try { SaveIndex(packDir, list); } catch (Exception ex) { Logger.Warn($"usermods.json yazılamadı: {ex.Message}"); } }
            return list;
        }

        /// <summary>Diskteki gerçek yol (kapalı modlar .disabled uzantılı durur).</summary>
        public static string ActualPath(string packDir, UserContentEntry e)
        {
            var p = Path.Combine(FolderFor(packDir, e.Kind), e.File);
            if (e.Kind == ContentKind.Mod && !e.Enabled) p += ".disabled";
            if (!File.Exists(p))
            {
                // durum/dosya uyuşmazlığına dayanıklı ol
                var alt = e.Kind == ContentKind.Mod ? (e.Enabled ? p + ".disabled" : p[..^".disabled".Length]) : p;
                if (File.Exists(alt)) return alt;
            }
            return p;
        }

        // ===================== EKLE / SİL / AÇ-KAPAT =====================
        public static UserContentEntry AddFile(string packDir, string sourceFile, ContentKind kind, UserContentEntry? meta = null)
        {
            var dir = FolderFor(packDir, kind);
            Directory.CreateDirectory(dir);
            var fileName = Path.GetFileName(meta?.File is { Length: > 0 } mf ? mf : sourceFile);
            var dest = Path.Combine(dir, fileName);
            File.Copy(sourceFile, dest, true);
            var disabledTwin = dest + ".disabled";
            if (File.Exists(disabledTwin)) File.Delete(disabledTwin);

            var list = LoadIndex(packDir);
            list.RemoveAll(e => e.Kind == kind && e.File.Equals(fileName, StringComparison.OrdinalIgnoreCase));
            if (meta?.ProjectId != null)
            {
                // aynı projenin eski sürüm dosyalarını kaldır (güncelleme)
                foreach (var old in list.Where(e => e.Kind == kind && e.Source == meta.Source && e.ProjectId == meta.ProjectId).ToList())
                {
                    TryDelete(ActualPath(packDir, old));
                    list.Remove(old);
                }
            }

            var entry = meta ?? new UserContentEntry();
            entry.File = fileName;
            entry.Kind = kind;
            if (string.IsNullOrWhiteSpace(entry.Name))
                entry.Name = kind == ContentKind.Mod ? (ReadModMeta(dest).Name ?? Path.GetFileNameWithoutExtension(fileName)) : Path.GetFileNameWithoutExtension(fileName);
            entry.Sha1 ??= Sha1Of(dest);
            entry.InstalledAt = DateTime.Now;
            entry.Enabled = kind == ContentKind.Mod || entry.Enabled;
            list.Add(entry);
            SaveIndex(packDir, list);
            if (kind != ContentKind.Mod && entry.Enabled) ApplyPackConfigs(packDir, list, entry);
            Raise();
            return entry;
        }

        public static void Remove(string packDir, UserContentEntry entry)
        {
            var list = LoadIndex(packDir);
            var hit = list.FirstOrDefault(e => e.Kind == entry.Kind && e.File.Equals(entry.File, StringComparison.OrdinalIgnoreCase));
            TryDelete(ActualPath(packDir, entry));
            if (hit != null) list.Remove(hit);
            SaveIndex(packDir, list);
            if (entry.Kind != ContentKind.Mod) ApplyPackConfigs(packDir, list, null);
            Raise();
        }

        public static void SetEnabled(string packDir, UserContentEntry entry, bool enabled)
        {
            var list = LoadIndex(packDir);
            var hit = list.FirstOrDefault(e => e.Kind == entry.Kind && e.File.Equals(entry.File, StringComparison.OrdinalIgnoreCase));
            if (hit == null) return;

            if (hit.Kind == ContentKind.Mod)
            {
                var from = ActualPath(packDir, hit);
                var on = Path.Combine(FolderFor(packDir, hit.Kind), hit.File);
                var to = enabled ? on : on + ".disabled";
                if (!from.Equals(to, StringComparison.OrdinalIgnoreCase) && File.Exists(from))
                {
                    if (File.Exists(to)) File.Delete(to);
                    File.Move(from, to);
                }
            }
            else if (hit.Kind == ContentKind.Shader && enabled)
            {
                // aynı anda tek gölgelendirici açık olabilir
                foreach (var e in list.Where(x => x.Kind == ContentKind.Shader)) e.Enabled = false;
            }

            hit.Enabled = enabled;
            entry.Enabled = enabled;
            SaveIndex(packDir, list);
            if (hit.Kind != ContentKind.Mod) ApplyPackConfigs(packDir, list, null);
            Raise();
        }

        public static bool IsInstalled(string packDir, string source, string projectId)
            => LoadIndex(packDir).Any(e => e.Source == source && e.ProjectId == projectId);

        // ===================== GÖLGELENDİRİCİ / KAYNAK PAKETİ AYARLARI =====================
        /// <summary>Iris/Oculus (shaderPack) ve options.txt (resourcePacks) ayarlarını kayıtlara göre yazar.</summary>
        public static void ApplyPackConfigs(string packDir, List<UserContentEntry> list, UserContentEntry? justAdded)
        {
            try
            {
                if (justAdded?.Kind == ContentKind.Shader && justAdded.Enabled)
                    foreach (var e in list.Where(x => x.Kind == ContentKind.Shader && !ReferenceEquals(x, justAdded))) e.Enabled = false;

                var shader = list.FirstOrDefault(e => e.Kind == ContentKind.Shader && e.Enabled);
                var cfg = Path.Combine(packDir, "config");
                Directory.CreateDirectory(cfg);
                foreach (var name in new[] { "iris.properties", "oculus.properties" })
                {
                    var p = Path.Combine(cfg, name);
                    if (name == "oculus.properties" && !File.Exists(p)) continue;
                    var props = File.Exists(p) ? File.ReadAllLines(p).ToList() : new List<string>();
                    SetProp(props, "enableShaders", shader != null ? "true" : "false");
                    if (shader != null) SetProp(props, "shaderPack", shader.File);
                    File.WriteAllLines(p, props);
                }

                var opt = Path.Combine(packDir, "options.txt");
                var lines = File.Exists(opt) ? File.ReadAllLines(opt).ToList() : new List<string>();
                var current = new List<string>();
                int idx = lines.FindIndex(l => l.StartsWith("resourcePacks:"));
                if (idx >= 0)
                {
                    try { current = JsonSerializer.Deserialize<List<string>>(lines[idx]["resourcePacks:".Length..]) ?? new(); }
                    catch { current = new() { "vanilla" }; }
                }
                else current.Add("vanilla");

                var ours = list.Where(e => e.Kind == ContentKind.ResourcePack).Select(e => "file/" + e.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
                current.RemoveAll(c => ours.Contains(c));
                foreach (var e in list.Where(e => e.Kind == ContentKind.ResourcePack && e.Enabled))
                    current.Add("file/" + e.File);
                var line = "resourcePacks:" + JsonSerializer.Serialize(current);
                if (idx >= 0) lines[idx] = line; else lines.Add(line);
                File.WriteAllLines(opt, lines);
                SaveIndex(packDir, list);
            }
            catch (Exception ex) { Logger.Warn($"Gölgelendirici/kaynak paketi ayarı yazılamadı: {ex.Message}"); }
        }

        private static void SetProp(List<string> props, string key, string value)
        {
            int i = props.FindIndex(l => l.StartsWith(key + "="));
            if (i >= 0) props[i] = $"{key}={value}"; else props.Add($"{key}={value}");
        }

        // ===================== BAŞLATMA: İKİ KLASÖR BİRLEŞİR =====================
        /// <summary>
        /// Önceki birleştirme kopyalarını temizler; açık kullanıcı modlarını mods/ içine kopyalar.
        /// Aynı mod ID ana pakette varsa ana paket kazanır.
        /// </summary>
        public static MergeReport PrepareForLaunch(string packDir, bool userModsEnabled)
        {
            var report = new MergeReport();
            var modsDir = Path.Combine(packDir, "mods");
            Directory.CreateDirectory(modsDir);

            foreach (var old in Directory.GetFiles(modsDir, MergePrefix + "*"))
                TryDelete(old);

            var list = Reconcile(packDir);
            ApplyPackConfigs(packDir, list, null);
            if (!userModsEnabled) return report;

            var mainIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var jar in Directory.GetFiles(modsDir, "*.jar"))
                foreach (var id in ReadModMeta(jar).Ids) mainIds.Add(id);

            foreach (var e in list.Where(x => x.Kind == ContentKind.Mod && x.Enabled))
            {
                var src = ActualPath(packDir, e);
                if (!File.Exists(src)) continue;
                var ids = ReadModMeta(src).Ids;
                var clash = ids.FirstOrDefault(mainIds.Contains);
                if (clash != null)
                {
                    report.Conflicts.Add($"{e.Name} ({clash})");
                    Logger.Warn($"Kullanıcı modu atlandı — ana pakette aynı mod ID var: {e.File} [{clash}]");
                    continue;
                }
                if (File.Exists(Path.Combine(modsDir, e.File)))
                {
                    report.Conflicts.Add($"{e.Name} (aynı dosya)");
                    continue;
                }
                File.Copy(src, Path.Combine(modsDir, MergePrefix + e.File), true);
                foreach (var id in ids) mainIds.Add(id);
                report.Added++;
            }
            Logger.Info($"Kullanıcı modları birleştirildi: {report.Added} eklendi, {report.Conflicts.Count} çakışma.");
            return report;
        }

        // ===================== ANA PAKET LİSTESİ =====================
        public static List<ContentRow> MainPackRows(string packDir)
        {
            var rows = new List<ContentRow>();
            var modsDir = Path.Combine(packDir, "mods");
            if (!Directory.Exists(modsDir)) return rows;
            foreach (var jar in Directory.GetFiles(modsDir, "*.jar").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var file = Path.GetFileName(jar);
                if (file.StartsWith(MergePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                var meta = ReadModMeta(jar);
                rows.Add(new ContentRow
                {
                    Title = meta.Name ?? Path.GetFileNameWithoutExtension(file),
                    Subtitle = "Ana paket · zorunlu" + (meta.Version is { Length: > 0 } v && !v.Contains('$') ? $" · {v}" : ""),
                    Icon = ReadIcon(jar, ContentKind.Mod),
                    Enabled = true,
                    Locked = true
                });
            }
            return rows;
        }

        // ===================== JAR META VERİSİ =====================
        public record ModMeta(List<string> Ids, string? Name, string? Version);

        private static readonly Dictionary<string, (DateTime, ModMeta)> MetaCache = new(StringComparer.OrdinalIgnoreCase);

        public static ModMeta ReadModMeta(string jarPath)
        {
            try
            {
                var stamp = File.GetLastWriteTimeUtc(jarPath);
                lock (MetaCache)
                    if (MetaCache.TryGetValue(jarPath, out var c) && c.Item1 == stamp) return c.Item2;

                var ids = new List<string>();
                string? name = null, version = null;
                using (var zip = ZipFile.OpenRead(jarPath))
                {
                    var toml = zip.GetEntry("META-INF/neoforge.mods.toml") ?? zip.GetEntry("META-INF/mods.toml");
                    if (toml != null)
                    {
                        using var r = new StreamReader(toml.Open());
                        string section = "";
                        foreach (var raw in r.ReadToEnd().Split('\n'))
                        {
                            var line = raw.Trim();
                            if (line.StartsWith("[")) { section = line; continue; }
                            if (section != "[[mods]]") continue;
                            var m = Regex.Match(line, "^(modId|displayName|version)\\s*=\\s*[\"']([^\"']*)[\"']");
                            if (!m.Success) continue;
                            if (m.Groups[1].Value == "modId") ids.Add(m.Groups[2].Value);
                            else if (m.Groups[1].Value == "displayName") name ??= m.Groups[2].Value;
                            else version ??= m.Groups[2].Value;
                        }
                    }
                    var fabric = zip.GetEntry("fabric.mod.json") ?? zip.GetEntry("quilt.mod.json");
                    if (fabric != null && ids.Count == 0)
                    {
                        using var s = fabric.Open();
                        using var doc = JsonDocument.Parse(s, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                        var root = doc.RootElement;
                        if (root.TryGetProperty("quilt_loader", out var q)) root = q;
                        if (root.TryGetProperty("id", out var id)) ids.Add(id.GetString() ?? "");
                        if (root.TryGetProperty("name", out var n)) name = n.GetString();
                        if (root.TryGetProperty("version", out var v)) version = v.GetString();
                        if (root.TryGetProperty("metadata", out var md) && md.TryGetProperty("name", out var mn)) name ??= mn.GetString();
                    }
                }
                var meta = new ModMeta(ids.Where(i => !string.IsNullOrWhiteSpace(i)).ToList(), name, version);
                lock (MetaCache) MetaCache[jarPath] = (stamp, meta);
                return meta;
            }
            catch
            {
                return new ModMeta(new List<string>(), null, null);
            }
        }

        // ===================== İKON =====================
        private static readonly Dictionary<string, (DateTime, ImageSource?)> IconCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Mod/paketin kendi ikonu. Mod: neoforge.mods.toml / mods.toml "logoFile" ya da fabric.mod.json "icon";
        /// kaynak paketi ve gölgelendirici: pack.png. Bulunamazsa null (arayüz ikon kutusunu gizler).
        /// Arka plan iş parçacığından çağrılabilir; dönen görsel donmuş (Freeze) ve küçük boyutta çözülmüştür.
        /// </summary>
        public static ImageSource? ReadIcon(string archivePath, ContentKind kind)
        {
            try
            {
                if (!File.Exists(archivePath)) return null;
                var stamp = File.GetLastWriteTimeUtc(archivePath);
                lock (IconCache)
                    if (IconCache.TryGetValue(archivePath, out var c) && c.Item1 == stamp) return c.Item2;

                ImageSource? img = null;
                using (var zip = ZipFile.OpenRead(archivePath))
                {
                    var entry = kind == ContentKind.Mod ? ModIconEntry(zip) : null;
                    entry ??= zip.GetEntry("pack.png");
                    if (entry != null && entry.Length is > 0 and < 4_000_000)
                    {
                        using var ms = new MemoryStream();
                        using (var s = entry.Open()) s.CopyTo(ms);
                        ms.Position = 0;
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.DecodePixelWidth = 96;
                        bmp.StreamSource = ms;
                        bmp.EndInit();
                        bmp.Freeze();
                        img = bmp;
                    }
                }
                lock (IconCache) IconCache[archivePath] = (stamp, img);
                return img;
            }
            catch
            {
                return null;
            }
        }

        private static ZipArchiveEntry? ModIconEntry(ZipArchive zip)
        {
            var toml = zip.GetEntry("META-INF/neoforge.mods.toml") ?? zip.GetEntry("META-INF/mods.toml");
            if (toml != null)
            {
                using var r = new StreamReader(toml.Open());
                var m = Regex.Match(r.ReadToEnd(), "(?m)^\\s*logoFile\\s*=\\s*[\"']([^\"']+)[\"']");
                if (m.Success && zip.GetEntry(m.Groups[1].Value.TrimStart('/')) is { } e) return e;
            }
            var fabric = zip.GetEntry("fabric.mod.json") ?? zip.GetEntry("quilt.mod.json");
            if (fabric != null)
            {
                using var s = fabric.Open();
                using var doc = JsonDocument.Parse(s, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                var root = doc.RootElement;
                if (root.TryGetProperty("quilt_loader", out var q) && q.TryGetProperty("metadata", out var qm)) root = qm;
                if (root.TryGetProperty("icon", out var icon))
                {
                    // "icon": "assets/x/icon.png"  ya da  { "16": "...", "128": "..." } (en büyüğü seçilir)
                    string? path = icon.ValueKind == JsonValueKind.String ? icon.GetString()
                        : icon.ValueKind == JsonValueKind.Object
                            ? icon.EnumerateObject().OrderByDescending(p => int.TryParse(p.Name, out var n) ? n : 0).Select(p => p.Value.GetString()).FirstOrDefault()
                            : null;
                    if (!string.IsNullOrWhiteSpace(path) && zip.GetEntry(path.TrimStart('/')) is { } e) return e;
                }
            }
            return null;
        }

        // ===================== YARDIMCILAR =====================
        public static string Sha1Of(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
            }
            catch { return ""; }
        }

        public static string Sha512Of(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                return Convert.ToHexString(SHA512.HashData(fs)).ToLowerInvariant();
            }
            catch { return ""; }
        }

        private static void TryDelete(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); }
            catch (Exception ex) { Logger.Warn($"Silinemedi ({p}): {ex.Message}"); }
        }
    }
}
