using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;

namespace FWLauncherV2.Services
{
    public enum StoreSource { Modrinth, CurseForge }

    /// <summary>Mağaza kartı.</summary>
    public class StoreItem
    {
        public StoreSource Source { get; set; }
        public string SourceKey => Source == StoreSource.Modrinth ? "modrinth" : "curseforge";
        public string ProjectId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public long Downloads { get; set; }
        public string? IconUrl { get; set; }
        public ContentKind Kind { get; set; }
        public Brush HeaderBrush { get; set; } = Brushes.Transparent;
        public bool Installed { get; set; }

        public string Icon => string.IsNullOrWhiteSpace(IconUrl) ? "pack://application:,,,/Assets/Images/icon_chest.png" : IconUrl!;
        public string DownloadsText => Downloads >= 1_000_000 ? $"{Downloads / 1_000_000.0:0.#}M indirme"
                                     : Downloads >= 1_000 ? $"{Downloads / 1_000.0:0.#}B indirme" : $"{Downloads} indirme";
        public string ButtonText => Installed ? "Kuruldu ✓" : "Kur";
    }

    /// <summary>Kurulacak tek bir dosya (seçilen uyumlu sürüm).</summary>
    public class StoreFile
    {
        public string Url { get; set; } = "";
        public string FileName { get; set; } = "";
        public string? Sha1 { get; set; }
        public string? Sha512 { get; set; }
        public string Version { get; set; } = "";
        public List<string> RequiredDeps { get; } = new();
    }

    /// <summary>
    /// Mod mağazası akışı: 1) ara → 2) API isteği → 3) profilin MC sürümü + yükleyicisiyle eşleşen dosyayı seç →
    /// 4) indir + hash doğrula + gerekli bağımlılıkları kur → 5) yalnızca kullanıcı klasörlerine yaz (usermods/ vb.).
    /// Modrinth anahtarsızdır; CurseForge launcher'ın gömülü anahtarını (veya Ayarlar'daki anahtarı) kullanır.
    /// </summary>
    public static class StoreService
    {
        private const string ModrinthApi = "https://api.modrinth.com/v2/";
        private const string CurseApi = "https://api.curseforge.com/v1/";
        private const string EmbeddedCurseKey = Gizli.CurseForgeApiKey;   // Gizli/Gizli.local.cs (git disi)

        private static readonly Color[] Palette =
        {
            Color.FromRgb(0x22, 0x9A, 0x5A), Color.FromRgb(0x2F, 0x6B, 0xEE), Color.FromRgb(0x9B, 0x4D, 0xF5),
            Color.FromRgb(0xE0, 0x8A, 0x0B), Color.FromRgb(0xD9, 0x3A, 0x8C), Color.FromRgb(0x14, 0xA3, 0x94)
        };

        private static string CurseKey()
        {
            var k = SettingsService.Load().CurseForgeApiKey;
            return string.IsNullOrWhiteSpace(k) ? EmbeddedCurseKey : k.Trim();
        }

        private static Brush Header(int index, int? rgb = null)
        {
            var c = rgb is int v && v > 0 ? Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v) : Palette[index % Palette.Length];
            var dark = Color.FromRgb((byte)(c.R * 0.25), (byte)(c.G * 0.25), (byte)(c.B * 0.3));
            var b = new LinearGradientBrush(c, dark, 30);
            b.Freeze();
            return b;
        }

        // ===================== ARAMA =====================
        public static Task<List<StoreItem>> SearchAsync(StoreSource src, ContentKind kind, string query)
            => src == StoreSource.Modrinth ? SearchModrinthAsync(kind, query) : SearchCurseAsync(kind, query);

