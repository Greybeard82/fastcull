using Fastcull.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

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

        /// <summary>
        /// Asks for the picker rather than opening it. The picker needs a window handle for its
        /// interop (PRD 1.1.1) and this control does not own one, so FilmstripView - which does -
        /// listens for the request.
        /// </summary>
        private void ChangeFolder_Click(object sender, RoutedEventArgs e) => ViewModel.RequestChangeFolder();

        /// <summary>
        /// PRD 4.1. Same reasoning as ChangeFolder: the name prompt is a ContentDialog and the
        /// picker needs a window handle, and neither belongs to this control.
        /// </summary>
        private void CreateSession_Click(object sender, RoutedEventArgs e) => ViewModel.RequestCreateSession();

        private void FinishSession_Click(object sender, RoutedEventArgs e) => ViewModel.RequestFinishSession();

        /// <summary>
        /// Reports a pick to the view-model, which ignores it when the chosen session is already
        /// the open one - which is what happens every time the list is rebuilt after an open.
        /// </summary>
        private void SessionDropdown_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox combo) ViewModel.SelectSession(combo.SelectedIndex);
        }

        /// <summary>
        /// Keeps the panel up while the list is open. The popup is drawn outside the panel's
        /// bounds, so without this the pointer leaves SidebarHost, the auto-hide collapses the
        /// panel, and the list the user just opened disappears with it.
        /// </summary>
        private void SessionDropdown_DropDownOpened(object sender, object e)
            => ViewModel.IsSessionPickerOpen = true;

        private void SessionDropdown_DropDownClosed(object sender, object e)
            => ViewModel.IsSessionPickerOpen = false;

        /// <summary>
        /// Selecting a folder moves the cursor to its first photo. It deliberately does not filter
        /// the sequence - see the note on FolderNode.FirstPhotoIndex.
        /// </summary>
        private void FolderRow_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (RowOf(sender) is { } row) ViewModel.NavigateToFolder(row);
        }

        /// <summary>
        /// Expand/collapse only. Marking the tap handled is load-bearing: without it the event
        /// bubbles to the row and opening a folder also moves the cursor into it, which was
        /// measurably the behaviour before this was a Tapped handler.
        /// </summary>
        private void FolderChevron_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;

            if (RowOf(sender) is { } row) ViewModel.ToggleFolder(row);
        }

        /// <summary>
        /// The row's view-model, read from Tag rather than DataContext. ItemsRepeater does not
        /// push DataContext into its templates, so reading it here returns null - the template
        /// binds the item to Tag instead.
        /// </summary>
        private static FolderRowViewModel? RowOf(object sender)
            => (sender as FrameworkElement)?.Tag as FolderRowViewModel;
    }
}
