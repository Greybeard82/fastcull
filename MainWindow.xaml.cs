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

            // Hand the custom strip to the window so it still drags, and keep the counter clear
            // of the caption buttons.
            SetTitleBar(TitleBarDragRegion);
            TitleBarDragRegion.SizeChanged += (_, _) => ReserveCaptionButtonSpace();
            ReserveCaptionButtonSpace();

            Filmstrip.ViewModel.PropertyChanged += ViewModel_PropertyChanged;

            InitializeSidebar();
        }

        // ------------------------------------------------------------------
        // Sidebar (PRD 1.5)
        // ------------------------------------------------------------------

        private ViewModels.SidebarViewModel SidebarViewModel => Filmstrip.ViewModel.Sidebar;

        private void InitializeSidebar()
        {
            SidebarPanel.Width = ViewModels.SidebarViewModel.PanelWidth;
            SidebarPanel.Bind(SidebarViewModel);

            SidebarViewModel.PropertyChanged += Sidebar_PropertyChanged;
            ApplySidebarLayout();
        }

        private void Sidebar_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // Both derive from IsPinned/IsHovered, so either changing is worth a re-apply. The
            // work is two property writes; filtering it more finely would cost more than it saves.
            if (e.PropertyName is nameof(ViewModels.SidebarViewModel.IsShown)
                              or nameof(ViewModels.SidebarViewModel.GutterWidth)
                              or nameof(ViewModels.SidebarViewModel.IsPinned)
                              or nameof(ViewModels.SidebarViewModel.IsHovered))
            {
                ApplySidebarLayout();
            }
        }

        /// <summary>
        /// The only place the panel's visibility and the stage's gutter are set, so the two can
        /// never disagree - a visible panel over a zero gutter is the overlay case, a visible
        /// panel over a full gutter is the pinned case, and there is no third state to get wrong.
        /// </summary>
        private void ApplySidebarLayout()
        {
            SidebarPanel.Visibility = SidebarViewModel.IsShown ? Visibility.Visible : Visibility.Collapsed;

            // Changing this column's width is what makes the stage recompute its slot count: the
            // FilmstripView's SizeChanged handler already reflows on any width change, so pinning
            // needs no special case there.
            SidebarGutter.Width = SidebarViewModel.GutterWidth;
        }

        private void SidebarHost_PointerEntered(object sender, PointerRoutedEventArgs e)
            => SidebarViewModel.IsHovered = true;

        private void SidebarHost_PointerExited(object sender, PointerRoutedEventArgs e)
            => SidebarViewModel.IsHovered = false;

        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModels.MainViewModel.IsZoomed))
                ApplyFullScreen(Filmstrip.ViewModel.IsZoomed);
        }

        /// <summary>
        /// Puts the window into real fullscreen for zoom, and back afterwards.
        ///
        /// The FullScreen presenter is what removes the system chrome and lets the taskbar
        /// auto-hide; our own title-bar strip collapses separately via its binding, because it is
        /// drawn by the app rather than the system. Returning to the Default (overlapped)
        /// presenter restores the window's previous size and position - the presenter remembers
        /// them, so nothing needs saving here.
        /// </summary>
        private void ApplyFullScreen(bool fullScreen)
        {
            try
            {
                TitleBarDragRegion.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;

                AppWindow.SetPresenter(fullScreen
                    ? AppWindowPresenterKind.FullScreen
                    : AppWindowPresenterKind.Default);
            }
            catch (Exception ex)
            {
                // A presenter change is cosmetic; never let it take the app down mid-cull.
                System.Diagnostics.Debug.WriteLine($"[FastCull] Presenter change failed: {ex}");
            }
        }

        /// <summary>
        /// Pads the title bar's right edge by the width the system reserves for the minimize /
        /// maximize / close buttons, plus the handoff's 30px gap. RightInset is in raw physical
        /// pixels while XAML layout is in DIPs, so it has to be scaled - hard-coding a width
        /// would drift on a non-100% display or if the caption buttons ever change size.
        /// </summary>
        private void ReserveCaptionButtonSpace()
        {
            var scale = TitleBarDragRegion.XamlRoot?.RasterizationScale ?? 1.0;
            if (scale <= 0) scale = 1.0;

            var captionWidth = AppWindow.TitleBar.RightInset / scale;
            TitleBarDragRegion.Padding = new Thickness(11.2, 0, captionWidth + 30, 0);
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
