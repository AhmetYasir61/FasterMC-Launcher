using System;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace FWLauncherV2.Services
{
    /// <summary>
    /// Sistem tepsisi (saatin yanı) simgesi. Oyun açıkken launcher gizlendiğinde buradan geri açılabilir.
    /// Çift tık: launcher'ı göster. Sağ tık: Göster · Oyunu kapat · Çıkış.
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly WinForms.NotifyIcon _icon;
        private readonly WinForms.ToolStripMenuItem _killGame;
        private readonly Action _show;
        private bool _balloonShown;

        public TrayIcon(Action show, Action killGame, Action exit)
        {
            _show = show;
            _icon = new WinForms.NotifyIcon
            {
                Text = "FasterMC Launcher",
                Icon = LoadIcon(),
                Visible = true
            };
            _icon.DoubleClick += (s, e) => _show();
            _icon.BalloonTipClicked += (s, e) => _show();

            var menu = new WinForms.ContextMenuStrip { ShowImageMargin = false };
            menu.Items.Add("Launcher'ı göster", null, (s, e) => _show());
            _killGame = new WinForms.ToolStripMenuItem("Oyunu kapat", null, (s, e) => killGame()) { Visible = false };
            menu.Items.Add(_killGame);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Çıkış", null, (s, e) => exit());
            menu.Opening += (s, e) => _killGame.Visible = Views.MainView.RunningGame != null;
            _icon.ContextMenuStrip = menu;
        }

        private static System.Drawing.Icon LoadIcon()
        {
            try
            {
                var info = Application.GetResourceStream(new Uri("pack://application:,,,/icon/FWLauncherV2.ico"));
                if (info?.Stream != null) return new System.Drawing.Icon(info.Stream);
            }
            catch { }
            try { return System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)!; }
            catch { return System.Drawing.SystemIcons.Application; }
        }

        /// <summary>Launcher gizlendiğinde (oturumda bir kez) bilgi balonu gösterir.</summary>
        public void NotifyHidden()
        {
            if (_balloonShown) return;
            _balloonShown = true;
            try
            {
                _icon.ShowBalloonTip(4000, "FasterMC Launcher arka planda",
                    "Oyun kapanınca launcher geri gelecek. Şimdi açmak için bu simgeye çift tıkla.", WinForms.ToolTipIcon.Info);
            }
            catch { }
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}
