using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Fastcull.Services;
using Fastcull.ViewModels;

namespace Fastcull.Views
{
    /// <summary>
    /// PRD 4.2's Finish Session confirmation.
    ///
    /// Stage 1: Confirm computes the plan and writes it to a log. It moves and copies nothing -
    /// see <see cref="FinishPlanner"/>, which has no file-writing code beyond the log itself.
    ///
    /// Modal, unlike PRD 2.1.3's help overlay: the root Grid has a solid background and so is
    /// hit-testable, which stops pointer events reaching the stage while a choice is pending.
    /// </summary>
    public sealed partial class FinishSessionView : UserControl
    {
        public FinishSessionView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Set once by MainWindow, exactly as SidebarView is bound. A DependencyProperty would be
        /// overkill for something assigned once and never rebound.
        /// </summary>
        public MainViewModel ViewModel { get; private set; } = new();

        public void Bind(MainViewModel viewModel)
        {
            ViewModel = viewModel;

            // x:Bind resolves against the field as it stood at InitializeComponent, so the tree
            // has to be re-evaluated once the real view-model is in place.
            Bindings.Update();
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
            => ViewModel.FinishOperation = FinishOperation.Copy;

        private void Move_Click(object sender, RoutedEventArgs e)
            => ViewModel.FinishOperation = FinishOperation.Move;

        private void Cancel_Click(object sender, RoutedEventArgs e) => ViewModel.CancelFinish();

        /// <summary>
        /// Stops a run in flight. Distinct from Cancel_Click above, which closes the screen before
        /// anything has started - these are different actions and share no code on purpose.
        /// </summary>
        private void CancelRun_Click(object sender, RoutedEventArgs e) => ViewModel.CancelFinishRun();

        /// <summary>
        /// The button is disabled until a choice is made, so this cannot run without one - but
        /// ConfirmFinishAsync re-checks anyway. A guard that only lives in the UI is a guard that
        /// disappears the moment anything else calls the method.
        /// </summary>
        private async void Confirm_Click(object sender, RoutedEventArgs e)
            => await ViewModel.ConfirmFinishAsync();
    }
}
