using System;
using System.Drawing;
using System.Windows.Forms;

namespace CloudFrame.App
{
    /// <summary>
    /// Small non-blocking floating status window shown during background work.
    /// Sits in the bottom-right corner, is never TopMost, and auto-hides
    /// when there is nothing to report.
    ///
    /// Thread safety: SetStatus() is safe to call from any thread.
    /// Updates are applied via BeginInvoke (non-blocking) so callers on the
    /// UI thread are never deadlocked.
    /// </summary>
    public sealed class StatusWindow : Form
    {
        private readonly Label _lblTitle;
        private readonly Label _lblMessage;
        private readonly System.Windows.Forms.Timer _autoHideTimer;
        private readonly System.Windows.Forms.Timer _refreshTimer;

        // Pending message written by any thread, read by the UI timer.
        private volatile string _pending = string.Empty;
        private string _displayed = string.Empty;

        private const int WindowWidth = 340;
        private const int WindowHeight = 80;
        private const int Margin = 16;

        public StatusWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = false;
            BackColor = Color.FromArgb(30, 30, 30);
            Opacity = 0.92;
            Size = new Size(WindowWidth, WindowHeight);
            StartPosition = FormStartPosition.Manual;
            Visible = false;

            Region = Region.FromHrgn(
                CreateRoundRectRgn(0, 0, WindowWidth, WindowHeight, 10, 10));

            _lblTitle = new Label
            {
                Text = "CloudFrame",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(160, 160, 160),
                Location = new Point(12, 10),
                Size = new Size(WindowWidth - 24, 18),
                AutoSize = false
            };

            _lblMessage = new Label
            {
                Text = "",
                Font = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(220, 220, 220),
                Location = new Point(12, 32),
                Size = new Size(WindowWidth - 24, 40),
                AutoSize = false
            };

            Controls.Add(_lblTitle);
            Controls.Add(_lblMessage);

            // Auto-hide after idle.
            _autoHideTimer = new System.Windows.Forms.Timer { Interval = 2500 };
            _autoHideTimer.Tick += (_, _) => { _autoHideTimer.Stop(); Hide(); };

            // Poll _pending every 300 ms and apply to UI.
            // This decouples the background threads from the UI thread entirely —
            // no Invoke or BeginInvoke needed from the caller side.
            _refreshTimer = new System.Windows.Forms.Timer { Interval = 300 };
            _refreshTimer.Tick += OnRefreshTick;
            _refreshTimer.Start();

            // Drag support.
            _lblTitle.MouseDown += OnDragMouseDown;
            _lblMessage.MouseDown += OnDragMouseDown;
            MouseDown += OnDragMouseDown;
        }

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Sets the status message. Safe to call from any thread.
        /// Pass empty string to trigger auto-hide.
        /// </summary>
        public void SetStatus(string message)
        {
            // Just write the pending message — the UI timer picks it up.
            // volatile write is safe from any thread.
            _pending = message;
        }

        // ── UI timer — runs on UI thread every 300 ms ──────────────────────────

        private void OnRefreshTick(object? sender, EventArgs e)
        {
            string msg = _pending;

            if (msg == _displayed) return;
            _displayed = msg;

            _autoHideTimer.Stop();

            if (string.IsNullOrEmpty(msg))
            {
                _autoHideTimer.Start();
                return;
            }

            _lblMessage.Text = msg;

            if (!Visible)
            {
                PositionBottomRight();
                Show();
            }
        }

        // ── Positioning ────────────────────────────────────────────────────────

        private void PositionBottomRight()
        {
            var screen = Screen.PrimaryScreen?.WorkingArea
                ?? new Rectangle(0, 0, 1920, 1080);
            Location = new Point(
                screen.Right - WindowWidth - Margin,
                screen.Bottom - WindowHeight - Margin);
        }

        // ── Drag ───────────────────────────────────────────────────────────────

        private Point _dragStart;

        private void OnDragMouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) _dragStart = e.Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (e.Button == MouseButtons.Left)
            {
                Left += e.X - _dragStart.X;
                Top += e.Y - _dragStart.Y;
            }
        }

        // ── Win32 ──────────────────────────────────────────────────────────────

        [System.Runtime.InteropServices.DllImport("Gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(
            int x1, int y1, int x2, int y2, int cx, int cy);

        // ── Dispose ────────────────────────────────────────────────────────────

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _refreshTimer.Dispose();
                _autoHideTimer.Dispose();
                _lblTitle.Font.Dispose();
                _lblMessage.Font.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}