        private static async Task<List<StoreItem>> SearchModrinthAsync(ContentKind kind, string query)
        {
            string type = kind switch { ContentKind.Mod => "mod", ContentKind.Shader => "shader", _ => "resourcepack" };
            var facets = new List<string> { $"[\"project_type:{type}\"]" };
            if (!string.IsNullOrWhiteSpace(PackContext.McVersion)) facets.Add($"[\"versions:{PackContext.McVersion}\"]");
            if (kind == ContentKind.Mod && !string.IsNullOrWhiteSpace(PackContext.LoaderKey)) facets.Add($"[\"categories:{PackContext.LoaderKey}\"]");
            string url = $"{ModrinthApi}search?limit=24&index={(string.IsNullOrWhiteSpace(query) ? "downloads" : "relevance")}" +
                         $"&query={Uri.EscapeDataString(query ?? "")}&facets={Uri.EscapeDataString("[" + string.Join(",", facets) + "]")}";

            using var doc = JsonDocument.Parse(await HttpClientProvider.Api.GetStringAsync(url));
            var list = new List<StoreItem>();
            int i = 0;
            foreach (var h in doc.RootElement.GetProperty("hits").EnumerateArray())
            {
                list.Add(new StoreItem
                {
                    Source = StoreSource.Modrinth,
                    Kind = kind,
                    ProjectId = h.GetProperty("project_id").GetString() ?? "",
                    Title = h.GetProperty("title").GetString() ?? "",
                    Description = h.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    Downloads = h.TryGetProperty("downloads", out var dl) ? dl.GetInt64() : 0,
                    IconUrl = h.TryGetProperty("icon_url", out var ic) && ic.ValueKind == JsonValueKind.String ? ic.GetString() : null,
                    HeaderBrush = Header(i, h.TryGetProperty("color", out var col) && col.ValueKind == JsonValueKind.Number ? col.GetInt32() : null)
                });
                i++;
            }
            return list;
        }

        private static int CurseClass(ContentKind k) => k switch { ContentKind.Mod => 6, ContentKind.Shader => 6552, _ => 12 };
        private static int CurseLoader() => PackContext.LoaderKey switch { "forge" => 1, "fabric" => 4, "quilt" => 5, "neoforge" => 6, _ => 0 };

