using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace KlingelingFileRenamer
{
    // Markiert neu eingefuegten Text blau, bis die Segmente explizit
    // geleert werden (z. B. beim Speichern).
    public class NewTextColorizer : DocumentColorizingTransformer
    {
        public TextSegmentCollection<TextSegment> NewSegments { get; }

        public NewTextColorizer(TextDocument document)
        {
            NewSegments = new TextSegmentCollection<TextSegment>(document);
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            foreach (var segment in NewSegments.FindOverlappingSegments(line))
            {
                int start = System.Math.Max(segment.StartOffset, line.Offset);
                int end = System.Math.Min(segment.EndOffset, line.EndOffset);
                if (start < end)
                {
                    ChangeLinePart(start, end, element =>
                    {
                        element.TextRunProperties.SetForegroundBrush(Brushes.Blue);
                    });
                }
            }
        }
    }
}
