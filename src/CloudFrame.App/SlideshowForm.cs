using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading.Tasks;
using System.Windows.Forms;
using CloudFrame.App.Engine;
using CloudFrame.Core.Cloud;
using CloudFrame.Core.Config;
using CloudFrame.Core.Index;
using CloudFrame.Providers.OneDrive;

namespace CloudFrame.App
{
    /// <summary>
    /// Fullscreen slideshow window. Also owns the startup sequence — all async
    /// initialisation runs in OnLoad so the WinForms message loop (Application.Run)
    /// is already active and the STA thread is correct before any async work begins.
    /// </summary>
    public sealed class SlideshowForm : Form
    {
        // ── Services (built in OnLoad) ─────────────────────────────────────────
        private AppSettings _settings = new();
        private SettingsService _settingsService = new();
        private SlideshowEngine? _engine;
        private TrayIconController? _tray;
        private IndexService? _indexService;

        // ── Current slide entry (for Hide) ────────────────────────────────────
        private CloudFrame.Core.Cloud.CloudImageEntry? _currentEntry;

        // ── Rendering state ────────────────────────────────────────────────────
        private Bitmap? _currentBitmap;
        private Bitmap? _incomingBitmap;
        private float _fadeAlpha;
        private const int FadeSteps = 30;
        private readonly System.Windows.Forms.Timer _fadeTimer;
        private readonly System.Windows.Forms.Timer _overlayTimer;
        private string _overlayText = string.Empty;
        private readonly StatusWindow _statusWindow = new StatusWindow();
        // ── Pause / click state ────────────────────────────────────────────────
        private bool _slideshowPaused;
        // Distinguishes a single click (pause) from a double-click (next slide).
        // On first click the timer starts; if a second click arrives before it
        // fires we treat the pair as a double-click and cancel the pause action.
        private readonly System.Windows.Forms.Timer _clickTimer;
        // ── Controls ───────────────────────────────────────────────────────────
        private readonly SlidePanel _panel;

        public SlideshowForm()
        {
            // ── Form setup ─────────────────────────────────────────────────────
            Text = "CloudFrame";
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            BackColor = Color.Black;
            TopMost = false;      // will be set true after settings if accounts exist
            ShowInTaskbar = false;
            Cursor = Cursors.Default;
            Opacity = 0;          // invisible until first image is ready

            // ── Slide panel ────────────────────────────────────────────────────
            _panel = new SlidePanel { Dock = DockStyle.Fill };
            _panel.Paint += OnPanelPaint;
            _panel.Click += OnPanelClick;
            _panel.DoubleClick += OnPanelDoubleClick;
            _panel.MouseClick += OnPanelMouseClick;
            Controls.Add(_panel);

            // ── Fade timer ─────────────────────────────────────────────────────
            _fadeTimer = new System.Windows.Forms.Timer { Interval = 16 }; // ~60fps
            _fadeTimer.Tick += OnFadeTick;

            // ── Overlay timer ──────────────────────────────────────────────────
            _overlayTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _overlayTimer.Tick += (_, _) =>
            {
                _overlayTimer.Stop();
                _overlayText = string.Empty;
                _panel.Invalidate();
            };

            // ── Click timer (single vs double click discrimination) ────────────
            // SystemInformation.DoubleClickTime is the OS double-click threshold.
            _clickTimer = new System.Windows.Forms.Timer
            {
                Interval = SystemInformation.DoubleClickTime + 50
            };
            _clickTimer.Tick += OnClickTimerTick;

            // ── Keyboard ───────────────────────────────────────────────────────
            KeyDown += OnKeyDown;
            KeyPreview = true;

            // ── Startup ────────────────────────────────────────────────────────
            Load += OnLoad;
        }

        // ── Startup sequence (runs on UI thread, message loop already active) ──

        private void OnLoad(object? sender, EventArgs e)
        {
            // Fire and forget — but since we're on the UI thread and the message
            // loop is running, all the awaits below resume correctly on the UI thread
            // (no ConfigureAwait(false) here so marshalling is automatic).
            _ = StartupAsync();
        }

