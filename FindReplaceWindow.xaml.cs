using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace KlingelingFileRenamer
{
    public partial class FindReplaceWindow : Window
    {
        private const string RegistryKeyPath = @"Software\Klingeling\FileRenamer";
        private const string RegistryPatternsValue = "SavedPatterns";

        // Field separator inside one stored entry (unlikely in normal patterns).
        private const char FieldSep = '\u001F';

        private readonly MainWindow _mainWindow;
        private readonly List<SavedPattern> _patterns = new List<SavedPattern>();

        // Prevent SelectionChanged from overwriting the text boxes while we
        // refresh the combo list after Save/Delete.
        private bool _suppressPatternSelection;

        // Prevent ModeRadio_Checked from clearing the text boxes when a saved
        // pattern is applied (that sets the mode programmatically).
        private bool _suppressModeClear;

        public FindReplaceWindow(MainWindow mainWindow)
        {
            InitializeComponent();
            _mainWindow = mainWindow;
            LoadPatternsFromRegistry();
            RefreshPatternsCombo();
            UpdateSavedPanelVisibility();
        }

        /// <summary>Search pattern currently entered in the dialog.</summary>
        public string SearchText => SearchTextBox.Text ?? string.Empty;

        /// <summary>Replacement text currently entered in the dialog.</summary>
        public string ReplaceText => ReplaceTextBox.Text ?? string.Empty;

        /// <summary>True when the Regex radio button is selected.</summary>
        public bool UseRegex => RegexRadio != null && RegexRadio.IsChecked == true;

        /// <summary>Status / error line shown at the bottom of the dialog.</summary>
        public string Status
        {
            get => StatusText.Text ?? string.Empty;
            set => StatusText.Text = value ?? string.Empty;
        }

        /// <summary>
        /// Prefills the search box (e.g. with the current editor selection)
        /// and focuses it so the user can start typing immediately.
        /// </summary>
        public void Prepare(string? initialSearch)
        {
            Status = string.Empty;

            if (!string.IsNullOrEmpty(initialSearch))
            {
                SearchTextBox.Text = initialSearch;
                _suppressPatternSelection = true;
                try
                {
                    SavedPatternsCombo.SelectedIndex = -1;
                }
                finally
                {
                    _suppressPatternSelection = false;
                }
            }

            SearchTextBox.Focus();
            SearchTextBox.SelectAll();
        }

        // ---------- Saved patterns (Registry) ----------

        private void LoadPatternsFromRegistry()
        {
            _patterns.Clear();

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
                if (key?.GetValue(RegistryPatternsValue) is not string[] entries)
                    return;

                foreach (var entry in entries)
                {
                    // Only regex patterns are kept / shown.
                    if (SavedPattern.TryParse(entry, FieldSep, out var pattern) && pattern.UseRegex)
                        _patterns.Add(pattern);
                }
            }
            catch
            {
                // Registry may be unavailable; keep an empty list.
            }
        }

        private void SavePatternsToRegistry()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
                if (key == null)
                    return;

                string[] entries = _patterns
                    .Select(p => p.ToStorageString(FieldSep))
                    .ToArray();

                key.SetValue(RegistryPatternsValue, entries, RegistryValueKind.MultiString);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Could not save patterns to the Registry:\n{ex.Message}",
                    "Find and Replace",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void RefreshPatternsCombo(SavedPattern? select = null)
        {
            _suppressPatternSelection = true;
            try
            {
                SavedPatternsCombo.ItemsSource = null;
                SavedPatternsCombo.ItemsSource = _patterns.ToList();

                if (select != null)
                {
                    int index = _patterns.FindIndex(p =>
                        string.Equals(p.DisplayName, select.DisplayName, StringComparison.OrdinalIgnoreCase));
                    SavedPatternsCombo.SelectedIndex = index;
                }
                else
                {
                    SavedPatternsCombo.SelectedIndex = -1;
                }
            }
            finally
            {
                _suppressPatternSelection = false;
            }
        }

        private void UpdateSavedPanelVisibility()
        {
            // May be called while InitializeComponent is still running and not
            // all named controls have been assigned yet.
            if (SavedPatternsPanel == null)
                return;

            SavedPatternsPanel.Visibility = UseRegex
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void SavedPatternsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressPatternSelection)
                return;

            if (SavedPatternsCombo?.SelectedItem is not SavedPattern pattern)
                return;

            // Set mode first (suppressed so fields are not cleared), then fill fields.
            _suppressModeClear = true;
            try
            {
                if (RegexRadio != null)
                    RegexRadio.IsChecked = true;
            }
            finally
            {
                _suppressModeClear = false;
            }

            if (PatternNameTextBox != null)
                PatternNameTextBox.Text = pattern.DisplayName;
            if (SearchTextBox != null)
                SearchTextBox.Text = pattern.Search;
            if (ReplaceTextBox != null)
                ReplaceTextBox.Text = pattern.Replace;
            UpdateSavedPanelVisibility();
            Status = string.Empty;
        }

        private void ModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            // Checked fires during InitializeComponent when IsChecked="True" is
            // applied in XAML – at that point sibling controls (e.g. RegexRadio)
            // may still be null. Bail out until the window is fully loaded.
            if (RegexRadio == null || SearchTextBox == null || ReplaceTextBox == null
                || PatternNameTextBox == null || SavedPatternsCombo == null
                || SavedPatternsPanel == null)
            {
                return;
            }

            if (_suppressModeClear)
            {
                UpdateSavedPanelVisibility();
                return;
            }

            // Only clear when the user actually switches mode (not on initial load).
            if (!IsLoaded)
            {
                UpdateSavedPanelVisibility();
                return;
            }

            SearchTextBox.Text = string.Empty;
            ReplaceTextBox.Text = string.Empty;
            PatternNameTextBox.Text = string.Empty;
            Status = string.Empty;

            _suppressPatternSelection = true;
            try
            {
                SavedPatternsCombo.SelectedIndex = -1;
            }
            finally
            {
                _suppressPatternSelection = false;
            }

            UpdateSavedPanelVisibility();
        }

        private void SavePattern_Click(object sender, RoutedEventArgs e)
        {
            if (!UseRegex)
            {
                Status = "Patterns can only be saved in Regex mode.";
                return;
            }

            string search = SearchText;
            if (string.IsNullOrEmpty(search))
            {
                Status = "Nothing to save – enter a search pattern first.";
                return;
            }

            string name = (PatternNameTextBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(name))
            {
                Status = "Please enter a name for this pattern.";
                PatternNameTextBox.Focus();
                return;
            }

            var pattern = new SavedPattern(name, search, ReplaceText, useRegex: true);

            // Same name (case-insensitive) ? update that entry.
            int existingByName = _patterns.FindIndex(p =>
                string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase));

            if (existingByName >= 0)
            {
                _patterns[existingByName] = pattern;
                Status = $"Pattern \"{name}\" updated.";
            }
            else
            {
                _patterns.Add(pattern);
                Status = $"Pattern \"{name}\" saved.";
            }

            SavePatternsToRegistry();
            RefreshPatternsCombo(pattern);
        }

        private void DeletePattern_Click(object sender, RoutedEventArgs e)
        {
            if (SavedPatternsCombo.SelectedItem is not SavedPattern pattern)
            {
                Status = "Select a saved pattern to delete.";
                return;
            }

            string name = pattern.DisplayName;
            _patterns.RemoveAll(p =>
                string.Equals(p.DisplayName, pattern.DisplayName, StringComparison.OrdinalIgnoreCase));

            SavePatternsToRegistry();
            RefreshPatternsCombo();
            PatternNameTextBox.Text = string.Empty;
            Status = $"Pattern \"{name}\" deleted.";
        }

        // ---------- Find / Replace actions ----------

        private void FindNext_Click(object sender, RoutedEventArgs e)
        {
            _mainWindow.FindNextFromDialog(this);
        }

        private void ReplaceOne_Click(object sender, RoutedEventArgs e)
        {
            _mainWindow.ReplaceOneFromDialog(this);
        }

        private void ReplaceAll_Click(object sender, RoutedEventArgs e)
        {
            _mainWindow.ReplaceAllFromDialog(this);
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // Enter in Search/Replace runs Find Next; Escape closes the dialog.
        private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                FindNext_Click(sender, e);
                e.Handled = true;
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }

        // ---------- Model ----------

        private sealed class SavedPattern
        {
            public string DisplayName { get; }
            public string Search { get; }
            public string Replace { get; }
            public bool UseRegex { get; }

            public SavedPattern(string displayName, string search, string replace, bool useRegex)
            {
                DisplayName = displayName;
                Search = search;
                Replace = replace;
                UseRegex = useRegex;
            }

            public string ToStorageString(char sep)
            {
                return string.Join(sep.ToString(),
                    Escape(DisplayName),
                    Escape(Search),
                    Escape(Replace),
                    UseRegex ? "1" : "0");
            }

            public static bool TryParse(string entry, char sep, out SavedPattern pattern)
            {
                pattern = null!;
                if (string.IsNullOrEmpty(entry))
                    return false;

                string[] parts = entry.Split(sep);
                if (parts.Length < 4)
                    return false;

                string name = Unescape(parts[0]);
                string search = Unescape(parts[1]);
                string replace = Unescape(parts[2]);
                bool useRegex = parts[3] == "1";

                if (string.IsNullOrEmpty(name))
                    name = search.Length <= 60 ? search : search.Substring(0, 59) + "…";

                pattern = new SavedPattern(name, search, replace, useRegex);
                return true;
            }

            private static string Escape(string value) =>
                (value ?? string.Empty)
                    .Replace("\\", "\\\\")
                    .Replace(FieldSep.ToString(), "\\x1F");

            private static string Unescape(string value)
            {
                if (string.IsNullOrEmpty(value))
                    return string.Empty;

                return value
                    .Replace("\\x1F", FieldSep.ToString())
                    .Replace("\\\\", "\\");
            }
        }
    }
}
