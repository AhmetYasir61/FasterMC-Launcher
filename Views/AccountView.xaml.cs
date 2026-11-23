using FWLauncherV2.Dialogs;
using FWLauncherV2.Services;
using System.Windows;
using System.Windows.Controls;

namespace FWLauncherV2.Views
{
    /// <summary>Hesap: profil, coin bakiyesi, yerel istatistikler ve coin gönderme.</summary>
    public partial class AccountView : UserControl
    {
        private string _user = "Oyuncu";

        public AccountView()
        {
            InitializeComponent();
            UserSession.Changed += () => Dispatcher.BeginInvoke(RefreshProfile);
            LocalStatsService.Changed += () => Dispatcher.BeginInvoke(RefreshStats);
            Loaded += async (s, e) =>
            {
                RefreshProfile();
                RefreshStats();
                await UserSession.LoadAsync();   // bakiye taze gelsin
            };
            try { TxtHwid.Text = Mask(HardwareId.Get()); } catch { }
        }

        public void SetUser(string user)
        {
            _user = string.IsNullOrWhiteSpace(user) ? "Oyuncu" : user;
            RefreshProfile();
            RefreshStats();
        }

        private static string Mask(string s) => s.Length <= 8 ? s : s[..4] + "…" + s[^4..];

        private void RefreshProfile()
        {
            TxtName.Text = _user;
            TxtInitial.Text = char.ToUpperInvariant(_user[0]).ToString();
            PlayerHead.Apply(ImgHead, TxtInitial, _user);
            TxtRank.Text = UserSession.RankText;
            TxtCoins.Text = UserSession.Coins.ToString("N0");
        }

        private void RefreshStats()
        {
            var s = LocalStatsService.Load(_user);
            TxtPlayTime.Text = LocalStatsService.FormatHours(s.TotalPlaySeconds);
            TxtLaunches.Text = s.LaunchCount.ToString("N0");
            TxtLastSession.Text = s.LastSessionSeconds > 0 ? LocalStatsService.FormatHours(s.LastSessionSeconds) : "—";
            TxtFirstSeen.Text = s.FirstSeen?.ToString("dd.MM.yy") ?? "—";
            TxtFirstSeen.ToolTip = s.FirstSeen?.ToString("dd MMMM yyyy HH:mm");
            TxtLastLogin.Text = LocalStatsService.FormatRelative(s.LastLogin);
            TxtSince.Text = LocalStatsService.FormatSince(s.FirstSeen);
        }

        // ===================== EYLEMLER =====================
        private string? StoreUrl()
        {
            var l = (Window.GetWindow(this) as MainWindow)?.LinkSettings;
            return !string.IsNullOrWhiteSpace(l?.StoreUrl) ? l!.StoreUrl : l?.WebsiteUrl;
        }

        private void BuyRank_Click(object sender, RoutedEventArgs e) => Links.Open(StoreUrl(), "Mağaza (rütbe) adresi");
        private void TopUp_Click(object sender, RoutedEventArgs e) => Links.Open(StoreUrl(), "Bakiye yükleme adresi");

        private void Transfer_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new TransferDialog { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true) RefreshProfile();
        }
    }
}
