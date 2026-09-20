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
        // Registry-Pfad, unter dem der zuletzt gewaehlte Ordner gespeichert wird
        private const string RegistryKeyPath = @"Software\Klingeling\FileRenamer";
        private const string RegistryValueName = "LastPath";

        // Waehrend des Wiederherstellens beim Start soll nicht gleichzeitig
        // wieder in die Registry geschrieben werden.
        private bool _isRestoringPath;

        // Als Klassenfeld deklariert, damit Document_Changed (und ggf.
        // spaeter der Speichern-Code) ebenfalls darauf zugreifen kann.
        private NewTextColorizer colorizer;

        // Wenn true, wird der Colorizer bei Document_Changed ignoriert
        // (z.B. waehrend EditorBox.Text programmatisch gesetzt wird).
        private bool _suppressColorizing;

        // Zustand der betroffenen Zeile VOR der Aenderung (in Document_Changing
        // erfasst), damit Document_Changed pruefen kann, ob die Aenderung
        // gegen die Invarianten (Zeilenanzahl, keine leeren Zeilen) verstoesst.
        private int _lineCountBeforeChange;
        private int _lineLengthBeforeChange;

        // Verzeichnis, dessen Dateien aktuell im Editor angezeigt werden,
        // und die Original-Dateinamen in der Reihenfolge der Editor-Zeilen.
        // Wird beim Anzeigen eines Verzeichnisses gesetzt und von
        // "Apply changes" benutzt, um zu wissen, welche Datei zu welcher
        // (ggf. editierten) Zeile gehoert.
        private string? _currentDirectory;
        private List<string> _originalFileNames = new List<string>();

        // ---------- Makro-Aufzeichnung ----------
        // Speichert Textänderungen und Cursor-Bewegungen als Sequenz.
        // Beim Abspielen wird ab der aktuellen Cursorposition nachgespielt
        // (z.B. gleiche Bearbeitung eine Zeile tiefer).
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
            // TextEdit: Offset relativ zur Cursorposition vor der Aenderung
            public int RelativeOffset { get; }
            public int RemovalLength { get; }
            public string InsertedText { get; }
            // CaretMove: relative Bewegung in Zeilen/Spalten (Down/Up/Left/Right, …)
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

            // Rechteck-Paste auch über Rechtsklick ? Einfügen und andere Wege abfangen
            DataObject.AddPastingHandler(EditorBox, OnPasting);

            colorizer = new NewTextColorizer(EditorBox.Document);
            EditorBox.TextArea.TextView.LineTransformers.Add(colorizer);
            EditorBox.Document.Changing += Document_Changing;
            EditorBox.Document.Changed += Document_Changed;

            // Fertige Suchen/Ersetzen-Leiste von AvalonEdit einklinken.
            // Danach oeffnet Strg+F automatisch die Suchleiste im Editor.
            SearchPanel.Install(EditorBox);

            // Verhindert, dass sich die Anzahl der Zeilen im Editor durch
            // Tippen, Loeschen, Einfuegen oder Ausschneiden veraendert.
            EditorBox.PreviewKeyDown += EditorBox_PreviewKeyDown;
            EditorBox.TextArea.PreviewTextInput += EditorBox_PreviewTextInput;

            // Cursor-Bewegungen fuer die Makro-Aufzeichnung mitschneiden.
            EditorBox.TextArea.Caret.PositionChanged += MacroCaret_PositionChanged;

            // Einfg: Umschalten zwischen Einfuege- und Ueberschreibmodus.
            EditorBox.TextArea.PreviewKeyDown += EditorBox_OverstrikeToggle_PreviewKeyDown;

            LoadDrives();

            // Erst nachdem das Fenster fertig geladen ist (Baum ist dann im
            // visuellen Baum), den zuletzt gewaehlten Pfad wiederherstellen.
            Loaded += (s, e) => RestoreLastPath();
        }

        // Erfasst VOR jeder Aenderung den Zustand der betroffenen Zeile,
        // damit Document_Changed das Ergebnis bewerten kann.
        private void Document_Changing(object? sender, DocumentChangeEventArgs e)
        {
            var document = EditorBox.Document;
            _lineCountBeforeChange = document.LineCount;

            int offset = Math.Min(e.Offset, document.TextLength);
            _lineLengthBeforeChange = document.GetLineByOffset(offset).Length;

            // Fuer Makro: Cursorposition vor der Textaenderung merken.
            if (_isRecordingMacro)
                _macroCaretBeforeChange = EditorBox.CaretOffset;
        }

        private void Document_Changed(object? sender, DocumentChangeEventArgs e)  // Methode
        {
            if (_suppressColorizing)
                return;

            var document = EditorBox.Document;

            // Offset-Bereich, der von der Aenderung betroffen ist. Bei reinem
            // Loeschen ist InsertionLength 0 - dann ist Start == Ende, das
            // reicht aber, um ueber GetLineByOffset die (ggf. zusammen-
            // gefuehrte) Zeile zu finden.
            int start = Math.Min(e.Offset, document.TextLength);
            int end = Math.Min(e.Offset + e.InsertionLength, document.TextLength);

            var startLine = document.GetLineByOffset(start);
            var endLine = document.GetLineByOffset(end);

            // ---------- Invarianten pruefen ----------
            // Diese Pruefung greift NACH der Aenderung und damit unabhaengig
            // vom Eingabeweg (Tastatur, Kontextmenue "Einfuegen", Drag&Drop,
            // eigene Suchen/Ersetzen-Funktion, ...). Die PreviewKeyDown-
            // Pruefungen verhindern die haeufigsten Faelle schon vorher
            // (kein Flackern), diese Pruefung ist das zuverlaessige Netz
            // fuer alle uebrigen Faelle.
            bool lineCountChanged = document.LineCount != _lineCountBeforeChange;
            bool lineBecameEmpty = startLine == endLine
                && _lineLengthBeforeChange > 0
                && startLine.Length == 0;

            if (lineCountChanged || lineBecameEmpty)
            {
                // Ungueltige Aenderung rueckgaengig machen. Erst nach dem
                // aktuellen Changed-Aufruf ausfuehren (Dispatcher.BeginInvoke),
                // damit UndoStack.Undo() nicht verschachtelt in dieses Event
                // hineinruft.
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

            // Ganze Zeile(n) als geaendert markieren, nicht nur den
            // exakten Aenderungsbereich.
            colorizer.NewSegments.Add(new TextSegment
            {
                StartOffset = startLine.Offset,
                Length = endLine.EndOffset - startLine.Offset
            });

            EditorBox.TextArea.TextView.Redraw();

            // Nur gueltige Aenderungen aufzeichnen (die obige Undo-Behandlung
            // ist bereits per "return" oben ausgeschlossen).
            if (_isRecordingMacro)
            {
                // Caret-Events durch diese Textaenderung nicht als Navigation speichern.
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

        // Setzt EditorBox.Text programmatisch, ohne dass der Colorizer das
        // Ergebnis komplett blau markiert.
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

        // Einfg-Taste: Einfuegen <-> Ueberschreiben (OverstrikeMode).
        private void EditorBox_OverstrikeToggle_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Insert)
                return;

            EditorBox.TextArea.OverstrikeMode = !EditorBox.TextArea.OverstrikeMode;
            e.Handled = true;
        }

        // ---------- Schutz: Zeilenanzahl darf sich nicht veraendern ----------
        // Greift nur bei interaktiver Bedienung (Tastatur/Zwischenablage).
        // Programmatisches Setzen von EditorBox.Text (z.B. beim Wechsel des
        // ausgewaehlten Ordners) ist davon bewusst NICHT betroffen.

        private void EditorBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

            // Enter wuerde immer eine neue Zeile erzeugen -> komplett blockieren.
            if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                e.Handled = true;
                return;
            }

            // Einfuegen (Strg+V): blockieren, wenn der Zwischenablagetext
            // einen Zeilenumbruch enthaelt (wuerde Zeilen hinzufuegen), die
            // Selektion mehrere Zeilen umfasst (wuerde Zeilen entfernen),
            // oder eine ganze Zeile durch leeren Zwischenablageinhalt leer
            // werden wuerde.
            //
            // Ausnahme Rechteck-/Blockselektion:
            // - AvalonEdit-eigenes Blockformat in der Zwischenablage, ODER
            // - aktive Blockselektion (Alt+Maus) im Editor
            // In beiden Faellen fuegt AvalonEdit spaltenweise in bestehende
            // Zeilen ein und erzeugt keine neuen Zeilen. Das gilt auch fuer
            // Rechteckbloecke aus anderen Editoren (dort nur Klartext mit
            // Zeilenumbruechen, ohne AvalonEdit-Format).
            // Document_Changed bleibt das Sicherheitsnetz.
            if (ctrl && e.Key == Key.V)
            {
                bool isBlock = IsBlockSelection();
                bool clipboardIsBlockData = ClipboardContainsRectangularData();
                string clipboardText = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
                bool hasNewlines = clipboardText.IndexOf('\n') >= 0 || clipboardText.IndexOf('\r') >= 0;

                // 1. AvalonEdit-eigenes Rechteck-Format oder aktive Blockselektion ? normal durchlassen
                if (clipboardIsBlockData || isBlock)
                    return;

                // 2. Mehrzeiliger Text aus fremdem Editor (UltraEdit, Notepad++ …)
                //    ? versuchen, als Rechteck einzufügen
                if (hasNewlines)
                {
                    if (TryPasteAsRectangle())
                    {
                        e.Handled = true;   // wir haben es selbst erledigt
                        return;
                    }

                    // Konnte nicht als Rechteck eingefügt werden ? blockieren
                    // (sonst würde die Zeilenanzahl steigen)
                    e.Handled = true;
                    return;
                }

                // 3. Einzeiliger Text – normale Prüfungen
                bool wouldEmptyLine = SelectionWouldEmptyLine() && clipboardText.Length == 0;
                bool spansMultipleLines = SelectionSpansMultipleLines();

                if (spansMultipleLines || wouldEmptyLine)
                    e.Handled = true;

                return;
            }
