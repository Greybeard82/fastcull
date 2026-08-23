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
using Windows.Foundation;
using Windows.Foundation.Collections;

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
