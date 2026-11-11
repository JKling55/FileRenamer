using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Search;
using Microsoft.Win32;


namespace KlingelingFileRenamer
{
    public partial class MainWindow : Window
    {
        // Registry path under which the last selected folder is stored
        private const string RegistryKeyPath = @"Software\Klingeling\FileRenamer";
        private const string RegistryValueName = "LastPath";

        // While restoring the path at startup, do not write back to the registry
        // at the same time.
        private bool _isRestoringPath;

        // Declared as a class field so that Document_Changed (and possibly the
        // save code later on) can access it as well.
        private NewTextColorizer colorizer;

        // If true, the colorizer is ignored in Document_Changed (e.g. while
        // EditorBox.Text is being set programmatically).
        private bool _suppressColorizing;

        // State of the affected line BEFORE the change (captured in Document_Changing)
        // so that Document_Changed can check whether the change violates the
        // invariants (line count, no empty lines).
        private int _lineCountBeforeChange;
        private int _lineLengthBeforeChange;

        // Directory whose files are currently shown in the editor, and the original
        // file names in the order of the editor lines. Set when a directory is
        // displayed and used by "Apply changes" to know which file belongs to
        // which (possibly edited) line.
        private string? _currentDirectory;
        private List<string> _originalFileNames = new List<string>();

        // ---------- Macro recording ----------
        // Stores text changes and caret movements as a sequence.
        // On playback it is replayed from the current caret position
        // (e.g. the same edit one line further down).
        private bool _isRecordingMacro;
        private int _macroLastCaretOffset;
        private int _macroCaretBeforeChange;
        private bool _macroIgnoreCaretChange;
        private readonly List<MacroOperation> _recordedMacro = new List<MacroOperation>();

        private enum MacroOpKind
        {
            TextEdit,
            CaretMove
        }

        private readonly struct MacroOperation
        {
            public MacroOpKind Kind { get; }
            // TextEdit: offset relative to the caret position before the change
            public int RelativeOffset { get; }
            public int RemovalLength { get; }
            public string InsertedText { get; }
            // CaretMove: relative movement in lines/columns (Down/Up/Left/Right, ...)
            public int LineDelta { get; }
            public int ColumnDelta { get; }

            public static MacroOperation TextEdit(int relativeOffset, int removalLength, string insertedText)
            {
                return new MacroOperation(MacroOpKind.TextEdit, relativeOffset, removalLength, insertedText, 0, 0);
            }

            public static MacroOperation CaretMove(int lineDelta, int columnDelta)
            {
                return new MacroOperation(MacroOpKind.CaretMove, 0, 0, string.Empty, lineDelta, columnDelta);
            }

            private MacroOperation(MacroOpKind kind, int relativeOffset, int removalLength, string insertedText,
                int lineDelta, int columnDelta)
            {
                Kind = kind;
                RelativeOffset = relativeOffset;
                RemovalLength = removalLength;
                InsertedText = insertedText;
                LineDelta = lineDelta;
                ColumnDelta = columnDelta;
            }
        }

        public MainWindow()
        {
            InitializeComponent();

            // Also catch rectangular paste via right-click -> Paste and other routes
            DataObject.AddPastingHandler(EditorBox, OnPasting);

            colorizer = new NewTextColorizer(EditorBox.Document);
            EditorBox.TextArea.TextView.LineTransformers.Add(colorizer);
            EditorBox.Document.Changing += Document_Changing;
            EditorBox.Document.Changed += Document_Changed;

            // Hook in AvalonEdit's built-in find panel.
            // After that, Ctrl+F opens the search bar in the editor automatically.
            SearchPanel.Install(EditorBox);

            // Prevents the number of lines in the editor from changing through
            // typing, deleting, pasting or cutting.
            EditorBox.PreviewKeyDown += EditorBox_PreviewKeyDown;
            EditorBox.TextArea.PreviewTextInput += EditorBox_PreviewTextInput;

            // Record caret movements for macro recording.
            EditorBox.TextArea.Caret.PositionChanged += MacroCaret_PositionChanged;

            // Insert key: toggle between insert and overwrite mode.
            EditorBox.TextArea.PreviewKeyDown += EditorBox_OverstrikeToggle_PreviewKeyDown;

            LoadDrives();

            // Restore the last selected path only once the window has finished loading
            // (the tree is then part of the visual tree).
            Loaded += (s, e) => RestoreLastPath();
        }

        // Captures the state of the affected line BEFORE every change so that
        // Document_Changed can evaluate the result.
        private void Document_Changing(object? sender, DocumentChangeEventArgs e)
        {
            var document = EditorBox.Document;
            _lineCountBeforeChange = document.LineCount;

            int offset = Math.Min(e.Offset, document.TextLength);
            _lineLengthBeforeChange = document.GetLineByOffset(offset).Length;

            // For macro: remember the caret position before the text change.
            if (_isRecordingMacro)
                _macroCaretBeforeChange = EditorBox.CaretOffset;
        }

