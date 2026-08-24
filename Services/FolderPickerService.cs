using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Fastcull.Services
{
    /// <summary>
    /// The native Windows folder picker (PRD 1.1.1).
    ///
    /// **The window-handle interop below is required, not optional.** `FolderPicker` is a WinRT
    /// type designed for UWP, where the system knows which window owns the dialog. A desktop WinUI 3
    /// app has no such ambient context, so the picker must be told explicitly via
    /// `IInitializeWithWindow` before it is shown. Without it the call does not degrade gracefully -
    /// it throws `COMException` (0x8000000E / "A method was called at an unexpected time") the
    /// moment `PickSingleFolderAsync` runs.
    ///
    /// That failure mode is the same shape as the WinRT problems this project has already paid
    /// for: the SoftwareBitmapSource UI-thread affinity crash, and `ApplicationData.Current`
    /// throwing for an unpackaged app. All three are WinRT APIs that assume a UWP host and fail at
    /// runtime rather than at compile time when they do not get one.
    ///
    /// This lives in the WinUI project rather than in Fastcull.Core precisely because of that
    /// window handle - Core has no window and no XAML, and dragging a HWND into it to satisfy a
    /// picker would be the wrong trade.
    /// </summary>
    public static class FolderPickerService
    {
        /// <summary>
        /// Shows the picker and returns the chosen folder's path, or null when the user cancelled.
        ///
        /// Never throws: a picker that fails must leave the app on whatever folder it already had,
        /// exactly as a cancel does.
        /// </summary>
        public static async Task<string?> PickFolderAsync(Window window)
        {
            if (window is null) return null;

            try
            {
                var picker = new FolderPicker
                {
                    SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                };

                // FileTypeFilter must be non-empty or PickSingleFolderAsync throws, even though a
                // folder picker has no use for it. "*" is the documented way to say "any".
                picker.FileTypeFilter.Add("*");

                // The interop step. WindowNative.GetWindowHandle gets the HWND for a WinUI 3
                // Window; InitializeWithWindow.Initialize is the managed wrapper over
                // IInitializeWithWindow::Initialize, which is what gives the picker an owner.
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                StorageFolder? folder = await picker.PickSingleFolderAsync();
                return folder?.Path;
            }
            catch (Exception ex)
            {
                // Logged rather than swallowed silently - a picker that does nothing with no trace
                // is exactly the class of failure the comment above is about.
                App.LogToFile("FolderPickerFailed", ex.ToString());
                System.Diagnostics.Debug.WriteLine($"[FastCull] Folder picker failed: {ex}");
                return null;
            }
        }
    }
}