        private static HttpRequestMessage CurseRequest(string url)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("x-api-key", CurseKey());
            req.Headers.Add("Accept", "application/json");
            return req;
        }

        private static async Task<JsonDocument> CurseGetAsync(string url)
        {
            using var resp = await HttpClientProvider.Api.SendAsync(CurseRequest(url));
            if ((int)resp.StatusCode == 403)
                throw new Exception("CurseForge API anahtarı geçersiz. Ayarlar → Modlar bölümünden kendi anahtarını girebilirsin.");
            resp.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        }

        private static async Task<List<StoreItem>> SearchCurseAsync(ContentKind kind, string query)
        {
            string url = $"{CurseApi}mods/search?gameId=432&classId={CurseClass(kind)}&pageSize=24&sortOrder=desc" +
                         $"&sortField={(string.IsNullOrWhiteSpace(query) ? 6 : 2)}&searchFilter={Uri.EscapeDataString(query ?? "")}";
            if (!string.IsNullOrWhiteSpace(PackContext.McVersion)) url += "&gameVersion=" + Uri.EscapeDataString(PackContext.McVersion);
            if (kind == ContentKind.Mod && CurseLoader() > 0) url += "&modLoaderType=" + CurseLoader();

            using var doc = await CurseGetAsync(url);
            var list = new List<StoreItem>();
            int i = 0;
            foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                string? icon = null;
                if (m.TryGetProperty("logo", out var logo) && logo.ValueKind == JsonValueKind.Object &&
                    logo.TryGetProperty("thumbnailUrl", out var t)) icon = t.GetString();
                list.Add(new StoreItem
                {
                    Source = StoreSource.CurseForge,
                    Kind = kind,
                    ProjectId = m.GetProperty("id").GetInt32().ToString(),
                    Title = m.GetProperty("name").GetString() ?? "",
                    Description = m.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
                    Downloads = m.TryGetProperty("downloadCount", out var dc) ? (long)dc.GetDouble() : 0,
                    IconUrl = icon,
                    HeaderBrush = Header(i)
                });
                i++;
            }
            return list;
        }

        // ===================== UYUMLU DOSYA SEÇİMİ =====================
        public static Task<StoreFile?> ResolveFileAsync(StoreSource src, ContentKind kind, string projectId)
            => src == StoreSource.Modrinth ? ResolveModrinthAsync(kind, projectId) : ResolveCurseAsync(kind, projectId);

        private static async Task<StoreFile?> ResolveModrinthAsync(ContentKind kind, string projectId)
        {
            string url = $"{ModrinthApi}project/{Uri.EscapeDataString(projectId)}/version";
            var q = new List<string>();
            if (!string.IsNullOrWhiteSpace(PackContext.McVersion)) q.Add("game_versions=" + Uri.EscapeDataString($"[\"{PackContext.McVersion}\"]"));
            if (kind == ContentKind.Mod && !string.IsNullOrWhiteSpace(PackContext.LoaderKey)) q.Add("loaders=" + Uri.EscapeDataString($"[\"{PackContext.LoaderKey}\"]"));
            if (q.Count > 0) url += "?" + string.Join("&", q);

            using var doc = JsonDocument.Parse(await HttpClientProvider.Api.GetStringAsync(url));
            var ver = doc.RootElement.EnumerateArray().FirstOrDefault();
            if (ver.ValueKind != JsonValueKind.Object) return null;

            var files = ver.GetProperty("files").EnumerateArray().ToList();
            if (files.Count == 0) return null;
            var f = files.FirstOrDefault(x => x.TryGetProperty("primary", out var p) && p.GetBoolean());
            if (f.ValueKind != JsonValueKind.Object) f = files[0];

            var sf = new StoreFile
            {
                Url = f.GetProperty("url").GetString() ?? "",
                FileName = f.GetProperty("filename").GetString() ?? "",
                Version = ver.TryGetProperty("version_number", out var vn) ? vn.GetString() ?? "" : ""
            };
            if (f.TryGetProperty("hashes", out var h))
            {
                if (h.TryGetProperty("sha1", out var s1)) sf.Sha1 = s1.GetString();
                if (h.TryGetProperty("sha512", out var s5)) sf.Sha512 = s5.GetString();
            }
            if (ver.TryGetProperty("dependencies", out var deps))
                foreach (var d in deps.EnumerateArray())
                    if (d.TryGetProperty("dependency_type", out var dt) && dt.GetString() == "required" &&
                        d.TryGetProperty("project_id", out var pid) && pid.ValueKind == JsonValueKind.String)
                        sf.RequiredDeps.Add(pid.GetString()!);
            return sf;
        }

        private static async Task<StoreFile?> ResolveCurseAsync(ContentKind kind, string projectId)
        {
            string url = $"{CurseApi}mods/{projectId}/files?pageSize=30";
            if (!string.IsNullOrWhiteSpace(PackContext.McVersion)) url += "&gameVersion=" + Uri.EscapeDataString(PackContext.McVersion);
            if (kind == ContentKind.Mod && CurseLoader() > 0) url += "&modLoaderType=" + CurseLoader();

            using var doc = await CurseGetAsync(url);
            var file = doc.RootElement.GetProperty("data").EnumerateArray()
                .OrderByDescending(x => x.TryGetProperty("fileDate", out var fd) ? fd.GetString() : "")
                .FirstOrDefault();
            if (file.ValueKind != JsonValueKind.Object) return null;

            int fileId = file.GetProperty("id").GetInt32();
            string fileName = file.GetProperty("fileName").GetString() ?? $"{fileId}.jar";
            string? dl = file.TryGetProperty("downloadUrl", out var du) && du.ValueKind == JsonValueKind.String ? du.GetString() : null;
            // Üçüncü taraf dağıtımı kapalı dosyalarda downloadUrl null gelir → CDN yolu denenir.
            dl ??= $"https://edge.forgecdn.net/files/{fileId / 1000}/{fileId % 1000}/{Uri.EscapeDataString(fileName)}";

            var sf = new StoreFile
            {
                Url = dl,
                FileName = fileName,
                Version = file.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : ""
            };
            if (file.TryGetProperty("hashes", out var hs))
                foreach (var h in hs.EnumerateArray())
                    if (h.TryGetProperty("algo", out var a) && a.GetInt32() == 1) sf.Sha1 = h.GetProperty("value").GetString();
            if (file.TryGetProperty("dependencies", out var deps))
                foreach (var d in deps.EnumerateArray())
                    if (d.TryGetProperty("relationType", out var rt) && rt.GetInt32() == 3)
                        sf.RequiredDeps.Add(d.GetProperty("modId").GetInt32().ToString());
            return sf;
        }

        // ===================== KURULUM =====================
        /// <summary>Seçili öğeyi (ve gerekli bağımlılıklarını) kurar. Kurulan öğe adlarını döndürür.</summary>
        public static async Task<List<string>> InstallAsync(StoreItem item, IProgress<string>? progress = null)
        {
            var installed = new List<string>();
            var visited = new HashSet<string>();
            await InstallRecursiveAsync(item.Source, item.Kind, item.ProjectId, item.Title, item.Description, item.IconUrl, installed, visited, progress, 0);
            return installed;
        }

        private static async Task InstallRecursiveAsync(StoreSource src, ContentKind kind, string projectId, string? title,
            string? desc, string? icon, List<string> installed, HashSet<string> visited, IProgress<string>? progress, int depth)
        {
            if (!visited.Add(projectId) || depth > 4) return;
            string packDir = PackContext.PackDir;
            string sourceKey = src == StoreSource.Modrinth ? "modrinth" : "curseforge";

            // bağımlılık zaten kurulu ise (kullanıcı tarafında) atla
            if (depth > 0 && UserContentService.IsInstalled(packDir, sourceKey, projectId)) return;

            progress?.Report($"Uyumlu sürüm aranıyor: {title ?? projectId}");
            var file = await ResolveFileAsync(src, kind, projectId)
                ?? throw new Exception($"{title ?? projectId}: {PackContext.McVersion} {PackContext.LoaderDisplay} için uyumlu dosya bulunamadı.");

            if (depth > 0 && title == null)
                (title, desc, icon) = await FetchMetaAsync(src, projectId);

            // ana pakette zaten olan bağımlılığı indirme (aynı dosya adı)
            if (depth > 0 && File.Exists(Path.Combine(packDir, "mods", file.FileName))) return;

            progress?.Report($"İndiriliyor: {file.FileName}");
            var tmpDir = Path.Combine(SettingsService.LauncherDirectory, "cache", "downloads");
            Directory.CreateDirectory(tmpDir);
            var tmp = Path.Combine(tmpDir, Guid.NewGuid().ToString("N") + Path.GetExtension(file.FileName));
            try
            {
                using (var resp = await HttpClientProvider.Download.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var fs = File.Create(tmp);
                    await resp.Content.CopyToAsync(fs);
                }

                progress?.Report("Hash doğrulanıyor...");
                if (!string.IsNullOrWhiteSpace(file.Sha512) &&
                    !string.Equals(UserContentService.Sha512Of(tmp), file.Sha512, StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"{file.FileName}: hash doğrulaması başarısız (SHA-512).");
                if (string.IsNullOrWhiteSpace(file.Sha512) && !string.IsNullOrWhiteSpace(file.Sha1) &&
                    !string.Equals(UserContentService.Sha1Of(tmp), file.Sha1, StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"{file.FileName}: hash doğrulaması başarısız (SHA-1).");

                UserContentService.AddFile(packDir, tmp, kind, new UserContentEntry
                {
                    File = file.FileName,
                    Name = title ?? Path.GetFileNameWithoutExtension(file.FileName),
                    Source = sourceKey,
                    ProjectId = projectId,
                    Version = file.Version,
                    Sha1 = file.Sha1?.ToLowerInvariant(),
                    Description = desc,
                    IconUrl = icon,
                    Enabled = true
                });
                installed.Add(title ?? file.FileName);
                Logger.Info($"Mağazadan kuruldu: {file.FileName} ({sourceKey}:{projectId})");
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }

            foreach (var dep in file.RequiredDeps)
                await InstallRecursiveAsync(src, ContentKind.Mod, dep, null, null, null, installed, visited, progress, depth + 1);
        }

        private static async Task<(string?, string?, string?)> FetchMetaAsync(StoreSource src, string projectId)
        {
            try
            {
                if (src == StoreSource.Modrinth)
                {
                    using var doc = JsonDocument.Parse(await HttpClientProvider.Api.GetStringAsync($"{ModrinthApi}project/{projectId}"));
                    var r = doc.RootElement;
                    return (r.GetProperty("title").GetString(), r.TryGetProperty("description", out var d) ? d.GetString() : null,
                            r.TryGetProperty("icon_url", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null);
                }
                else
                {
                    using var doc = await CurseGetAsync($"{CurseApi}mods/{projectId}");
                    var r = doc.RootElement.GetProperty("data");
                    string? icon = r.TryGetProperty("logo", out var l) && l.ValueKind == JsonValueKind.Object ? l.GetProperty("thumbnailUrl").GetString() : null;
                    return (r.GetProperty("name").GetString(), r.TryGetProperty("summary", out var s) ? s.GetString() : null, icon);
                }
            }
            catch { return (null, null, null); }
        }
    }
}