        private async Task StartupAsync()
        {
            // 1. Load settings (only on very first call — on restart _settings
            //    is already up to date from SettingsForm.OnOkClick).
            if (_settings.Accounts.Count == 0 && _engine is null)
            {
                _settingsService = new SettingsService();
                _settings = _settingsService.LoadOrCreate();
            }

            // 2. First-run: no accounts configured — open Settings so the user
            //    knows what to do. Skip this on engine restarts (accounts exist).
            if (_settings.Accounts.Count == 0)
            {
                ShowSettingsDialog();
                if (_settings.Accounts.Count == 0)
                {
                    // Still no accounts — sit quietly in the tray.
                    BuildTray();
                    return;
                }
            }

            await StartEngineAsync();
        }

        private async Task StartEngineAsync()
        {
            SetStatus("Checking accounts…");

            // Build providers from current settings.
            var providers = ProviderFactory.Build(_settings, Program.HttpClient);

            if (providers.Count == 0)
            {
                SetStatus("No accounts configured. Open Settings to add one.");
                return;
            }

            SetStatus("Authenticating…");

            // Silently authenticate each provider.
            foreach (var provider in providers)
            {
                bool ok = await provider.EnsureAuthenticatedAsync();
                if (!ok)
                {
                    SetStatus($"Sign-in needed for '{provider.DisplayName}'. Use Settings → Sign in.");
                    return;
                }
            }

            SetStatus("Loading index…");

            // Load cached index from disk for fast first slide.
            var indexCache = new IndexCacheService(
                string.IsNullOrEmpty(_settings.IndexCachePath) ? null : _settings.IndexCachePath);
            var indexService = new IndexService(_settings, providers, indexCache);
            var cachedIndex = await indexService.LoadCachedIndexAsync();

            System.Diagnostics.Trace.TraceInformation(
                "[SlideshowForm] Cached index loaded: {0} images.", cachedIndex.TotalCount);

            if (cachedIndex.TotalCount == 0)
                SetStatus("No index yet — scanning OneDrive for images…");
            else
                SetStatus($"Index loaded ({cachedIndex.TotalCount} images). Fetching first image…");

            // Build engine.
            _engine = new SlideshowEngine(
                _settings,
                ct => _indexService!.RefreshFromCloudAsync(ct, msg => SetStatus(msg)),
                (entry, ct) =>
                {
                    var provider = providers.Find(p => p.AccountId == entry.AccountId)
                        ?? throw new InvalidOperationException($"No provider for {entry.AccountId}");

                    int dim = _settings.CacheMaxDimensionPixels > 0
                        ? _settings.CacheMaxDimensionPixels
                        : Math.Max(
                            Screen.PrimaryScreen?.Bounds.Width ?? 1920,
                            Screen.PrimaryScreen?.Bounds.Height ?? 1080);

                    return provider.GetStreamAsync(entry, dim, ct);
                });

            _engine.SlideReady += OnSlideReady;
            _engine.ErrorOccurred += OnEngineError;

            // Capture the UI-thread scheduler NOW (we are on the UI thread).
            // Lambdas registered below may be invoked on thread-pool threads so
            // they must NOT call TaskScheduler.FromCurrentSynchronizationContext()
            // themselves — that throws when CurrentSynchronizationContext is null.
            var uiScheduler = System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext();

            // Wire up index refresh.
            indexService.IndexRefreshed += newIndex =>
            {
                _engine?.UpdateIndex(newIndex);
                SetStatus($"Index updated — {newIndex.TotalCount:N0} images found.");
                // Clear status after a few seconds (marshal via the pre-captured scheduler).
                System.Threading.Tasks.Task.Delay(4000)
                    .ContinueWith(_ => SetStatus(""), uiScheduler);
            };

            // Keep reference for HideCurrentImageAsync.
            _indexService = indexService;

            // Build / rebuild tray icon.
            BuildTray();

            // Start engine — first slide arrives via SlideReady event.
            await _engine.StartAsync(cachedIndex);

            // Kick off background cloud refresh with live progress updates.
            SetStatus(cachedIndex.TotalCount == 0
                ? "Scanning OneDrive for images…"
                : "Refreshing image index in background…");

            _ = indexService
                .RefreshFromCloudAsync(ct: default, onProgress: msg => SetStatus(msg))
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        var msg = t.Exception?.InnerException?.Message
                            ?? t.Exception?.Message
                            ?? "unknown error";
                        SetStatus($"Index scan failed: {msg}");
                        System.Diagnostics.Trace.TraceError(
                            "[SlideshowForm] Direct RefreshFromCloud task faulted: {0}", msg);
                    }
                    else if (t.IsCompletedSuccessfully)
                    {
                        // Status was already updated via IndexRefreshed event;
                        // clear any lingering progress text after a short delay.
                        System.Threading.Tasks.Task.Delay(3000)
                            .ContinueWith(_ => SetStatus(""), uiScheduler);
                    }
                }, uiScheduler);

            // Now safe to go TopMost.
            TopMost = true;

            // Prevent screen saver and display sleep while slideshow is running.
            ScreenWakeService.KeepAwake();
        }

        private void BuildTray()
        {
            _tray?.Dispose();
            _tray = new TrayIconController(this, _engine, _settingsService, _settings);
        }

        // ── Engine events ──────────────────────────────────────────────────────

        private void OnSlideReady(SlideEventArgs e)
        {
            if (InvokeRequired) { BeginInvoke(() => OnSlideReady(e)); return; }

            _currentEntry = e.Entry;
            _tray?.OnSlideChanged(e.Entry.Name);

            var incoming = new Bitmap(e.Bitmap);
            BeginTransition(incoming, e.Entry.Name);

            // Make the window visible on first slide and clear status.
            SetStatus("");
            if (Opacity < 1.0)
                Opacity = 1.0;
        }

        private void OnEngineError(string message)
        {
            if (InvokeRequired) { BeginInvoke(() => OnEngineError(message)); return; }
            _tray?.ShowBalloon("CloudFrame", message, ToolTipIcon.Warning);
            // Also show in the status overlay so it's visible on the black screen.
            if (_currentBitmap is null)
                SetStatus($"Error: {message}");
        }

        // ── Transitions ────────────────────────────────────────────────────────

        private void BeginTransition(Bitmap incoming, string fileName)
        {
            _fadeTimer.Stop();

            if (_settings.Transition == TransitionStyle.None ||
                _settings.TransitionDurationMs <= 0 ||
                _currentBitmap is null)
            {
                _incomingBitmap?.Dispose();
                _currentBitmap?.Dispose();
                _currentBitmap = incoming;
                _incomingBitmap = null;
                _fadeAlpha = 1f;
                UpdateOverlay(fileName);
                _panel.Invalidate();
                return;
            }

            _incomingBitmap?.Dispose();
            _incomingBitmap = incoming;
            _fadeAlpha = 0f;
            UpdateOverlay(fileName);

            int stepMs = Math.Max(1, _settings.TransitionDurationMs / FadeSteps);
            _fadeTimer.Interval = stepMs;
            _fadeTimer.Start();
        }

        private void OnFadeTick(object? sender, EventArgs e)
        {
            _fadeAlpha = Math.Min(1f, _fadeAlpha + 1f / FadeSteps);
            _panel.Invalidate();

            if (_fadeAlpha >= 1f)
            {
                _fadeTimer.Stop();
                _currentBitmap?.Dispose();
                _currentBitmap = _incomingBitmap;
                _incomingBitmap = null;
                _fadeAlpha = 1f;
            }
        }

        // ── Painting ───────────────────────────────────────────────────────────

        private void OnPanelPaint(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.SmoothingMode = SmoothingMode.None;

            var screen = _panel.ClientRectangle;

            if (_currentBitmap is not null)
            {
                float alpha = _incomingBitmap is not null ? 1f - _fadeAlpha : 1f;
                DrawBitmapFitted(g, _currentBitmap, screen, alpha);
            }

            if (_incomingBitmap is not null && _fadeAlpha > 0f)
                DrawBitmapFitted(g, _incomingBitmap, screen, _fadeAlpha);

            if (_overlayText.Length > 0)
                DrawOverlay(g, screen);

            if (_slideshowPaused)
                DrawPauseIcon(g, screen);
        }

        private static void DrawBitmapFitted(Graphics g, Bitmap bmp, Rectangle screen, float alpha)
        {
            var dest = FitRect(bmp.Size, screen);

            if (alpha >= 1f)
            {
                g.DrawImage(bmp, dest);
                return;
            }

            using var ia = new ImageAttributes();
            var cm = new ColorMatrix { Matrix33 = alpha };
            ia.SetColorMatrix(cm);
            g.DrawImage(bmp, dest, 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia);
        }

        private static Rectangle FitRect(Size imageSize, Rectangle screen)
        {
            float scaleX = (float)screen.Width / imageSize.Width;
            float scaleY = (float)screen.Height / imageSize.Height;
            float scale = Math.Min(scaleX, scaleY);

            int w = (int)(imageSize.Width * scale);
            int h = (int)(imageSize.Height * scale);
            int x = screen.X + (screen.Width - w) / 2;
            int y = screen.Y + (screen.Height - h) / 2;

            return new Rectangle(x, y, w, h);
        }

        private void DrawOverlay(Graphics g, Rectangle screen)
        {
            using var font = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point);
            var size = g.MeasureString(_overlayText, font);
            float x = 16f;
            float y = screen.Height - size.Height - 16f;

            using var shadow = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
            g.DrawString(_overlayText, font, shadow, x + 1, y + 1);

            using var brush = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
            g.DrawString(_overlayText, font, brush, x, y);
        }

        private static void DrawPauseIcon(Graphics g, Rectangle screen)
        {
            // Draw a classic ▌▌ pause icon centred on the screen, semi-transparent.
            const int barW = 18;
            const int barH = 60;
            const int gap = 12;
            int totalW = barW * 2 + gap;
            int x = screen.X + (screen.Width - totalW) / 2;
            int y = screen.Y + (screen.Height - barH) / 2;

            using var bg = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
            int padX = 16, padY = 10;
            g.FillRectangle(bg,
                x - padX, y - padY,
                totalW + padX * 2, barH + padY * 2);

            using var bar = new SolidBrush(Color.FromArgb(220, 255, 255, 255));
            g.FillRectangle(bar, x, y, barW, barH);
            g.FillRectangle(bar, x + barW + gap, y, barW, barH);
        }

        private void UpdateOverlay(string fileName)
        {
            _overlayText = fileName;
            _overlayTimer.Stop();
            _overlayTimer.Start();
        }

        internal void SetStatus(string message)
        {
            _statusWindow.SetStatus(message);
        }

        // ── Mouse / click ──────────────────────────────────────────────────────

        private void OnPanelMouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            // The Click event fires for right-clicks too and would start _clickTimer,
            // pausing the slideshow. Cancel that here so the menu doesn't affect state.
            _clickTimer.Stop();
            _tray?.Menu.Show(_panel, e.Location);
        }

        private void OnPanelClick(object? sender, EventArgs e)
        {
            // Start a short timer. If it fires without a second click arriving
            // the gesture is a single-click → toggle pause.
            _clickTimer.Stop();
            _clickTimer.Start();
        }

        private void OnPanelDoubleClick(object? sender, EventArgs e)
        {
            // Cancel the pending single-click action and advance the slide instead.
            _clickTimer.Stop();
            if (_engine is not null) _ = _engine.NextSlideAsync();
        }

        private void OnClickTimerTick(object? sender, EventArgs e)
        {
            _clickTimer.Stop();

            _slideshowPaused = !_slideshowPaused;

            if (_slideshowPaused)
            {
                _ = _engine?.PauseAsync();
                ScreenWakeService.Release();
            }
            else
            {
                _ = _engine?.ResumeAsync();
                ScreenWakeService.KeepAwake();
            }

            _panel.Invalidate(); // repaint to show/hide pause icon
        }

        // ── Keyboard ───────────────────────────────────────────────────────────

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Escape:
                    Application.Exit();
                    break;
                case Keys.Space:
                case Keys.Right:
                    if (_engine is not null) _ = _engine.NextSlideAsync();
                    break;
                case Keys.Left:
                    if (_engine is not null) _ = _engine.PreviousSlideAsync();
                    break;
                case Keys.S:
                    ShowSettingsDialog();
                    break;
            }
        }

        // ── Settings ───────────────────────────────────────────────────────────

        internal void ShowSettingsDialog()
        {
            bool wasTopMost = TopMost;
            TopMost = false;

            using var dlg = new SettingsForm(_settings, _settingsService);
            dlg.TopMost = true;
            var result = dlg.ShowDialog();

            TopMost = wasTopMost;

            // If the user saved new settings, restart the engine so the
            // changes take effect immediately without requiring an app restart.
            if (result == DialogResult.OK)
                _ = RestartEngineAsync();
        }

        // ── Hide image ─────────────────────────────────────────────────────────

        internal void HideCurrentImageAsync()
        {
            var entry = _currentEntry;
            if (entry is null || _indexService is null) return;

            _ = Task.Run(async () =>
            {
                var newIndex = await _indexService
                    .HideAndRebuildAsync(entry.Id)
                    .ConfigureAwait(false);

                _engine?.UpdateIndex(newIndex);

                // Advance immediately so the hidden image disappears at once.
                if (_engine is not null)
                    await _engine.NextSlideAsync().ConfigureAwait(false);
            });
        }

        internal void ToggleAlwaysOnTop()
        {
            if (InvokeRequired) { BeginInvoke(ToggleAlwaysOnTop); return; }
            TopMost = !TopMost;
        }

        private void InvalidateDeltaTokens()
        {
            var deltaService = new CloudFrame.Providers.OneDrive.DeltaSyncService(
                Program.HttpClient,
                string.IsNullOrEmpty(_settings.IndexCachePath) ? null : _settings.IndexCachePath);

            foreach (var account in _settings.Accounts)
            {
                var folders = account.RootFolders.Count > 0
                    ? account.RootFolders
                    : new System.Collections.Generic.List<string> { "" };

                foreach (var folder in folders)
                    deltaService.InvalidateToken(account.AccountId, folder);
            }
        }

        private async Task RestartEngineAsync()
        {
            // Invalidate delta tokens so any settings changes (new root folders,
            // changed filters) trigger a fresh full scan rather than a delta
            // that might miss files excluded by the old config.
            InvalidateDeltaTokens();

            // Tear down the existing engine cleanly.
            if (_engine is not null)
            {
                _engine.SlideReady -= OnSlideReady;
                _engine.ErrorOccurred -= OnEngineError;
                await _engine.DisposeAsync();
                _engine = null;
            }

            SetStatus("Initialising…");

            // Rebuild engine with updated settings.
            // _settings was already updated in-place by SettingsForm.OnOkClick.
            await StartEngineAsync();
        }

        // ── Dispose ────────────────────────────────────────────────────────────

        protected override async void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            ScreenWakeService.Release();
            if (_engine is not null)
                await _engine.DisposeAsync();
            _tray?.Dispose();
            _statusWindow.Dispose();
            _fadeTimer.Dispose();
            _overlayTimer.Dispose();
            _clickTimer.Dispose();
            _currentBitmap?.Dispose();
            _incomingBitmap?.Dispose();
        }

        // ── Double-buffered panel ──────────────────────────────────────────────

        private sealed class SlidePanel : Panel
        {
            public SlidePanel()
            {
                SetStyle(
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.UserPaint |
                    ControlStyles.DoubleBuffer |
                    ControlStyles.OptimizedDoubleBuffer,
                    true);
                UpdateStyles();
            }
        }
    }
}