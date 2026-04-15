using System.Runtime.InteropServices;

namespace CloudFrame.App
{
    /// <summary>
    /// Prevents the screen saver from activating and the display from turning
    /// off or locking while the slideshow is running.
    ///
    /// Call <see cref="KeepAwake"/> while the slideshow is playing and
    /// <see cref="Release"/> when it is paused or the app exits.
    ///
    /// Uses <c>SetThreadExecutionState</c>; the ES_CONTINUOUS flag makes the
    /// state sticky so it only needs to be set once per run/pause transition
    /// rather than on a polling timer.
    /// </summary>
    internal static class ScreenWakeService
    {
        // ES flags
        private const uint ES_CONTINUOUS = 0x80000000u;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001u;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002u;

        [DllImport("kernel32.dll", SetLastError = false)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        /// <summary>
        /// Prevents screen saver, display sleep, and automatic screen lock
        /// for as long as the process is alive (or until <see cref="Release"/>
        /// is called).
        /// </summary>
        public static void KeepAwake() =>
            SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);

        /// <summary>
        /// Restores normal power/screen-saver behaviour. Call when the
        /// slideshow is paused or the app is closing.
        /// </summary>
        public static void Release() =>
            SetThreadExecutionState(ES_CONTINUOUS);
    }
}
