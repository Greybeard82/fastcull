using Fastcull.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fastcull.Views
{
    /// <summary>
    /// The left panel of PRD 1.5. Purely a surface for <see cref="SidebarViewModel"/> - it holds
    /// no state of its own, so show/hide and pinning behave identically whether they were driven
    /// by the pointer or by the button.
    ///
    /// The auto-hide plumbing deliberately lives in MainWindow rather than here: the hot zone that
    /// reveals the panel sits outside the panel's own bounds, and an element cannot sensibly own
    /// hover tracking for a region it does not contain.
    /// </summary>
    public sealed partial class SidebarView : UserControl
    {
        public SidebarView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Set once by MainWindow after the MainViewModel exists. A DependencyProperty would be
        /// overkill: this is assigned exactly once and never rebound.
        /// </summary>
        public SidebarViewModel ViewModel { get; private set; } = new();

        public void Bind(SidebarViewModel viewModel)
        {
            ViewModel = viewModel;

            // x:Bind generates its bindings against the field as it stood at InitializeComponent,
            // so the whole tree has to be re-evaluated once the real view-model is in place.
            Bindings.Update();
        }

        private void PinButton_Click(object sender, RoutedEventArgs e) => ViewModel.TogglePin();
    }
}
