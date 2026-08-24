using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Fastcull
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        /// <summary>
        /// The app's single window. Exposed because the native folder picker (PRD 1.1.1) needs a
        /// window handle for its IInitializeWithWindow interop, and the view that opens the picker
        /// has no other way to reach one.
        /// </summary>
        public static Window? MainWindow => (Current as App)?._window;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        // Fixed, well-known path so evidence survives regardless of Output-window/pipe timing -
        // File.AppendAllText opens, writes, and closes synchronously on every call, so nothing is
        // buffered that a fail-fast an instant later could lose.
        private static readonly string CrashLogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fastcull-crash.log");

        public App()
        {
            // Last-resort net, not a substitute for guarding call sites: catches exceptions of
            // this general shape (thrown across a XAML/native callback boundary, or in a Task
            // nobody awaited) that would otherwise fail-fast the whole process instead of
            // raising a normal catchable exception. FirstChanceException and AppDomain's own
            // UnhandledException are logged in addition to WinUI's Application-level ones, since
            // between them they cover every point an exception could be first thrown or finally
            // go unhandled, however it crosses the ABI boundary.
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            InitializeComponent();
        }

        /// <summary>
        /// Internal so subsystems can report a failure they had to swallow. Swallowing an
        /// exception to keep the app alive is sometimes right; leaving no trace of it is not -
        /// a silently-caught COMException in the zoom path hid a real defect for three rounds
        /// of investigation because Debug.WriteLine is invisible unless you happen to be
        /// attached to a debugger at the time.
        /// </summary>
        internal static void LogToFile(string source, string details)
        {
            try
            {
                File.AppendAllText(CrashLogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {source}{Environment.NewLine}{details}{Environment.NewLine}---{Environment.NewLine}");
            }
            catch
            {
                // Logging itself must never throw across this boundary.
            }
        }

        private void OnFirstChanceException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            LogToFile("FirstChanceException", e.Exception.ToString());
        }

        private void OnAppDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            LogToFile("AppDomain.UnhandledException", (e.ExceptionObject as Exception)?.ToString() ?? e.ExceptionObject?.ToString() ?? "(null exception object)");
        }

        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            LogToFile("Application.UnhandledException", e.Exception.ToString());
            e.Handled = true;
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            LogToFile("TaskScheduler.UnobservedTaskException", e.Exception.ToString());
            e.SetObserved();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            Diagnostics.ZoomTrace.Bind(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            Diagnostics.ZoomTrace.Reset("app launch");

            _window = new MainWindow();
            _window.Activate();
        }
    }
}
