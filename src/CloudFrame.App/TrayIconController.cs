using System;
using System.Drawing;
using System.Windows.Forms;
using CloudFrame.App.Engine;
using CloudFrame.Core.Config;

namespace CloudFrame.App
{
    internal sealed class TrayIconController : IDisposable
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly ToolStripMenuItem _pauseItem;
        private readonly ToolStripMenuItem _nextItem;
        private readonly ToolStripMenuItem _hideItem;
        private readonly ToolStripMenuItem _alwaysOnTopItem;
        private readonly SlideshowForm _form;
        private readonly SlideshowEngine? _engine;  // null when no accounts configured
        private bool _paused;
        private bool _disposed;

        public TrayIconController(
            SlideshowForm form,
            SlideshowEngine? engine,
            SettingsService settingsService,
            AppSettings settings)
        {
            _form = form;
            _engine = engine;

            _pauseItem = new ToolStripMenuItem("Pause", null, OnPauseClick)
            {
                Enabled = engine is not null
            };
            _nextItem = new ToolStripMenuItem("Next image", null, OnNextClick)
            {
                Enabled = engine is not null
            };
            _hideItem = new ToolStripMenuItem("Hide this image", null, OnHideClick)
            {
                Enabled = false   // enabled once the first slide is shown
            };
            _alwaysOnTopItem = new ToolStripMenuItem("Always on top", null, OnAlwaysOnTopClick)
            {
                CheckOnClick = false,  // we manage the check mark manually
                Checked = false        // updated when the menu opens
            };

            var menu = new ContextMenuStrip();
            menu.Opening += (_, _) => _alwaysOnTopItem.Checked = _form.TopMost;
            menu.Items.Add(_nextItem);
            menu.Items.Add(_pauseItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_alwaysOnTopItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_hideItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Settings\u2026", null, (_, _) => _form.ShowSettingsDialog());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => Application.Exit());

            _notifyIcon = new NotifyIcon
            {
                Text = "CloudFrame",
                Icon = LoadIcon(),
                ContextMenuStrip = menu,
                Visible = true
            };

            _notifyIcon.DoubleClick += OnNextClick;
        }

        public void ShowBalloon(string title, string message, ToolTipIcon icon)
            => _notifyIcon.ShowBalloonTip(4000, title, message, icon);

        /// <summary>The context menu shown on the tray icon right-click.</summary>
        public ContextMenuStrip Menu => _notifyIcon.ContextMenuStrip!;

        /// <summary>
        /// Called by <see cref="SlideshowForm"/> when a new slide is displayed so
        /// the Hide menu item can be enabled with the current image's name.
        /// </summary>
        public void OnSlideChanged(string imageName)
        {
            _hideItem.Enabled = true;
            _hideItem.Text = $"Hide \"{imageName}\"";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _pauseItem.Dispose();
            _nextItem.Dispose();
            _hideItem.Dispose();
            _alwaysOnTopItem.Dispose();
        }

        private void OnNextClick(object? sender, EventArgs e)
        {
            if (_engine is not null) _ = _engine.NextSlideAsync();
        }

        private void OnHideClick(object? sender, EventArgs e)
        {
            _form.HideCurrentImageAsync();
        }

        private void OnAlwaysOnTopClick(object? sender, EventArgs e)
        {
            _form.ToggleAlwaysOnTop();
        }

        private void OnPauseClick(object? sender, EventArgs e)
        {
            if (_engine is null) return;
            _paused = !_paused;
            _pauseItem.Text = _paused ? "Resume" : "Pause";
            if (_paused)
            {
                _ = _engine.PauseAsync();
                ScreenWakeService.Release();
            }
            else
            {
                _ = _engine.ResumeAsync();
                ScreenWakeService.KeepAwake();
            }
        }

        private static Icon LoadIcon()
        {
            try
            {
                string iconPath = System.IO.Path.Combine(
                    AppContext.BaseDirectory, "Resources", "cloudframe.ico");
                if (System.IO.File.Exists(iconPath))
                    return new Icon(iconPath, 16, 16);
            }
            catch { }

            return GenerateFallbackIcon();
        }

        private static Icon GenerateFallbackIcon()
        {
            using var bmp = new Bitmap(16, 16);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.FromArgb(24, 90, 189));
            using var font = new Font("Arial", 5f, FontStyle.Bold);
            g.DrawString("CF", font, Brushes.White, 1f, 4f);
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}