        private void Document_Changed(object? sender, DocumentChangeEventArgs e)  // method
        {
            if (_suppressColorizing)
                return;

            var document = EditorBox.Document;

            // Offset range affected by the change. For a pure deletion InsertionLength
            // is 0, so start == end; that is still enough to find the (possibly merged)
            // line via GetLineByOffset.
            int start = Math.Min(e.Offset, document.TextLength);
            int end = Math.Min(e.Offset + e.InsertionLength, document.TextLength);

            var startLine = document.GetLineByOffset(start);
            var endLine = document.GetLineByOffset(end);

            // ---------- Check invariants ----------
            // This check runs AFTER the change and is therefore independent of the
            // input route (keyboard, context menu "Paste", drag & drop, our own
            // find/replace function, ...). The PreviewKeyDown checks prevent the most
            // common cases beforehand (no flicker); this check is the reliable safety
            // net for all remaining cases.
            bool lineCountChanged = document.LineCount != _lineCountBeforeChange;
            bool lineBecameEmpty = startLine == endLine
                && _lineLengthBeforeChange > 0
                && startLine.Length == 0;

            if (lineCountChanged || lineBecameEmpty)
            {
                // Undo the invalid change. Run it only after the current Changed call
                // (Dispatcher.BeginInvoke) so that UndoStack.Undo() is not called nested
                // inside this event.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _suppressColorizing = true;
                    try
                    {
                        document.UndoStack.Undo();
                    }
                    finally
                    {
                        _suppressColorizing = false;
                    }
                }));
                return;
            }

            // Mark the whole line(s) as changed, not just the exact changed range.
            colorizer.NewSegments.Add(new TextSegment
            {
                StartOffset = startLine.Offset,
                Length = endLine.EndOffset - startLine.Offset
            });

            EditorBox.TextArea.TextView.Redraw();

            // Record only valid changes (invalid ones are already excluded by the
            // "return" in the undo handling above).
            if (_isRecordingMacro)
            {
                // Do not store caret events caused by this text change as navigation.
                _macroIgnoreCaretChange = true;
                string insertedText = e.InsertedText?.Text ?? string.Empty;
                _recordedMacro.Add(MacroOperation.TextEdit(
                    e.Offset - _macroCaretBeforeChange,
                    e.RemovalLength,
                    insertedText));
                _macroLastCaretOffset = EditorBox.CaretOffset;
                Dispatcher.BeginInvoke(new Action(() => _macroIgnoreCaretChange = false));
            }
        }

        // Sets EditorBox.Text programmatically without the colorizer marking the
        // whole result blue.
        private void SetEditorTextWithoutColorizing(string text)
        {
            _suppressColorizing = true;
            try
            {
                EditorBox.Text = text;
                colorizer.NewSegments.Clear();
            }
            finally
            {
                _suppressColorizing = false;
            }
            EditorBox.TextArea.TextView.Redraw();
        }

        // Insert key: insert <-> overwrite (OverstrikeMode).
        private void EditorBox_OverstrikeToggle_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Insert)
                return;

            // Do not intercept Shift+Insert (paste) and Ctrl+Insert (copy); they belong
            // to AvalonEdit's Paste/Copy commands.
            if (Keyboard.Modifiers != ModifierKeys.None)
                return;

            EditorBox.TextArea.OverstrikeMode = !EditorBox.TextArea.OverstrikeMode;
            e.Handled = true;
        }

        // ---------- Protection: the line count must not change ----------
        // Only applies to interactive use (keyboard/clipboard).
        // Programmatically setting EditorBox.Text (e.g. when the selected folder
        // changes) is deliberately NOT affected.

        private void EditorBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

          //  bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

            // Enter would always create a new line -> block it completely.
            if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                e.Handled = true;
                return;
            }

            // Paste (Ctrl+V): block if the clipboard text contains a line break (would
            // add lines), if the selection spans several lines (would remove lines),
            // or if a whole line would become empty because the clipboard content is
            // empty.
            //
            // Exception: rectangular/block selection:
            // - AvalonEdit's own block format on the clipboard, OR
            // - an active block selection (Alt+mouse) in the editor
            // In both cases AvalonEdit inserts column-wise into existing lines and
            // creates no new lines. This also applies to rectangular blocks from other
            // editors (plain text with line breaks only, without the AvalonEdit
            // format).
            // Document_Changed remains the safety net.
            if ((ctrl && e.Key == Key.V) || (shift && e.Key == Key.Insert))
            {
                bool isBlock = IsBlockSelection();
                bool clipboardIsBlockData = ClipboardContainsRectangularData();
                string clipboardText = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
                bool hasNewlines = clipboardText.IndexOf('\n') >= 0 || clipboardText.IndexOf('\r') >= 0;

                // 1. AvalonEdit's own rectangle format or an active block selection -> let it through normally
                if (clipboardIsBlockData || isBlock)
                {
                    PasteForShiftInsert(e);
                    return;
                }

                // 2. Multi-line text from a foreign editor (UltraEdit, Notepad++ ...)
                //    -> try to insert it as a rectangle
                if (hasNewlines)
                {
                    if (TryPasteAsRectangle())
                    {
                        e.Handled = true;   // we handled it ourselves
                        return;
                    }

                    // Could not be inserted as a rectangle -> block it
                    // (otherwise the line count would increase)
                    e.Handled = true;
                    return;
                }

                // 3. Single-line text - normal checks
                bool wouldEmptyLine = SelectionWouldEmptyLine() && clipboardText.Length == 0;
                bool spansMultipleLines = SelectionSpansMultipleLines();

                if (spansMultipleLines || wouldEmptyLine)
                {
                    e.Handled = true;
                    return;
                }

                PasteForShiftInsert(e);
                return;
            }