//                bool isBlock = IsBlockSelection();
//                bool clipboardIsBlockData = ClipboardContainsRectangularData();
//                string clipboardText = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
//                bool hasNewlines = clipboardText.IndexOf('\n') >= 0 || clipboardText.IndexOf('\r') >= 0;
//                // Mehrzeiliger Text ist ok, wenn Ziel eine Blockselektion ist
//                // oder die Zwischenablage AvalonEdit-Blockdaten enthaelt.
//                bool pasteAddsLines = hasNewlines && !clipboardIsBlockData && !isBlock;
//                bool wouldEmptyLine = !isBlock && SelectionWouldEmptyLine() && clipboardText.Length == 0;
//                bool spansMultipleLines = !isBlock && SelectionSpansMultipleLines();
//
//                if (pasteAddsLines || spansMultipleLines || wouldEmptyLine)
//                    e.Handled = true;
//
//                return;
//            }

            // Ausschneiden (Strg+X): blockieren, wenn die Selektion mehrere
            // Zeilen umfasst (wuerde eine oder mehrere Zeilen entfernen)
            // oder eine Zeile dadurch komplett leer wuerde. Bei Blockselektion
            // nicht relevant (siehe oben).
            if (ctrl && e.Key == Key.X)
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

        // Faengt normale Texteingabe ab: verhindert eingefuegte Zeilenumbrueche
        // (z.B. ueber IME), das Ueberschreiben einer mehrzeiligen Selektion,
        // und das versehentliche Leerwerden einer Zeile bei leerem Eingabetext.
        // Bei einer Blockselektion (Alt+Maus) wird ueblicherweise in jede
        // betroffene Zeile derselbe Text eingefuegt - das ist erlaubt.
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

        // Erkennt eine rechteckige Blockselektion (Alt+Maus). Deren Segmente
        // enthalten pro Zeile nur eine Spalte und keine Zeilenumbrueche,
        // daher greifen die Zeilenanzahl-/Leerzeilen-Pruefungen hier nicht.
        private bool IsBlockSelection()
        {
            return EditorBox.TextArea.Selection is RectangleSelection;
        }

        // Erkennt, ob der aktuelle Zwischenablageinhalt aus dem Kopieren
        // einer Blockselektion stammt. AvalonEdit legt dafuer zusaetzlich
        // zum Klartext ein eigenes Datenformat ab (Name kann je nach Version
        // leicht variieren, daher wird nur auf "Rectangular" geprueft statt
        // auf einen exakten String).
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
                // Zwischenablage kann kurzzeitig von einem anderen Prozess
                // gesperrt sein - dann sicherheitshalber "nein" annehmen.
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

        // Backspace am Zeilenanfang wuerde die aktuelle Zeile mit der
        // vorherigen verschmelzen -> Zeilenanzahl wuerde sinken.
        private bool CaretWouldMergeWithPreviousLine()
        {
            var document = EditorBox.Document;
            int caretOffset = EditorBox.CaretOffset;
            var line = document.GetLineByOffset(caretOffset);
            return caretOffset == line.Offset && line.LineNumber > 1;
        }

        // Entf am Zeilenende wuerde die naechste Zeile mit der aktuellen
        // verschmelzen -> Zeilenanzahl wuerde sinken.
        private bool CaretWouldMergeWithNextLine()
        {
            var document = EditorBox.Document;
            int caretOffset = EditorBox.CaretOffset;
            var line = document.GetLineByOffset(caretOffset);
            bool isLastLine = line.LineNumber == document.LineCount;
            return caretOffset == line.EndOffset && !isLastLine;
        }

        // Prueft, ob das Entfernen der aktuellen Selektion (Backspace, Entf,
        // Ausschneiden, oder Ueberschreiben mit leerem Text) eine Zeile auf
        // 0 Zeichen reduzieren wuerde. Mehrzeilige Selektionen werden
        // bereits an anderer Stelle (SelectionSpansMultipleLines) blockiert,
        // daher hier nur die Pruefung fuer eine einzelne Zeile.
        private bool SelectionWouldEmptyLine()
        {
            if (EditorBox.SelectionLength <= 0)
                return false;

            var document = EditorBox.Document;
            var line = document.GetLineByOffset(EditorBox.SelectionStart);
            int selectionEnd = EditorBox.SelectionStart + EditorBox.SelectionLength;

            if (selectionEnd > line.EndOffset)
                return false; // mehrzeilig, wird anderswo behandelt

            int remaining = line.Length - EditorBox.SelectionLength;
            return remaining <= 0;
        }

        // Backspace ohne Selektion: wenn die Zeile nur noch dieses eine
        // Zeichen enthaelt (und kein Zeilenumbruch geloescht wird, das ist
        // separat abgesichert), wuerde die Zeile dadurch leer werden.
        private bool BackspaceWouldEmptyLine()
        {
            var document = EditorBox.Document;
            int caretOffset = EditorBox.CaretOffset;
            var line = document.GetLineByOffset(caretOffset);
            return caretOffset != line.Offset && line.Length == 1;
        }

        // Entf ohne Selektion: analog zu BackspaceWouldEmptyLine, nur fuer
        // das Loeschen des Zeichens rechts vom Cursor.
        private bool DeleteWouldEmptyLine()
        {
            var document = EditorBox.Document;
            int caretOffset = EditorBox.CaretOffset;
            var line = document.GetLineByOffset(caretOffset);
            return caretOffset != line.EndOffset && line.Length == 1;
        }

        // Laedt alle verfuegbaren Laufwerke als Wurzelknoten
        private void LoadDrives()
        {
            // Baum leeren, damit LoadDrives auch beim erneuten Aufruf
            // (z.B. bei F5/Refresh) keine doppelten Laufwerksknoten anlegt.
            DirectoryTree.Items.Clear();

            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;

                var item = new TreeViewItem
                {
                    Header = drive.Name,
                    Tag = drive.RootDirectory.FullName
                };

                // Dummy-Kind, damit der Expand-Pfeil erscheint (echtes Laden erst beim Aufklappen)
                item.Items.Add(new TreeViewItem { Header = "..." });
                item.Expanded += Directory_Expanded;

                DirectoryTree.Items.Add(item);
            }
        }

        // Wird beim Aufklappen eines Knotens aufgerufen -> laedt Unterordner nach (Lazy Loading)
        private void Directory_Expanded(object sender, RoutedEventArgs e)
        {
            var item = (TreeViewItem)sender;

            // Schon geladen? (kein Dummy-Kind mehr) -> nichts tun
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
                // Ordner ohne Zugriffsrechte einfach ueberspringen
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fehler beim Lesen von '{path}':\n{ex.Message}");
            }
        }

        // Wird aufgerufen, wenn im Baum ein Verzeichnis ausgewaehlt wird.
        // Die Dateien des Verzeichnisses werden als Text (eine pro Zeile) in
        // die Editor-Box geschrieben. Der Text ist danach frei editierbar.
        private void DirectoryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (DirectoryTree.SelectedItem is not TreeViewItem selected)
                return;

            string path = (string)selected.Tag;

            try
            {
                var files = Directory.GetFiles(path);
                var fileNames = files.Select(f => Path.GetFileName(f) ?? string.Empty).ToList();

                // Merken, welche Datei zu welcher Zeile gehoert - wird von
                // "Apply changes" benoetigt, um Aenderungen zuzuordnen.
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

            // Nur speichern, wenn die Auswahl durch den Benutzer erfolgt ist,
            // nicht waehrend wir selbst beim Start den Baum aufklappen.
            if (!_isRestoringPath)
                SaveLastPath(path);
        }

        // ---------- Zuletzt gewaehlten Pfad in der Registry merken ----------

        private void SaveLastPath(string path)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
                key?.SetValue(RegistryValueName, path);
            }
            catch (Exception ex)
            {
                // Registry-Zugriff kann in Ausnahmefaellen fehlschlagen (z.B.
                // Gruppenrichtlinien) - dann speichern wir eben nicht.
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

        // Liest den gespeicherten Pfad. Existiert er nicht mehr, wird
        // schrittweise das naechst uebergeordnete Verzeichnis probiert,
        // bis eines gefunden wird, das existiert (oder nichts mehr uebrig ist).
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

        // Klappt den Baum Schritt fuer Schritt auf, bis der gewuenschte Pfad
        // erreicht ist, und waehlt den entsprechenden Knoten aus.
        private void SelectPathInTree(string targetPath)
        {
            string fullPath = Path.GetFullPath(targetPath);
            string rootName = Path.GetPathRoot(fullPath) ?? string.Empty;

            // Passenden Laufwerks-Knoten suchen (Tag ist der volle Root-Pfad, z.B. "C:\")
            TreeViewItem? current = DirectoryTree.Items
                .OfType<TreeViewItem>()
                .FirstOrDefault(i => string.Equals((string)i.Tag, rootName, StringComparison.OrdinalIgnoreCase));

            if (current == null)
                return;

            // Restlichen Pfad in einzelne Ordnernamen zerlegen, z.B. "Users\Max\Projekte"
            string remainder = fullPath.Substring(rootName.Length).Trim(Path.DirectorySeparatorChar);
            var segments = remainder.Length == 0
                ? Array.Empty<string>()
                : remainder.Split(Path.DirectorySeparatorChar);

            foreach (var segment in segments)
            {
                // Aufklappen loest Directory_Expanded aus -> laedt die Kinder nach (Lazy Loading)
                current.IsExpanded = true;

                var next = current.Items
                    .OfType<TreeViewItem>()
                    .FirstOrDefault(i => string.Equals(i.Header?.ToString(), segment, StringComparison.OrdinalIgnoreCase));

                if (next == null)
                    break; // Ordner z.B. inzwischen umbenannt -> auf letztem bekannten Stand bleiben

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

        // Sucht ab der aktuellen Cursorposition (case-insensitiv), springt am
        // Ende des Texts wieder an den Anfang. Im Regex-Modus wird der
        // Suchbegriff als regulaerer Ausdruck interpretiert.
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

            // Nicht gefunden -> von vorne bis zur urspruenglichen Position suchen
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

            // Sichtbar scrollen
            int line = EditorBox.Document.GetLineByOffset(index).LineNumber;
            EditorBox.ScrollToLine(line);

            return true;
        }

        // Versucht, das Suchmuster als Regex zu kompilieren. Bei einem
        // ungueltigen Muster wird eine Fehlermeldung in _findReplaceStatus gesetzt.
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

        // Ersetzt die aktuell markierte Fundstelle (falls sie zum Suchbegriff
        // passt) und springt danach zur naechsten Fundstelle. Im Regex-Modus
        // koennen im Ersetzen-Feld $1, $2, ... auf Gruppen des Suchmusters
        // verweisen.
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

                // Nur ersetzen, wenn die aktuelle Selektion tatsaechlich
                // exakt einem Treffer des Musters entspricht.
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

        // Ersetzt alle Vorkommen im gesamten Text. Im Regex-Modus wird das
        // Suchmuster pro Zeile angewendet (Ersetzen-Feld kann $1, $2, ...
        // fuer Gruppen aus dem Suchmuster verwenden) - ideal z.B. um
        // "praefix(gruppe).ext" zeilenweise in "(gruppe)praefix.ext"
        // umzuwandeln, unabhaengig von der Laenge des Praefix.
        //
        // Im Literal-Modus wird jede Fundstelle einzeln ueber Document.Replace
        // ersetzt, statt den kompletten Text neu zu setzen - dadurch werden
        // die ersetzten Zeilen (wie bei "Ersetzen") blau markiert, und die
        // Zeilenanzahl-/Leerzeilen-Pruefungen aus Document_Changed greifen
        // automatisch.
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
                    // Rueckwaerts iterieren, damit sich die Offsets der noch
                    // nicht bearbeiteten Zeilen durch vorherige Ersetzungen
                    // nicht verschieben.
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

            // Erst alle Fundstellen sammeln ...
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

            // ... und von hinten nach vorne ersetzen, damit sich die Offsets
            // der noch nicht bearbeiteten Fundstellen nicht verschieben.
            // BeginUpdate/EndUpdate fasst alles zu einem einzigen Undo-
            // Schritt zusammen; Document_Changed feuert dabei trotzdem fuer
            // jede einzelne Ersetzung.
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

        // Vergleicht die aktuellen Editor-Zeilen mit den beim Laden
        // gemerkten Original-Dateinamen, zeigt bei Unterschieden einen
        // Bestaetigungsdialog und benennt bei "Yes" die entsprechenden
        // Dateien auf Dateisystemebene um. Danach wird neu geladen.
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

            // Aktuelle Zeilen direkt aus dem Dokument lesen (robuster als
            // EditorBox.Text zu splitten, unabhaengig von der genauen
            // Zeilenumbruch-Darstellung).
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

            // Leere/nur-Leerzeichen-Zeilen abfangen (sollte durch die
            // Editor-Schutzmechanismen eigentlich nicht vorkommen).
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

            // Nur tatsaechlich geaenderte Zeilen sind umzubenennende Dateien.
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

            // Ungueltige Dateinamen vorab pruefen.
            var invalidChars = Path.GetInvalidFileNameChars();
            var invalidNames = changes.Where(c => c.NewName.IndexOfAny(invalidChars) >= 0).ToList();
            if (invalidNames.Count > 0)
            {
                string list = string.Join("\n", invalidNames.Select(c => $"'{c.NewName}' enthaelt ungueltige Zeichen"));
                MessageBox.Show($"Folgende neue Dateinamen sind ungueltig:\n\n{list}",
                    "Klingeling File Renamer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Doppelte Zielnamen erkennen (case-insensitiv, wie im
            // Dateisystem ueblich) - betrachtet alle Zeilen, nicht nur die
            // geaenderten, damit eine umbenannte Datei auch nicht mit einer
            // unveraenderten kollidiert.
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

            // Bestaetigungsdialog mit der Liste aller Aenderungen anzeigen.
            var dialog = new RenameConfirmDialog(changes) { Owner = this };
            if (dialog.ShowDialog() != true)
                return;

            // Tatsaechliche Umbenennung auf Dateisystemebene.
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
                        // File.Move scheitert bei reiner Gross-/Kleinschreibungs-
                        // Aenderung auf einem case-insensitiven Dateisystem, da
                        // das Ziel scheinbar schon existiert (dieselbe Datei).
                        // Deshalb ueber einen temporaeren Namen umbenennen.
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

            // Abschliessender Refresh, wie gewuenscht.
            Refresh_Click(this, new RoutedEventArgs());
        }

        // ---------- Makro-Aufzeichnung/-Wiedergabe ----------

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

            // Reine Cursorbewegung (Pfeiltasten, Home/End, Klick, …) speichern.
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

        // Spielt das aufgezeichnete Makro ab der aktuellen Cursorposition ab.
        // Cursor-Bewegungen (z.B. eine Zeile nach unten) werden mit ausgefuehrt,
        // danach die zugehoerigen Textaenderungen.
        private void PlayMacro_Click(object sender, RoutedEventArgs e)
        {
            if (_isRecordingMacro)
            {
                MessageBox.Show("Recording still active – stop it first (Alt+K).",
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
                        // Cursor analog zur normalen Eingabe hinter dem eingefuegten Text.
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

        // Bewegt den logischen Cursor um lineDelta Zeilen und columnDelta Spalten.
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
            // Aktuell ausgewaehlten Pfad merken, damit der Baum nach dem
            // Neuladen wieder an derselben Stelle steht, statt auf die
            // reine Laufwerksliste zurueckzufallen.
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

            // Kein Zeilenumbruch ? kein Rechteck nötig
            if (text.IndexOf('\n') < 0 && text.IndexOf('\r') < 0)
                return false;

            var textArea = EditorBox.TextArea;

            // Nur bei leerer Auswahl (Caret) oder bereits aktiver Blockselektion
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

            // Mehrzeiliger Text ? versuchen, als Rechteck einzufügen und normales Einfügen abbrechen
            if (TryPasteAsRectangle())
            {
                e.CancelCommand();
            }
            else
            {
                // Nicht als Rechteck möglich ? abbrechen, damit keine Zeilen hinzukommen
                e.CancelCommand();
            }
        }

    }  // end MainWindow : Window
}

