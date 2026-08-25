using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Fastcull.Services;
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
            FinishOverlay.Bind(Filmstrip.ViewModel);

            InitializeSidebar();
            InitializeSessions();
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

        // ------------------------------------------------------------------
        // Sessions (PRD 4.1 / 4.2)
        // ------------------------------------------------------------------

        /// <summary>
        /// Wires the session controls. They live here rather than in SidebarView for the same
        /// reason the change-folder request does: both the name prompt and the folder picker need
        /// a window, and the sidebar is a UserControl that does not have one.
        /// </summary>
        private void InitializeSessions()
        {
            SidebarViewModel.CreateSessionRequested += OnCreateSessionRequested;
            SidebarViewModel.ReopenSessionRequested += OnReopenSessionRequested;
            SidebarViewModel.FinishSessionRequested += () => Filmstrip.ViewModel.BeginFinish();

            // Populate the dropdown at startup, so a prior session can be reopened even when the
            // remembered folder is gone and the app came up on the empty state.
            Filmstrip.ViewModel.RefreshSessions();
        }

        /// <summary>
        /// PRD 4.1: prompt for an optional name, then the folder picker. In that order - naming
        /// the job before choosing the card matches how the decision is actually made, and a name
        /// prompt that appeared afterwards would read as a rename of something already open.
        /// </summary>
        private async void OnCreateSessionRequested()
        {
            var (proceed, name) = await PromptForSessionNameAsync();
            if (!proceed) return;

            var folder = await Services.FolderPickerService.PickFolderAsync(this);
            if (string.IsNullOrWhiteSpace(folder)) return;

            await Filmstrip.ViewModel.OpenFolderAsync(folder, name);
        }

        private async void OnReopenSessionRequested(SessionSummary session)
        {
            if (!session.FolderExists)
            {
                await ShowMessageAsync(
                    "Folder not available",
                    $"{session.RootFolder}\n\nThe ratings for this session are safe, but the folder cannot be reached right now. Reconnect the drive and try again.");
                return;
            }

            // No name passed: reopening must never overwrite the name the session already has.
            await Filmstrip.ViewModel.OpenFolderAsync(session.RootFolder);
        }

        /// <summary>
        /// The optional-name prompt. Returns whether to continue, and the name - empty meaning
        /// "skipped", which the caller passes through as null so the folder name is used.
        ///
        /// Two buttons rather than three: Continue takes whatever is in the box including nothing,
        /// so skipping is just not typing. A separate "Skip" button would imply that continuing
        /// with an empty field does something different, which it does not.
        /// </summary>
        private async Task<(bool Proceed, string? Name)> PromptForSessionNameAsync()
        {
            var input = new TextBox
            {
                PlaceholderText = "Optional name",
                Background = new SolidColorBrush(Colors.Black),
                Foreground = new SolidColorBrush(Colors.White),
                MaxLength = 80,
            };

            var hint = new TextBlock
            {
                Text = "Leave blank to use the folder's own name.",
                FontSize = 11,
                Margin = new Thickness(0, 10, 0, 0),
                Foreground = (Brush)Application.Current.Resources["Neutral700Brush"],
                TextWrapping = TextWrapping.Wrap,
            };

            var panel = new StackPanel { Width = 320 };
            panel.Children.Add(input);
            panel.Children.Add(hint);

            var dialog = new ContentDialog
            {
                Title = "New session",
                Content = panel,
                PrimaryButtonText = "Choose folder",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,

                // A ContentDialog in WinUI 3 has no ambient window, so it must be told which XAML
                // tree it belongs to or it throws when shown.
                XamlRoot = Content.XamlRoot,
                RequestedTheme = ElementTheme.Dark,
            };

            // Captured as it is typed rather than read once after the dialog closes.
            //
            // Reading input.Text afterwards looked obviously correct and was measurably wrong: the
            // box showed "Iceland Trip" in a screenshot while Text came back empty, and the
            // session was created with the folder-name fallback every time. The dialog tears its
            // content down as it closes, and whatever the box has not committed by then is gone.
            // Watching TextChanged does not depend on that timing at all.
            var typed = string.Empty;
            input.TextChanged += (_, _) => typed = input.Text;

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return (false, null);

            var name = (string.IsNullOrWhiteSpace(input.Text) ? typed : input.Text)?.Trim();
            return (true, string.IsNullOrWhiteSpace(name) ? null : name);
        }

        private async Task ShowMessageAsync(string title, string message)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot,
                RequestedTheme = ElementTheme.Dark,
            };

            await dialog.ShowAsync();
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
            var vm = Filmstrip.ViewModel;

            switch (e.PropertyName)
            {
                // Two independent reasons to be fullscreen, one window state. Deriving it from
                // both is what lets zoom come and go inside standalone fullscreen without the
                // window changing at all (PRD 1.7.3).
                case nameof(ViewModels.MainViewModel.IsZoomed):
                case nameof(ViewModels.MainViewModel.IsFullScreen):
                    ApplyFullScreen(vm.IsZoomed || vm.IsFullScreen);
                    break;

                case nameof(ViewModels.MainViewModel.IsHelpVisible):
                    HelpOverlay.Visibility = vm.IsHelpVisible ? Visibility.Visible : Visibility.Collapsed;
                    break;

                case nameof(ViewModels.MainViewModel.IsFinishVisible):
                    FinishOverlay.Visibility = vm.IsFinishVisible ? Visibility.Visible : Visibility.Collapsed;
                    break;

                case nameof(ViewModels.MainViewModel.ToastText):
                    ToastText.Text = vm.ToastText;
                    Toast.Visibility = vm.ToastVisibility;
                    break;
            }
        }

        /// <summary>Whether the FullScreen presenter is currently applied.</summary>
        private bool _isFullScreenApplied;

        /// <summary>
        /// Puts the window into real fullscreen, and back afterwards. Driven by zoom (PRD 1.7) and
        /// by standalone fullscreen (PRD 1.7.3) alike - it takes the combined answer, not a
        /// reason, which is what makes the two compose instead of fighting.
        ///
        /// **Idempotent on purpose.** Pressing Space to zoom while already in standalone
        /// fullscreen asks for fullscreen a second time; without this guard that would be a real
        /// presenter round-trip, and the window would visibly flinch for no reason.
        ///
        /// The FullScreen presenter is what removes the system chrome and hides the taskbar; our
        /// own title-bar strip collapses separately, because it is drawn by the app rather than
        /// the system.
        /// </summary>
        private void ApplyFullScreen(bool fullScreen)
        {
            if (fullScreen == _isFullScreenApplied) return;

            try
            {
                TitleBarDragRegion.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;

                // Seeded on the way in, put back on the way out - once per fullscreen cycle, not
                // once per transition. While fullscreen there is no title bar to un-maximize from,
                // so nothing can observe the seeded value in between.
                if (fullScreen) SeedRestoreRect();

                AppWindow.SetPresenter(fullScreen
                    ? AppWindowPresenterKind.FullScreen
                    : AppWindowPresenterKind.Default);

                if (!fullScreen) RestoreSavedRect();

                _isFullScreenApplied = fullScreen;
            }
            catch (Exception ex)
            {
                // A presenter change is cosmetic; never let it take the app down mid-cull.
                System.Diagnostics.Debug.WriteLine($"[FastCull] Presenter change failed: {ex}");
            }
        }

        // ------------------------------------------------------------------
        // Fullscreen transition flicker (PRD 1.7.4)
        // ------------------------------------------------------------------

        private const int SW_SHOWMAXIMIZED = 3;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        /// <summary>Win32 RECT: left/top/right/bottom, NOT x/y/width/height.</summary>
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        /// <summary>
        /// Uses the local RECT above rather than System.Drawing.Rectangle. They are both four ints
        /// and marshal without complaint, but Rectangle means {X, Y, Width, Height} while RECT
        /// means {Left, Top, Right, Bottom} - so substituting one silently writes a rectangle
        /// whose right and bottom edges are a width and a height. That mistake made this very fix
        /// produce a *worse* flicker than the bug it was meant to remove.
        /// </summary>
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT
        {
            public int length, flags, showCmd;
            public POINT ptMinPosition, ptMaxPosition;
            public RECT rcNormalPosition;
        }

        /// <summary>The window's real restore rectangle, held while the seeded one is in place.</summary>
        private RECT? _savedNormalRect;

        /// <summary>
        /// Makes a maximized window's fullscreen transition a single visible step instead of two.
        ///
        /// **The measured problem.** Sampling GetWindowRect and GetWindowPlacement at ~1 ms while
        /// pressing Escape out of zoom, on a maximized window over a 3440x1440 monitor:
        ///
        ///     t+0.0 ms   Escape
        ///     t+89.7 ms  NORMAL      2580x1023   &lt;-- the restored rect, briefly on screen
        ///     t+103.2 ms MAXIMIZED   2580x1023
        ///     t+104.0 ms MAXIMIZED   3456x1408
        ///
        /// The window really does visit its restored size for ~13.5 ms - about one frame at 60 Hz,
        /// and proportionally longer wherever the compositor is slower, which is why it was
        /// noticed on a second, slower machine and not the first. Entering zoom did the same for
        /// ~9.5 ms. Nothing in this file asks for that: SetPresenter restores the window and then
        /// re-maximizes it, and both steps are separately visible.
        ///
        /// **The fix.** The intermediate frame is the window being moved to its stored "restore"
        /// rectangle, so point that rectangle at the geometry the window is about to occupy. The
        /// restore step then lands exactly where the maximize step would have put it, and there is
        /// nothing to see.
        ///
        /// Deliberately does nothing unless the window is maximized: an ordinary windowed size has
        /// no maximize step after the restore, so it never had a second frame to begin with, and
        /// rewriting its restore rectangle would throw away the user's window size for no gain.
        /// </summary>
        private void SeedRestoreRect()
        {
            _savedNormalRect = null;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            var placement = new WINDOWPLACEMENT
            {
                length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>()
            };

            if (!GetWindowPlacement(hwnd, ref placement)) return;
            if (placement.showCmd != SW_SHOWMAXIMIZED) return;
            if (!GetWindowRect(hwnd, out var current)) return;

            _savedNormalRect = placement.rcNormalPosition;

            // Where the window sits right now, which is where the transition will land it. Read
            // from the live window rather than computed from monitor metrics, so the few pixels a
            // maximized window overhangs its monitor by are already accounted for.
            placement.rcNormalPosition = current;
            SetWindowPlacement(hwnd, ref placement);
        }

        /// <summary>
        /// Puts the user's real restore rectangle back, once the window is out of fullscreen and
        /// maximized again - at which point it is not sitting on that rectangle, so the write is
        /// invisible. Un-maximizing later still returns the window to the size the user chose.
        /// </summary>
        private void RestoreSavedRect()
        {
            if (_savedNormalRect is not { } saved) return;
            _savedNormalRect = null;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            // Re-read rather than reusing the placement captured on the way in: the window's state
            // has changed since, and writing a stale showCmd back would undo the transition that
            // just happened. Only the restore rectangle is ours to put back.
            var now = new WINDOWPLACEMENT
            {
                length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>()
            };

            if (!GetWindowPlacement(hwnd, ref now)) return;
            if (now.showCmd != SW_SHOWMAXIMIZED) return;

            now.rcNormalPosition = saved;
            SetWindowPlacement(hwnd, ref now);
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
        /// <summary>
        /// Whether either Shift key is physically down right now.
        ///
        /// KeyRoutedEventArgs carries no modifier state, and a Window has no CoreWindow to ask, so
        /// this goes to the input source directly. Shift arrives as its own KeyDown first, which
        /// resolves to None and is ignored - only the Space that follows it sees this as true.
        /// </summary>
        private static bool IsShiftDown() => IsDown(Windows.System.VirtualKey.Shift);

        /// <summary>Ctrl, for PRD 1.9's undo and redo.</summary>
        private static bool IsControlDown() => IsDown(Windows.System.VirtualKey.Control);

        private static bool IsDown(Windows.System.VirtualKey key)
            => Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(key)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        /// <summary>
        /// Whether a control that wants raw keystrokes currently has focus.
        ///
        /// **This guard is load-bearing, and its absence was a real bug.** PreviewKeyDown tunnels
        /// from the root downward and this handler marks every mapped key Handled, so without it a
        /// focused TextBox never receives a single character - the keys are consumed by the cull
        /// before the box sees them. Typing "Iceland Trip" into the session-name prompt instead
        /// fired I (info overlay), C (set Picked), A and D (navigate), R and T (jump to ends) and
        /// Space (toggle zoom), which rated photos, moved the cursor and dismissed the dialog.
        ///
        /// The ComboBox case is narrower on purpose: it is guarded only while its dropdown is
        /// open. Guarding it whenever it merely has focus would mean that clicking the session
        /// picker once left every subsequent keystroke dead, and the cull would silently stop
        /// responding until the user clicked elsewhere.
        /// </summary>
        private bool IsTextEntryFocused()
        {
            var focused = FocusManager.GetFocusedElement(Content.XamlRoot);

            return focused switch
            {
                TextBox or PasswordBox or RichEditBox or AutoSuggestBox => true,
                ComboBox combo => combo.IsDropDownOpen,
                _ => false,
            };
        }

        private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // Let the focused control have the keystroke. Nothing is marked Handled here, so the
            // TextBox receives it exactly as it would in any other app.
            if (IsTextEntryFocused()) return;

            var resolved = InputRouter.Resolve(e.Key, e.KeyStatus.IsExtendedKey, IsShiftDown(), IsControlDown());

            // The finish confirmation is modal (PRD 4.2), so the cull is unreachable while it is
            // up. Escape still works - a modal with no way out is a trap - and it is routed
            // through the same dismiss chain as everywhere else.
            if (Filmstrip.ViewModel.IsFinishVisible && resolved.Command != AppCommand.ExitZoom)
            {
                e.Handled = true;
                return;
            }

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