//                bool isBlock = IsBlockSelection();
//                bool clipboardIsBlockData = ClipboardContainsRectangularData();
//                string clipboardText = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
//                bool hasNewlines = clipboardText.IndexOf('\n') >= 0 || clipboardText.IndexOf('\r') >= 0;
//                // Multi-line text is fine if the target is a block selection
//                // or the clipboard contains AvalonEdit block data.
//                bool pasteAddsLines = hasNewlines && !clipboardIsBlockData && !isBlock;
//                bool wouldEmptyLine = !isBlock && SelectionWouldEmptyLine() && clipboardText.Length == 0;
//                bool spansMultipleLines = !isBlock && SelectionSpansMultipleLines();
//
//                if (pasteAddsLines || spansMultipleLines || wouldEmptyLine)
//                    e.Handled = true;
//
//                return;
//            }

            // Cut (Ctrl+X or Shift+Delete): block if the selection spans several lines
            // (would remove one or more lines) or if a line would become completely
            // empty as a result. Not relevant for block selection (see above).
            if ((ctrl && e.Key == Key.X) || (shift && e.Key == Key.Delete && EditorBox.SelectionLength > 0))
            {
                if (!IsBlockSelection() && (SelectionSpansMultipleLines() || SelectionWouldEmptyLine()))
                    e.Handled = true;

                return;
            }

            if (e.Key == Key.Back)
            {
                if (EditorBox.SelectionLength > 0)
                {
                    if (!IsBlockSelection() && (SelectionSpansMultipleLines() || SelectionWouldEmptyLine()))
                        e.Handled = true;
                }
                else if (CaretWouldMergeWithPreviousLine() || BackspaceWouldEmptyLine())
                {
                    e.Handled = true;
                }
                return;
            }

            if (e.Key == Key.Delete)
            {
                if (EditorBox.SelectionLength > 0)
                {
                    if (!IsBlockSelection() && (SelectionSpansMultipleLines() || SelectionWouldEmptyLine()))
                        e.Handled = true;
                }
                else if (CaretWouldMergeWithNextLine() || DeleteWouldEmptyLine())
                {
                    e.Handled = true;
                }
            }
        }

        // Shift+Insert: AvalonEdit does not reliably treat this key combination as
        // paste (Ctrl+V has an explicit binding there, Shift+Insert does not).
        // Therefore the paste command is triggered here ourselves once the
        // protection checks have passed. Ctrl+V is left to AvalonEdit.
        private void PasteForShiftInsert(KeyEventArgs e)
        {
            if (e.Key != Key.Insert)
                return;

            ApplicationCommands.Paste.Execute(null, EditorBox.TextArea);
            e.Handled = true;
        }

        // Intercepts normal text input: prevents inserted line breaks (e.g. via IME),
        // overwriting a multi-line selection, and accidentally emptying a line when
        // the input text is empty. With a block selection (Alt+mouse) the same text
        // is usually inserted into every affected line - that is allowed.
        private void EditorBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (e.Text.IndexOf('\n') >= 0 || e.Text.IndexOf('\r') >= 0)
            {
                e.Handled = true;
                return;
            }

            if (!IsBlockSelection() && SelectionSpansMultipleLines())
            {
                e.Handled = true;
                return;
            }

            if (e.Text.Length == 0 && !IsBlockSelection() && SelectionWouldEmptyLine())
                e.Handled = true;
        }

        // Detects a rectangular block selection (Alt+mouse). Its segments contain
        // only one column per line and no line breaks, so the line-count/empty-line
        // checks do not apply here.
        private bool IsBlockSelection()
        {
            return EditorBox.TextArea.Selection is RectangleSelection;
        }

        // Detects whether the current clipboard content comes from copying a block
        // selection. AvalonEdit stores its own data format in addition to the plain
        // text (the name may vary slightly between versions, so only "Rectangular"
        // is checked instead of an exact string).
        private bool ClipboardContainsRectangularData()
        {
            try
            {
                var dataObject = Clipboard.GetDataObject();
                if (dataObject == null)
                    return false;

                return dataObject.GetFormats()
                    .Any(format => format.IndexOf("Rectangular", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch (ExternalException)
            {
                // The clipboard may be locked briefly by another process - assume "no"
                // to be safe.
                return false;
            }
        }

        private bool SelectionSpansMultipleLines()
        {
            if (EditorBox.SelectionLength <= 0)
                return false;

            var document = EditorBox.Document;
            int startLine = document.GetLineByOffset(EditorBox.SelectionStart).LineNumber;
            int endLine = document.GetLineByOffset(EditorBox.SelectionStart + EditorBox.SelectionLength).LineNumber;
            return startLine != endLine;
        }

        // Backspace at the start of a line would merge the current line with the
        // previous one -> the line count would decrease.
        private bool CaretWouldMergeWithPreviousLine()
        {
            var document = EditorBox.Document;
            int caretOffset = EditorBox.CaretOffset;
            var line = document.GetLineByOffset(caretOffset);
            return caretOffset == line.Offset && line.LineNumber > 1;
        }

        // Delete at the end of a line would merge the next line with the current
        // one -> the line count would decrease.
        private bool CaretWouldMergeWithNextLine()
        {
            var document = EditorBox.Document;
            int caretOffset = EditorBox.CaretOffset;
            var line = document.GetLineByOffset(caretOffset);
            bool isLastLine = line.LineNumber == document.LineCount;
            return caretOffset == line.EndOffset && !isLastLine;
        }

        // Checks whether removing the current selection (Backspace, Delete, cut, or
        // overwriting with empty text) would reduce a line to 0 characters.
        // Multi-line selections are already blocked elsewhere
        // (SelectionSpansMultipleLines), so only the check for a single line is
        // done here.
        private bool SelectionWouldEmptyLine()
        {
            if (EditorBox.SelectionLength <= 0)
                return false;

            var document = EditorBox.Document;
            var line = document.GetLineByOffset(EditorBox.SelectionStart);
            int selectionEnd = EditorBox.SelectionStart + EditorBox.SelectionLength;

            if (selectionEnd > line.EndOffset)
                return false; // multi-line, handled elsewhere

            int remaining = line.Length - EditorBox.SelectionLength;
            return remaining <= 0;
        }

        // Backspace without selection: if the line contains only this one character
        // (and no line break is deleted, which is secured separately), the line
        // would become empty.
        private bool BackspaceWouldEmptyLine()
        {
            var document = EditorBox.Document;
            int caretOffset = EditorBox.CaretOffset;
            var line = document.GetLineByOffset(caretOffset);
            return caretOffset != line.Offset && line.Length == 1;
        }

        // Delete without selection: analogous to BackspaceWouldEmptyLine, but for
        // deleting the character to the right of the caret.
        private bool DeleteWouldEmptyLine()
        {
            var document = EditorBox.Document;
            int caretOffset = EditorBox.CaretOffset;
            var line = document.GetLineByOffset(caretOffset);
            return caretOffset != line.EndOffset && line.Length == 1;
        }

        // Loads all available drives as root nodes
        private void LoadDrives()
        {
            // Clear the tree so that LoadDrives does not create duplicate drive nodes
            // when called again (e.g. on F5/refresh).
            DirectoryTree.Items.Clear();

            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;

                var item = new TreeViewItem
                {
                    Header = drive.Name,
                    Tag = drive.RootDirectory.FullName
                };

                // Dummy child so that the expand arrow appears (actual loading happens on expand)
                item.Items.Add(new TreeViewItem { Header = "..." });
                item.Expanded += Directory_Expanded;

                DirectoryTree.Items.Add(item);
            }
        }

        // Called when a node is expanded -> loads subfolders on demand (lazy loading)
        private void Directory_Expanded(object sender, RoutedEventArgs e)
        {
            var item = (TreeViewItem)sender;

            // Already loaded? (no dummy child anymore) -> do nothing
            if (item.Items.Count != 1 || !(item.Items[0] is TreeViewItem dummy) || dummy.Header?.ToString() != "...")
                return;

            item.Items.Clear();

            string path = (string)item.Tag;

            try
            {
                foreach (var dir in Directory.GetDirectories(path))
                {
                    var subItem = new TreeViewItem
                    {
                        Header = Path.GetFileName(dir),
                        Tag = dir
                    };
                    subItem.Items.Add(new TreeViewItem { Header = "..." });
                    subItem.Expanded += Directory_Expanded;
                    item.Items.Add(subItem);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Simply skip folders without access rights
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fehler beim Lesen von '{path}':\n{ex.Message}");
            }
        }

        // Called when a directory is selected in the tree.
        // The files of the directory are written to the editor box as text (one per
        // line). The text is freely editable afterwards.
        private void DirectoryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (DirectoryTree.SelectedItem is not TreeViewItem selected)
                return;

            string path = (string)selected.Tag;

            try
            {
                var files = Directory.GetFiles(path);
                var fileNames = files.Select(f => Path.GetFileName(f) ?? string.Empty).ToList();

                // Remember which file belongs to which line - needed by
                // "Apply changes" to map changes to files.
                _currentDirectory = path;
                _originalFileNames = fileNames;

                SetEditorTextWithoutColorizing(string.Join(Environment.NewLine, fileNames));
            }
            catch (UnauthorizedAccessException)
            {
                _currentDirectory = null;
                _originalFileNames = new List<string>();
                SetEditorTextWithoutColorizing("(kein Zugriff auf dieses Verzeichnis)");
            }
            catch (Exception ex)
            {
                _currentDirectory = null;
                _originalFileNames = new List<string>();
                MessageBox.Show($"Fehler beim Lesen von '{path}':\n{ex.Message}");
            }

            // Save only if the selection was made by the user, not while we are
            // expanding the tree ourselves at startup.
            if (!_isRestoringPath)
                SaveLastPath(path);
        }

        // ---------- Remember the last selected path in the registry ----------

        private void SaveLastPath(string path)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
                key?.SetValue(RegistryValueName, path);
            }
            catch (Exception ex)
            {
                // Registry access can fail in exceptional cases (e.g. group policies) -
                // then we simply do not save.
                MessageBox.Show($"Fehler beim Schreiben von '{path}' in Registry:\n{ex.Message}");
            }
        }

        private string? ReadLastPath()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
                return key?.GetValue(RegistryValueName) as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Reads the saved path. If it no longer exists, the next parent directory is
        // tried step by step until one is found that exists (or nothing is left).
        private string? FindExistingPathOrAncestor(string? path)
        {
            while (!string.IsNullOrEmpty(path))
            {
                if (Directory.Exists(path))
                    return path;

                path = Directory.GetParent(path)?.FullName;
            }

            return null;
        }

        private void RestoreLastPath()
        {
            string? saved = ReadLastPath();
            string? target = FindExistingPathOrAncestor(saved);

            if (target == null)
                return;

            _isRestoringPath = true;
            try
            {
                SelectPathInTree(target);
            }
            finally
            {
                _isRestoringPath = false;
            }
        }

        // Expands the tree step by step until the desired path is reached, and
        // selects the corresponding node.
        private void SelectPathInTree(string targetPath)
        {
            string fullPath = Path.GetFullPath(targetPath);
            string rootName = Path.GetPathRoot(fullPath) ?? string.Empty;

            // Find the matching drive node (Tag is the full root path, e.g. "C:\")
            TreeViewItem? current = DirectoryTree.Items
                .OfType<TreeViewItem>()
                .FirstOrDefault(i => string.Equals((string)i.Tag, rootName, StringComparison.OrdinalIgnoreCase));

            if (current == null)
                return;

            // Split the remaining path into individual folder names, e.g. "Users\Max\Projects"
            string remainder = fullPath.Substring(rootName.Length).Trim(Path.DirectorySeparatorChar);
            var segments = remainder.Length == 0
                ? Array.Empty<string>()
                : remainder.Split(Path.DirectorySeparatorChar);

            foreach (var segment in segments)
            {
                // Expanding triggers Directory_Expanded -> loads the children on demand (lazy loading)
                current.IsExpanded = true;

                var next = current.Items
                    .OfType<TreeViewItem>()
                    .FirstOrDefault(i => string.Equals(i.Header?.ToString(), segment, StringComparison.OrdinalIgnoreCase));

                if (next == null)
                    break; // folder e.g. renamed in the meantime -> stay at the last known state

                current = next;
            }

            current.IsSelected = true;
            current.BringIntoView();
        }

        // ---------- Find/Replace dialog (Ctrl+H) ----------
        // Ctrl+F still opens AvalonEdit's built-in find panel.
        // Ctrl+H / Edit > Find/Replace opens our dedicated dialog window.

        private FindReplaceWindow? _findReplaceWindow;

        // Status line written by the last find/replace operation; the dialog
        // reads this after calling into the methods below.
        private string _findReplaceStatus = string.Empty;

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
            // Alt+Letter comes through as Key.System with the real key in SystemKey.
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.F5)
            {
                Refresh_Click(sender, e);
                e.Handled = true;
            }
            else if (ctrl && key == Key.R)
            {
                ApplyChanges_Click(sender, e);
                e.Handled = true;
            }
            else if (ctrl && key == Key.H)
            {
                OpenFindReplaceDialog();
                e.Handled = true;
            }
            else if (ctrl && key == Key.K)
            {
                // Start recording (ignored if already recording)
                if (!_isRecordingMacro)
                    StartMacroRecording();
                e.Handled = true;
            }
            else if (alt && key == Key.K)
            {
                // Stop recording
                if (_isRecordingMacro)
                    StopMacroRecording();
                e.Handled = true;
            }
            else if (ctrl && key == Key.M)
            {
                PlayMacro_Click(sender, e);
                e.Handled = true;
            }
        }

        private void OpenFindReplaceDialog()
        {
            if (_findReplaceWindow == null)
            {
                _findReplaceWindow = new FindReplaceWindow(this)
                {
                    Owner = this
                };
                _findReplaceWindow.Closed += (s, e) =>
                {
                    _findReplaceWindow = null;
                    EditorBox.Focus();
                };
            }

            string? initial = !string.IsNullOrEmpty(EditorBox.SelectedText)
                ? EditorBox.SelectedText
                : null;

            if (!_findReplaceWindow.IsVisible)
                _findReplaceWindow.Show();

            _findReplaceWindow.Prepare(initial);
            _findReplaceWindow.Activate();
        }

        // Called from FindReplaceWindow buttons.
        internal void FindNextFromDialog(FindReplaceWindow dialog)
        {
            FindNext(dialog.SearchText, dialog.UseRegex);
            dialog.Status = _findReplaceStatus;
        }

        internal void ReplaceOneFromDialog(FindReplaceWindow dialog)
        {
            ReplaceOne(dialog.SearchText, dialog.ReplaceText, dialog.UseRegex);
            dialog.Status = _findReplaceStatus;
        }

        internal void ReplaceAllFromDialog(FindReplaceWindow dialog)
        {
            ReplaceAll(dialog.SearchText, dialog.ReplaceText, dialog.UseRegex);
            dialog.Status = _findReplaceStatus;
        }

        // Searches from the current caret position (case-insensitive) and wraps to
        // the start of the text when the end is reached. In regex mode the search
        // term is interpreted as a regular expression.
        private bool FindNext(string search, bool useRegex)
        {
            if (string.IsNullOrEmpty(search))
            {
                _findReplaceStatus = string.Empty;
                return false;
            }

            string text = EditorBox.Text;
            int startPos = EditorBox.SelectionStart + EditorBox.SelectionLength;

            if (useRegex)
            {
                if (!TryBuildRegex(search, out Regex? regex))
                    return false;

                Match match = regex.Match(text, Math.Min(startPos, text.Length));
                if (!match.Success)
                    match = regex.Match(text, 0);

                if (!match.Success)
                {
                    _findReplaceStatus = "Not found";
                    return false;
                }

                _findReplaceStatus = string.Empty;
                EditorBox.Focus();
                EditorBox.Select(match.Index, match.Length);

                int matchLine = EditorBox.Document.GetLineByOffset(match.Index).LineNumber;
                EditorBox.ScrollToLine(matchLine);
                return true;
            }

            int index = text.IndexOf(search, startPos, StringComparison.OrdinalIgnoreCase);

            // Not found -> search from the beginning up to the original position
            if (index < 0)
                index = text.IndexOf(search, 0, StringComparison.OrdinalIgnoreCase);

            if (index < 0)
            {
                _findReplaceStatus = "Not found";
                return false;
            }

            _findReplaceStatus = string.Empty;
            EditorBox.Focus();
            EditorBox.Select(index, search.Length);

            // Scroll into view
            int line = EditorBox.Document.GetLineByOffset(index).LineNumber;
            EditorBox.ScrollToLine(line);

            return true;
        }

        // Tries to compile the search pattern as a regex. For an invalid pattern an
        // error message is set in _findReplaceStatus.
        private bool TryBuildRegex(string pattern, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Regex? regex)
        {
            try
            {
                // Multiline: ^ and $ match start/end of each line, not only the
                // whole document. That matches Replace-All (which already runs
                // the pattern per line) and is what users expect for file names.
                regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
                return true;
            }
            catch (ArgumentException ex)
            {
                regex = null;
                _findReplaceStatus = $"Invalid regex: {ex.Message}";
                return false;
            }
        }

        // Replaces the currently selected match (if it matches the search term) and
        // then jumps to the next match. In regex mode the replace field can use
        // $1, $2, ... to refer to groups of the search pattern.
        private void ReplaceOne(string search, string replacement, bool useRegex)
        {
            if (string.IsNullOrEmpty(search))
                return;

            if (useRegex)
            {
                if (!TryBuildRegex(search, out Regex? regex))
                    return;

                string selected = EditorBox.SelectedText ?? string.Empty;
                Match match = regex.Match(selected);

                // Replace only if the current selection exactly matches a hit of the pattern.
                if (match.Success && match.Index == 0 && match.Length == selected.Length)
                {
                    int pos = EditorBox.SelectionStart;
                    string replacementText = match.Result(replacement ?? string.Empty);
                    EditorBox.Document.Replace(pos, EditorBox.SelectionLength, replacementText);
                    EditorBox.Select(pos, replacementText.Length);
                }

                FindNext(search, useRegex);
                return;
            }

            bool selectionMatches = EditorBox.SelectedText.Equals(search, StringComparison.OrdinalIgnoreCase);

            if (selectionMatches)
            {
                int pos = EditorBox.SelectionStart;
                string repl = replacement ?? string.Empty;
                EditorBox.Document.Replace(pos, EditorBox.SelectionLength, repl);
                EditorBox.Select(pos, repl.Length);
            }

            FindNext(search, useRegex);
        }

        // Replaces all occurrences in the whole text. In regex mode the pattern is
        // applied per line (the replace field can use $1, $2, ... for groups from
        // the search pattern) - ideal e.g. to turn "prefix(group).ext" into
        // "(group)prefix.ext" line by line, regardless of the length of the prefix.
        //
        // In literal mode each match is replaced individually via Document.Replace
        // instead of resetting the whole text - this way the replaced lines are
        // marked blue (as with "Replace"), and the line-count/empty-line checks
        // from Document_Changed apply automatically.
        private void ReplaceAll(string search, string replacement, bool useRegex)
        {
            if (string.IsNullOrEmpty(search))
                return;

            string repl = replacement ?? string.Empty;
            var document = EditorBox.Document;

            if (useRegex)
            {
                if (!TryBuildRegex(search, out Regex? regex))
                    return;

                int changedLines = 0;

                document.BeginUpdate();
                try
                {
                    // Iterate backwards so that the offsets of the lines not yet processed are
                    // not shifted by earlier replacements.
                    for (int lineNumber = document.LineCount; lineNumber >= 1; lineNumber--)
                    {
                        var line = document.GetLineByNumber(lineNumber);
                        string lineText = document.GetText(line);
                        string newLineText = regex.Replace(lineText, repl);

                        if (!string.Equals(lineText, newLineText, StringComparison.Ordinal))
                        {
                            document.Replace(line.Offset, line.Length, newLineText);
                            changedLines++;
                        }
                    }
                }
                finally
                {
                    document.EndUpdate();
                }

                _findReplaceStatus = $"{changedLines} line(s) updated.";
                return;
            }

            string text = EditorBox.Text;

            // First collect all matches ...
            var matchOffsets = new List<int>();
            int pos = 0;
            while (true)
            {
                int index = text.IndexOf(search, pos, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                    break;

                matchOffsets.Add(index);
                pos = index + search.Length;
            }

            // ... and replace from back to front so that the offsets of the matches not
            // yet processed do not shift. BeginUpdate/EndUpdate combines everything
            // into a single undo step; Document_Changed still fires for every single
            // replacement.
            document.BeginUpdate();
            try
            {
                for (int i = matchOffsets.Count - 1; i >= 0; i--)
                {
                    document.Replace(matchOffsets[i], search.Length, repl);
                }
            }
            finally
            {
                document.EndUpdate();
            }

            _findReplaceStatus = $"{matchOffsets.Count} replacement(s)";
        }

        // ---------- Menu actions ----------

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Replace_Click(object sender, RoutedEventArgs e)
        {
            OpenFindReplaceDialog();
        }

        // Compares the current editor lines with the original file names remembered
        // at load time, shows a confirmation dialog if there are differences and,
        // on "Yes", renames the corresponding files on the file system.
        // Afterwards the view is reloaded.
        private void ApplyChanges_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDirectory == null)
            {
                MessageBox.Show("Bitte zuerst ein Verzeichnis auswaehlen.", "Klingeling File Renamer",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_originalFileNames.Count == 0)
            {
                MessageBox.Show("Keine Dateien in diesem Verzeichnis.", "Klingeling File Renamer",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Read the current lines directly from the document (more robust than
            // splitting EditorBox.Text, independent of the exact line-break
            // representation).
            var document = EditorBox.Document;
            var currentNames = new List<string>();
            foreach (var line in document.Lines)
                currentNames.Add(document.GetText(line));

            if (currentNames.Count != _originalFileNames.Count)
            {
                MessageBox.Show(
                    "Die Anzahl der Zeilen stimmt nicht mehr mit der Anzahl der Dateien ueberein.\n" +
                    "Bitte das Verzeichnis neu laden (F5) und erneut versuchen.",
                    "Klingeling File Renamer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Catch empty/whitespace-only lines (should not actually occur thanks to
            // the editor protection mechanisms).
            for (int i = 0; i < currentNames.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(currentNames[i]))
                {
                    MessageBox.Show(
                        $"Zeile {i + 1} ist leer - das ist nicht erlaubt.\nBitte korrigieren und erneut versuchen.",
                        "Klingeling File Renamer", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            // Only lines that actually changed are files to be renamed.
            var changes = new List<(string OldName, string NewName)>();
            for (int i = 0; i < _originalFileNames.Count; i++)
            {
                if (!string.Equals(_originalFileNames[i], currentNames[i], StringComparison.Ordinal))
                    changes.Add((_originalFileNames[i], currentNames[i]));
            }

            if (changes.Count == 0)
            {
                MessageBox.Show("Keine Aenderungen vorhanden.", "Klingeling File Renamer",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Check for invalid file names up front.
            var invalidChars = Path.GetInvalidFileNameChars();
            var invalidNames = changes.Where(c => c.NewName.IndexOfAny(invalidChars) >= 0).ToList();
            if (invalidNames.Count > 0)
            {
                string list = string.Join("\n", invalidNames.Select(c => $"'{c.NewName}' enthaelt ungueltige Zeichen"));
                MessageBox.Show($"Folgende neue Dateinamen sind ungueltig:\n\n{list}",
                    "Klingeling File Renamer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Detect duplicate target names (case-insensitive, as usual for file
            // systems) - considers all lines, not only the changed ones, so that a
            // renamed file also does not collide with an unchanged one.
            var duplicates = currentNames
                .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicates.Count > 0)
            {
                string list = string.Join("\n", duplicates);
                MessageBox.Show(
                    $"Folgende Dateinamen wuerden doppelt vergeben:\n\n{list}\n\nBitte eindeutige Namen verwenden.",
                    "Klingeling File Renamer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Show a confirmation dialog with the list of all changes.
            var dialog = new RenameConfirmDialog(changes) { Owner = this };
            if (dialog.ShowDialog() != true)
                return;

            // Actual renaming at file system level.
            var errors = new List<string>();
            foreach (var (oldName, newName) in changes)
            {
                string oldFullPath = Path.Combine(_currentDirectory, oldName);
                string newFullPath = Path.Combine(_currentDirectory, newName);

                try
                {
                    bool caseOnlyChange = string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase);

                    if (caseOnlyChange)
                    {
                        // File.Move fails for a pure upper/lower-case change on a case-insensitive
                        // file system because the target apparently already exists (same file).
                        // Therefore rename via a temporary name.
                        string tempFullPath = oldFullPath + ".klingeling_tmp";
                        File.Move(oldFullPath, tempFullPath);
                        File.Move(tempFullPath, newFullPath);
                    }
                    else
                    {
                        if (File.Exists(newFullPath))
                        {
                            errors.Add($"{oldName} -> {newName}: Zieldatei existiert bereits.");
                            continue;
                        }

                        File.Move(oldFullPath, newFullPath);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{oldName} -> {newName}: {ex.Message}");
                }
            }

            if (errors.Count > 0)
            {
                MessageBox.Show(
                    "Einige Dateien konnten nicht umbenannt werden:\n\n" + string.Join("\n", errors),
                    "Klingeling File Renamer", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            // Final refresh, as requested.
            Refresh_Click(this, new RoutedEventArgs());
        }

        // ---------- Macro recording/playback ----------

        private void MacroCaret_PositionChanged(object? sender, EventArgs e)
        {
            if (!_isRecordingMacro || _macroIgnoreCaretChange)
                return;

            int newOffset = EditorBox.CaretOffset;
            if (newOffset == _macroLastCaretOffset)
                return;

            var document = EditorBox.Document;
            var oldLine = document.GetLineByOffset(
                Math.Clamp(_macroLastCaretOffset, 0, document.TextLength));
            var newLine = document.GetLineByOffset(
                Math.Clamp(newOffset, 0, document.TextLength));

            int oldColumn = _macroLastCaretOffset - oldLine.Offset;
            int newColumn = newOffset - newLine.Offset;
            int lineDelta = newLine.LineNumber - oldLine.LineNumber;
            int columnDelta = newColumn - oldColumn;

            // Store a pure caret movement (arrow keys, Home/End, click, ...).
            if (lineDelta != 0 || columnDelta != 0)
                _recordedMacro.Add(MacroOperation.CaretMove(lineDelta, columnDelta));

            _macroLastCaretOffset = newOffset;
        }

        private void StartMacroRecording()
        {
            _recordedMacro.Clear();
            _macroLastCaretOffset = EditorBox.CaretOffset;
            _macroIgnoreCaretChange = false;
            _isRecordingMacro = true;
            RecordMacroMenuItem.Header = "_Stop Recording Macro";
            RecordMacroMenuItem.InputGestureText = "Alt+K";
        }

        private void StopMacroRecording()
        {
            if (!_isRecordingMacro)
                return;

            _isRecordingMacro = false;
            RecordMacroMenuItem.Header = "_Start Recording Macro";
            RecordMacroMenuItem.InputGestureText = "Ctrl+K";
            MessageBox.Show($"Macro recorded: {_recordedMacro.Count} step(s).",
                "Klingeling File Renamer", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void RecordMacro_Click(object sender, RoutedEventArgs e)
        {
            if (_isRecordingMacro)
                StopMacroRecording();
            else
                StartMacroRecording();
        }

        // Plays the recorded macro from the current caret position.
        // Caret movements (e.g. one line down) are executed as well, followed by
        // the associated text changes.
        private void PlayMacro_Click(object sender, RoutedEventArgs e)
        {
            if (_isRecordingMacro)
            {
                MessageBox.Show("Recording still active - stop it first (Alt+K).",
                    "Klingeling File Renamer", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_recordedMacro.Count == 0)
            {
                MessageBox.Show("No macro recorded.", "Klingeling File Renamer",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var document = EditorBox.Document;
            int caret = EditorBox.CaretOffset;

            _macroIgnoreCaretChange = true;
            document.BeginUpdate();
            try
            {
                foreach (var op in _recordedMacro)
                {
                    if (op.Kind == MacroOpKind.CaretMove)
                    {
                        caret = ApplyCaretMove(document, caret, op.LineDelta, op.ColumnDelta);
                    }
                    else // TextEdit
                    {
                        int offset = Math.Clamp(caret + op.RelativeOffset, 0, document.TextLength);
                        int removalLength = Math.Clamp(op.RemovalLength, 0, document.TextLength - offset);
                        document.Replace(offset, removalLength, op.InsertedText);
                        // Caret ends up behind the inserted text, as with normal input.
                        caret = offset + (op.InsertedText?.Length ?? 0);
                    }
                }
            }
            finally
            {
                document.EndUpdate();
                EditorBox.CaretOffset = Math.Clamp(caret, 0, document.TextLength);
                _macroLastCaretOffset = EditorBox.CaretOffset;
                _macroIgnoreCaretChange = false;
            }
        }

        // Moves the logical caret by lineDelta lines and columnDelta columns.
        private static int ApplyCaretMove(TextDocument document, int caret, int lineDelta, int columnDelta)
        {
            caret = Math.Clamp(caret, 0, document.TextLength);
            var line = document.GetLineByOffset(caret);
            int column = caret - line.Offset;

            int targetLineNumber = Math.Clamp(line.LineNumber + lineDelta, 1, document.LineCount);
            var targetLine = document.GetLineByNumber(targetLineNumber);

            int targetColumn = Math.Clamp(column + columnDelta, 0, targetLine.Length);
            return targetLine.Offset + targetColumn;
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            // Remember the currently selected path so that after reloading the tree is
            // at the same place again instead of falling back to the bare drive list.
            string? currentPath = (DirectoryTree.SelectedItem as TreeViewItem)?.Tag as string;

            LoadDrives();

            string? target = FindExistingPathOrAncestor(currentPath);
            if (target == null)
                return;

            _isRestoringPath = true;
            try
            {
                SelectPathInTree(target);
            }
            finally
            {
                _isRestoringPath = false;
            }
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "Klingeling File Renamer\n\n" +
                "A simple tool for renaming files.\n" +
                "Select a folder in the tree, edit the file names\n" +
                "in the editor and apply the changes.\n\n" +
                "Copyright 2026 John Kling.\n\n" +
                "Donations are welcome via PayPal at \njohnkling@gmx.de..\n",
                "About Klingeling File Renamer",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        private bool TryPasteAsRectangle()
        {
            if (!Clipboard.ContainsText())
                return false;

            string text = Clipboard.GetText();
            if (string.IsNullOrEmpty(text))
                return false;

            // No line break -> no rectangle needed
            if (text.IndexOf('\n') < 0 && text.IndexOf('\r') < 0)
                return false;

            var textArea = EditorBox.TextArea;

            // Only with an empty selection (caret) or an already active block selection
            if (!textArea.Selection.IsEmpty && !(textArea.Selection is RectangleSelection))
                return false;

            return RectangleSelection.PerformRectangularPaste(
                textArea,
                textArea.Caret.Position,
                text,
                false);
        }
        private void OnPasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText, true))
                return;

            string? text = e.DataObject.GetData(DataFormats.UnicodeText, true) as string;
            if (string.IsNullOrEmpty(text))
                return;

            bool hasNewlines = text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0;
            if (!hasNewlines)
                return;

            // Multi-line text -> try to insert as a rectangle and cancel the normal paste
            if (TryPasteAsRectangle())
            {
                e.CancelCommand();
            }
            else
            {
                // Not possible as a rectangle -> cancel so that no lines are added
                e.CancelCommand();
            }
        }

    }  // end MainWindow : Window
}

