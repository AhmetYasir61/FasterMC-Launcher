using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace FWLauncherV2.Services
{
    /// <summary>Hesabın Minecraft: Java Edition'a sahip olmadığı için Horizon'ın başlatılmadığı durum.</summary>
    public sealed class NotOwnerException : Exception
    {
        public NotOwnerException(string message) : base(message) { }
    }

    /// <summary>
    /// Horizon'ın sahip kapısı (docs/microsoft-account.md, docs/api/launch.md): oyun yalnızca Minecraft: Java Edition'a
    /// sahip bir Microsoft hesabıyla başlar. Giriş cihaz kodu akışıyla yapılır. Yenileme belirteci Windows DPAPI ile
    /// şifrelenip bu kullanıcıya bağlı saklanır. FireWing hesabı (site/mağaza girişi) bundan ayrıdır.
    /// </summary>
    public static class MicrosoftAccount
    {
        private const string Tenant = "https://login.microsoftonline.com/consumers/oauth2/v2.0";
        private const string Scope = "XboxLive.signin offline_access";

        /// <summary>
        /// FireWing'in kendi Azure uygulama kimliği: Mojang onayından sonra buraya yazılır. Başka bir launcher'ın kimliği
        /// kullanılamaz. Geliştirme için ortam değişkeni FIREWING_MSA_CLIENT_ID önceliklidir.
        /// </summary>
        private const string BuiltInClientId = "";

        private static string TokenFile => Path.Combine(SettingsService.LauncherDirectory, "msa.dat");

        public static string? ClientId
        {
            get
            {
                string? env = Environment.GetEnvironmentVariable("FIREWING_MSA_CLIENT_ID");
                string id = string.IsNullOrWhiteSpace(env) ? BuiltInClientId : env.Trim();
                return id.Length == 0 ? null : id;
            }
        }

        private sealed class Stored
        {
            public string RefreshToken { get; set; } = "";
            public bool Owns { get; set; }
            public string Name { get; set; } = "";
        }

        /// <summary>
        /// Sahip hesabın oturumu. Giriş yoksa cihaz kodu gösterilir. Ağ yoksa daha önce sahip olduğu doğrulanmış hesap
        /// çevrimdışı oynar (dönüş null). Sahip olmayan hesapta NotOwnerException atılır.
        /// </summary>
        public static async Task<HorizonSession?> EnsureOwnerAsync(Action<string, string> showCode)
        {
            string clientId = ClientId ?? throw new NotOwnerException(
                "Bu launcher sürümünde Microsoft girişi henüz ayarlanmadı (Azure uygulama kimliği yok). " +
                "Horizon yalnızca Minecraft: Java Edition'a sahip bir hesapla başlar.");

            var stored = Load();
            string microsoftToken;
            try
            {
                microsoftToken = stored != null
                    ? await RefreshAsync(clientId, stored)
                    : await DeviceCodeAsync(clientId, showCode);
            }
            catch (HttpRequestException ex) when (stored is { Owns: true })
            {
                Logger.Warn($"Microsoft hesabı denetlenemedi, çevrimdışı başlıyor: {ex.Message}");
                return null;
            }

            try
            {
                return await MinecraftSessionAsync(microsoftToken, Load() ?? throw new Exception("Oturum kaydedilemedi."));
            }
            catch (HttpRequestException ex) when (Load() is { Owns: true })
            {
                Logger.Warn($"Minecraft hizmetlerine ulaşılamadı, çevrimdışı başlıyor: {ex.Message}");
                return null;
            }
        }

        public static void SignOut()
        {
            if (File.Exists(TokenFile)) File.Delete(TokenFile);
        }

        private static async Task<string> DeviceCodeAsync(string clientId, Action<string, string> showCode)
        {
            var start = await PostFormAsync($"{Tenant}/devicecode", new() { ["client_id"] = clientId, ["scope"] = Scope });
            string deviceCode = start["device_code"]!.GetValue<string>();
            int interval = start["interval"]?.GetValue<int>() ?? 5;
            int expires = start["expires_in"]?.GetValue<int>() ?? 900;
            showCode(start["user_code"]!.GetValue<string>(), start["verification_uri"]!.GetValue<string>());

            var until = DateTime.UtcNow.AddSeconds(expires);
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval));
                using var response = await HttpClientProvider.Api.PostAsync($"{Tenant}/token", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["device_code"] = deviceCode,
                }));
                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
                if (response.IsSuccessStatusCode) return Keep(body, null);
                switch (body["error"]?.GetValue<string>())
                {
                    case "authorization_pending": break;
                    case "slow_down": interval += 5; break;
                    case "authorization_declined": throw new NotOwnerException("Microsoft girişi reddedildi.");
                    default: throw new NotOwnerException("Microsoft girişi zaman aşımına uğradı; tekrar dene.");
                }
            }
            throw new NotOwnerException("Microsoft girişi zaman aşımına uğradı; tekrar dene.");
        }

        private static async Task<string> RefreshAsync(string clientId, Stored stored)
        {
            using var response = await HttpClientProvider.Api.PostAsync($"{Tenant}/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = stored.RefreshToken,
                ["scope"] = Scope,
            }));
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            if (response.IsSuccessStatusCode) return Keep(body, stored);
            if (body["error"]?.GetValue<string>() == "invalid_grant")
            {
                SignOut();
                throw new NotOwnerException("Microsoft oturumunun süresi doldu. OYNA'ya tekrar basıp giriş yap.");
            }
            throw new HttpRequestException($"Microsoft belirteci yenilenemedi ({(int)response.StatusCode}).");
        }

        private static string Keep(JsonNode body, Stored? stored)
        {
            Save(new Stored
            {
                RefreshToken = body["refresh_token"]?.GetValue<string>() ?? stored?.RefreshToken ?? "",
                Owns = stored?.Owns ?? false,
                Name = stored?.Name ?? "",
            });
            return body["access_token"]!.GetValue<string>();
        }

        private static async Task<HorizonSession> MinecraftSessionAsync(string microsoftToken, Stored stored)
        {
            var xbl = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", new JsonObject
            {
                ["Properties"] = new JsonObject
                {
                    ["AuthMethod"] = "RPS",
                    ["SiteName"] = "user.auth.xboxlive.com",
                    ["RpsTicket"] = "d=" + microsoftToken,
                },
                ["RelyingParty"] = "http://auth.xboxlive.com",
                ["TokenType"] = "JWT",
            });
            var xsts = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new JsonObject
            {
                ["Properties"] = new JsonObject
                {
                    ["SandboxId"] = "RETAIL",
                    ["UserTokens"] = new JsonArray(xbl["Token"]!.GetValue<string>()),
                },
                ["RelyingParty"] = "rp://api.minecraftservices.com/",
                ["TokenType"] = "JWT",
            });
            string hash = xsts["DisplayClaims"]!["xui"]![0]!["uhs"]!.GetValue<string>();
            var login = await PostJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox", new JsonObject
            {
                ["identityToken"] = $"XBL3.0 x={hash};{xsts["Token"]!.GetValue<string>()}",
            });
            string token = login["access_token"]!.GetValue<string>();

            var entitlements = await GetAsync("https://api.minecraftservices.com/entitlements/mcstore", token);
            bool owns = entitlements?["items"]?.AsArray().Any(i =>
                i?["name"]?.GetValue<string>() is "game_minecraft" or "product_minecraft") == true;
            if (!owns)
            {
                Save(new Stored { RefreshToken = stored.RefreshToken, Owns = false });
                throw new NotOwnerException("Bu Microsoft hesabı Minecraft: Java Edition'a sahip değil; Horizon başlatılamaz.");
            }

            var profile = await GetAsync("https://api.minecraftservices.com/minecraft/profile", token)
                ?? throw new NotOwnerException("Önce minecraft.net üzerinde Minecraft profilini oluştur.");
            string name = profile["name"]!.GetValue<string>();
            Save(new Stored { RefreshToken = stored.RefreshToken, Owns = true, Name = name });
            return new HorizonSession(name, profile["id"]!.GetValue<string>(), token);
        }

        private static async Task<JsonNode> PostFormAsync(string url, Dictionary<string, string> form)
        {
            using var response = await HttpClientProvider.Api.PostAsync(url, new FormUrlEncodedContent(form));
            response.EnsureSuccessStatusCode();
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        }

        private static async Task<JsonNode> PostJsonAsync(string url, JsonObject body)
        {
            using var response = await HttpClientProvider.Api.PostAsJsonAsync(url, body);
            string text = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.Forbidden && url.Contains("minecraftservices"))
                throw new NotOwnerException("Bu Azure uygulaması Mojang tarafından henüz onaylanmadı.");
            if (response.StatusCode == HttpStatusCode.Unauthorized && JsonNode.Parse(text)?["XErr"] is { } xerr)
                throw new NotOwnerException(XboxError(xerr.GetValue<long>()));
            response.EnsureSuccessStatusCode();
            return JsonNode.Parse(text)!;
        }

        private static async Task<JsonNode?> GetAsync(string url, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await HttpClientProvider.Api.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return JsonNode.Parse(await response.Content.ReadAsStringAsync());
        }

        private static string XboxError(long code) => code switch
        {
            2148916233 => "Bu Microsoft hesabının Xbox profili yok. xbox.com'da oluştur.",
            2148916235 => "Xbox Live bu ülkede kullanılamıyor.",
            2148916236 or 2148916237 => "Bu hesap için yetişkin doğrulaması gerekiyor.",
            2148916238 => "Çocuk hesabı: bir aileye eklenmesi gerekiyor.",
            _ => $"Xbox girişi reddedildi (XErr {code}).",
        };

        private static Stored? Load()
        {
            try
            {
                if (!File.Exists(TokenFile)) return null;
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(TokenFile), null, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<Stored>(plain);
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
            {
                Logger.Warn($"Microsoft oturumu okunamadı: {ex.Message}");
                return null;
            }
        }

        private static void Save(Stored stored)
        {
            byte[] plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(stored));
            File.WriteAllBytes(TokenFile, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
        }
    }
}
