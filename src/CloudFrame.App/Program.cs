using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Forms;

namespace CloudFrame.App
{
    internal static class Program
    {
        // Single shared HttpClient for the lifetime of the process.
        internal static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestHeaders = { { "User-Agent", "CloudFrame/1.0" } }
        };

        [STAThread]
        private static void Main()
        {
            SetupTraceLog();

            // Drop to BelowNormal priority immediately — most effective way
            // to keep old hardware fans quiet.
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;

            ApplicationConfiguration.Initialize();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

            // SlideshowForm owns the full startup sequence in its Load event.
            // Keeping Main() synchronous ensures Application.Run() always
            // executes on the original STA thread — required by WinForms.
            using var form = new SlideshowForm();
            Application.Run(form);
        }

        /// <summary>
        /// Attaches a file-backed <see cref="TextWriterTraceListener"/> so that
        /// all <c>Trace.*</c> calls throughout the application are written to
        /// <c>%LOCALAPPDATA%\CloudFrame\cloudframe.log</c>.
        /// The file rolls over (renamed to <c>.old.log</c>) when it exceeds 5 MB
        /// to prevent unbounded growth.
        /// </summary>
        private static void SetupTraceLog()
        {
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CloudFrame");
                Directory.CreateDirectory(logDir);

                string logPath = Path.Combine(logDir, "cloudframe.log");

                // Roll over if the log file has grown too large.
                var fi = new FileInfo(logPath);
                if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                {
                    string archive = Path.Combine(logDir, "cloudframe.old.log");
                    File.Move(logPath, archive, overwrite: true);
                }

                Trace.Listeners.Add(new TextWriterTraceListener(logPath, "fileLog")
                {
                    TraceOutputOptions = TraceOptions.DateTime | TraceOptions.ThreadId
                });
                Trace.AutoFlush = true;
                Trace.TraceInformation("[Startup] CloudFrame started — {0}", DateTime.Now);
            }
            catch (Exception ex)
            {
                // Non-fatal: diagnostic logging failed. The app continues without it.
                Debug.WriteLine($"[Startup] Could not set up trace log: {ex.Message}");
            }
        }
    }
}