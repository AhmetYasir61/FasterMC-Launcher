using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FWLauncherV2.Views;

namespace FWLauncherV2
{
    public partial class LoginWindow : Window
    {
        public LoginWindow()
        {
            InitializeComponent();
            MainWindow.ApplyPageTheme("login");
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            TxtVersion.Text = $"FasterMC Launcher {v?.Major}.{v?.Minor} · beta";

            // Açılış: yumuşak belirme
            Opacity = 0;
            Loaded += (s, e) => BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 1, System.TimeSpan.FromMilliseconds(300)));

            // Uygulama açıldığında ilk olarak LoginView göster
            ContentArea.Content = new LoginView(this);
        }

        public void SwitchToRegister()
        {
            TxtHero.Text = "Aramıza\nkatıl!";
            TxtHeroSub.Text = "Birkaç saniyede hesabını oluştur, FasterMC'ye adım at.";
            ContentArea.Content = new RegisterView(this);
        }

        public void SwitchToLogin()
        {
            TxtHero.Text = "Hoş\ngeldin!";
            TxtHeroSub.Text = "Giriş yap ve gerçek hayatta kalma havasına dal.";
            ContentArea.Content = new LoginView(this);
        }

        public void LoginSuccess(string username)
        {
            // Giriş başarılı → MainWindow'u aç, kullanıcı adını ilet
            MainWindow main = new MainWindow();
            main.NavigateToMainView(username);
            main.Show();

            // Login penceresini kapat
            this.Close();
        }

        // ✅ XAML'deki CloseButton_Click için
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        // Pencereyi boş alandan sürükle (form öğeleri hariç)
        private void TopBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is TextBox or PasswordBox or Button) return;
            if (e.ChangedButton == MouseButton.Left && e.LeftButton == MouseButtonState.Pressed)
            {
                try { this.DragMove(); } catch { }
            }
        }
    }
}
