using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace KlingelingFileRenamer
{
    // Zeigt eine Liste "Alter Dateiname -> Neuer Dateiname" an und liefert
    // ueber den Rueckgabewert von ShowDialog() (true/false) das Ergebnis von
    // Yes/No. DialogResult = true entspricht "Yes".
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
