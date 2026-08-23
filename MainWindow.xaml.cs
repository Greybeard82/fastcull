using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Fastcull.Input;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Fastcull
{
    /// <summary>
    /// Hosts the filmstrip and owns keyboard routing for the whole app.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            ApplyBlackTitleBar();
            AppWindow.Closing += AppWindow_Closing;
        }

        /// <summary>True once the shutdown flush has finished and the window may really close.</summary>
        private bool _shutdownComplete;

        /// <summary>
        /// Flushes pending rating writes before the window closes, so a rating made moments
        /// before close is not lost (PRD 3.1).
        ///
        /// This deliberately uses the cancelable AppWindow.Closing rather than Window.Closed.
        /// The previous version blocked the UI thread inside Closed via
        /// ShutdownAsync().GetAwaiter().GetResult(), which deadlocked: the awaited continuation
        /// needed the UI thread to resume on, and the UI thread was busy waiting for that same
        /// continuation. Confirmed from a hang dump - the STA thread sat in
        /// ManualResetEventSlim.Wait inside MainWindow_Closed while dumpasync showed
        /// ShutdownAsync suspended on a SetOnInvokeMres. The window then never closed and the
        /// process had to be killed from Task Manager.
        ///
        /// Cancelling the first close keeps the dispatcher pumping normally, which is the state
        /// async code is supposed to run in, then closes again for real once the flush is done.
        /// </summary>
        private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_shutdownComplete) return;   // second pass: let the close proceed

            args.Cancel = true;

            try
            {
                // No ConfigureAwait(false) here on purpose - Close() below must run on the UI
                // thread, so this continuation should come back to it.
                await Filmstrip.ViewModel.ShutdownAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FastCull] Shutdown flush failed: {ex}");
            }
            finally
            {
                // Set even if the flush threw: a failed flush must not trap the user in a window
                // that refuses to close.
                _shutdownComplete = true;
                Close();
            }
        }

        /// <summary>
        /// PRD 1.10: the caption buttons must not sit on a grey strip. Extending content into
        /// the title bar and painting every title-bar colour black is the only way to get the
        /// whole chrome to exactly #FF000000 - the default title bar is drawn by the system and
        /// ignores the app's theme brushes.
        /// </summary>
        private void ApplyBlackTitleBar()
        {
            ExtendsContentIntoTitleBar = true;

            var titleBar = AppWindow.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;
            titleBar.BackgroundColor = Colors.Black;
            titleBar.InactiveBackgroundColor = Colors.Black;
            titleBar.ButtonBackgroundColor = Colors.Black;
            titleBar.ButtonInactiveBackgroundColor = Colors.Black;
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0xFF, 0x30, 0x30, 0x30);
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0xFF, 0x50, 0x50, 0x50);
            titleBar.ForegroundColor = Colors.White;
            titleBar.InactiveForegroundColor = Color.FromArgb(0xFF, 0x90, 0x90, 0x90);
            titleBar.ButtonForegroundColor = Colors.White;
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(0xFF, 0x90, 0x90, 0x90);
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonPressedForegroundColor = Colors.White;
        }

        /// <summary>
        /// The single place keyboard input is handled, per PRD 2.2 ("handled in one place at
        /// window level") and 2.4. PreviewKeyDown tunnels from the root downward, so this runs
        /// before the bottom ScrollViewer/ItemsRepeater can consume arrow keys for scrolling or
        /// XY-focus navigation - which is exactly why handling KeyDown on the UserControl
        /// previously failed once focus moved into the filmstrip.
        /// </summary>
        private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            var resolved = InputRouter.Resolve(e.Key, e.KeyStatus.IsExtendedKey);

            if (resolved.Command == AppCommand.None)
            {
                // PRD 2.2: unmapped keys log at debug level rather than being silently swallowed.
                System.Diagnostics.Debug.WriteLine(
                    $"[FastCull] Unmapped key: {e.Key} (extended={e.KeyStatus.IsExtendedKey})");
                return;
            }

            Filmstrip.ViewModel.Execute(resolved);

            // Claim the key so the bottom filmstrip never also acts on it (PRD 2.4).
            e.Handled = true;
        }
    }
}
