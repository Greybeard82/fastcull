using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Fastcull.Input;

namespace Fastcull.Views
{
    /// <summary>
    /// PRD 2.1.3's keybinding overlay, toggled by H.
    ///
    /// **Non-blocking rather than modal**, which was a choice: a modal list would have to be
    /// dismissed before any binding on it could be tried, and the whole point of looking it up is
    /// to then press the key. The control sets IsHitTestVisible="False" so the pointer passes
    /// through, and keyboard never reaches it at all because MainWindow handles keys at window
    /// level. Everything underneath keeps working while it is open.
    ///
    /// The rows come from <see cref="KeyBindingCatalog"/>, which reads them out of
    /// <see cref="InputRouter"/> - this control chooses layout and nothing else, so it cannot
    /// disagree with the actual key map.
    ///
    /// Built in code rather than with a DataTemplate: ItemsRepeater does not propagate DataContext
    /// into its templates (the reason FilmstripView binds items to Tag), and the content here is
    /// static for the life of the process, so there is nothing a template would buy.
    /// </summary>
    public sealed partial class HelpOverlayView : UserControl
    {
        /// <summary>Columns the sections are dealt into, top to bottom then left to right.</summary>
        private const int ColumnCount = 2;

        public HelpOverlayView()
        {
            InitializeComponent();
            BuildContent();
        }

        private void BuildContent()
        {
            var columns = new Grid { ColumnSpacing = 44 };
            for (var i = 0; i < ColumnCount; i++)
                columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var stacks = new StackPanel[ColumnCount];
            for (var i = 0; i < ColumnCount; i++)
            {
                stacks[i] = new StackPanel();
                Grid.SetColumn(stacks[i], i);
                columns.Children.Add(stacks[i]);
            }

            // Balance by row count, not by section count: "Rate - jump" is four rows and
            // "Session" is one, so dealing sections round-robin would leave a lopsided card.
            var sections = KeyBindingCatalog.Sections;
            var totalRows = 0;
            foreach (var s in sections) totalRows += s.Rows.Count + 2;   // + title + spacing

            var target = totalRows / (double)ColumnCount;
            var column = 0;
            var placed = 0;

            foreach (var section in sections)
            {
                if (column < ColumnCount - 1 && placed >= target * (column + 1)) column++;

                stacks[column].Children.Add(SectionHeader(section.Title, first: stacks[column].Children.Count == 0));

                foreach (var row in section.Rows)
                    stacks[column].Children.Add(Row(row));

                placed += section.Rows.Count + 2;
            }

            SectionHost.Items.Add(columns);

            Footer.Text = $"H closes this   -   Esc closes it too   -   {CountRows(sections)} bindings";
        }

        private static int CountRows(System.Collections.Generic.IReadOnlyList<KeyBindingSection> sections)
        {
            var n = 0;
            foreach (var s in sections) n += s.Rows.Count;
            return n;
        }

        private TextBlock SectionHeader(string title, bool first) => new()
        {
            Text = title.ToUpperInvariant(),
            FontSize = 9,
            CharacterSpacing = 140,
            FontFamily = (FontFamily)Application.Current.Resources["UiFontFamily"],
            Foreground = (Brush)Application.Current.Resources["Neutral700Brush"],
            Margin = new Thickness(0, first ? 0 : 18, 0, 7),
        };

        /// <summary>
        /// One row: keys on the left in a fixed-width column so the descriptions line up, and the
        /// description beside it. A Grid rather than two stacked TextBlocks because the keys
        /// column has to be the same width on every row for the list to read as a table.
        /// </summary>
        private Grid Row(KeyBindingRow row)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var keys = new TextBlock
            {
                Text = row.Keys,
                FontSize = 12,
                FontFamily = (FontFamily)Application.Current.Resources["UiFontFamily"],
                Foreground = (Brush)Application.Current.Resources["AccentBrush"],
                VerticalAlignment = VerticalAlignment.Center,
            };

            var description = new TextBlock
            {
                Text = row.Description,
                FontSize = 12,
                FontFamily = (FontFamily)Application.Current.Resources["UiFontFamily"],
                Foreground = (Brush)Application.Current.Resources["Neutral300Brush"],
                VerticalAlignment = VerticalAlignment.Center,
            };

            Grid.SetColumn(description, 1);
            grid.Children.Add(keys);
            grid.Children.Add(description);
            return grid;
        }
    }
}
