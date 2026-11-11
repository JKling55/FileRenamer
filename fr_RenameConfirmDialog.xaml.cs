using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace KlingelingFileRenamer
{
    // Shows a list "old file name -> new file name" and returns the result of
    // Yes/No via the return value of ShowDialog() (true/false).
    // DialogResult = true corresponds to "Yes".
    public partial class RenameConfirmDialog : Window
    {
        public RenameConfirmDialog(IEnumerable<(string OldName, string NewName)> changes)
        {
            InitializeComponent();

            ChangesListView.ItemsSource = changes
                .Select(c => new RenameItem(c.OldName, c.NewName))
                .ToList();
        }

        private void Yes_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void No_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private sealed class RenameItem
        {
            public string OldName { get; }
            public string NewName { get; }

            public RenameItem(string oldName, string newName)
            {
                OldName = oldName;
                NewName = newName;
            }
        }
    }
}
