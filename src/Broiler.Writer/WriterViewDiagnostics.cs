using System.Collections.Generic;
using System.Linq;
using Broiler.Documents;
using Broiler.Documents.Model;

namespace Broiler.Writer;

internal static class WriterViewDiagnostics
{
    internal const string EmptyMessage = "No editing-view limitations detected for the formatting checked.";

    internal static IReadOnlyList<DocumentDiagnostic> Collect(RichTextDocument document, bool showsHiddenText = true, bool isPaginated = true)
    {
        var notes = new List<DocumentDiagnostic>();
        var styles = document.Paragraphs.Select(paragraph => paragraph.Style);
        bool unavailablePicture = false;
        document.MapPictures(image => { unavailablePicture |= image.IsMissingPayload; return image; });
        if (unavailablePicture)
            Add("picture-unavailable", "A picture payload is missing or was blocked by the document resource policy. Its placeholder and alternative text remain available; the picture properties cannot restore the missing data.");
        if (WriterGraphics.Shapes(document).Any())
        {
            Add("graphics", "Use Graphics > Edit objects on page to select, move and resize floating objects in paginated coordinates. Complex grouped, rotated, inline-frame, auto-width and linked-frame objects retain their imported data and have restricted editing.");
            if (!WriterGraphics.TracksParagraphAnchors)
                Add("graphics-anchors", "This Documents package does not move floating-object paragraph anchors after paragraph insertion or deletion. Update Broiler.Documents to include the anchor-tracking fix, or adjust the anchor in Graphics > Object size and position after those edits.");
        }

        if (!isPaginated)
        {
            if (document.Sections.Any(section => section.ColumnCount > 1 || section.Page is { ColumnCount: > 1 } ||
                    section.PageStyles?.Styles.Any(style => style.Page is { ColumnCount: > 1 }) == true) ||
                document.PageGeometry is { ColumnCount: > 1 })
                Add("columns", "The editor shows body text in a single page column. The document keeps its column layout.");

            if (styles.Any(style => style.PageBreakBefore || style.ColumnBreakBefore || style.KeepWithNext ||
                style.KeepTogether || style.WidowLines is > 0 || style.OrphanLines is > 0) ||
                document.Sections.Any(section => section.BreakType != SectionBreakType.Continuous))
                Add("pagination", "The editor shows text as a continuous flow. Page and column breaks and paragraph keep rules apply in paginated output where supported.");
        }

        if (document.Sections.Any(section => section.Hidden && section.ParagraphCount > 0))
            Add("hidden-section", isPaginated
                ? "Hidden sections are omitted from the paginated view. Their source text remains in the document and Formatting Codes."
                : "The editor can show text from sections marked hidden. The document keeps their visibility setting.");

        if (document.Paragraphs.Any(paragraph => paragraph.Runs.Any(run => run.Style.Hidden)))
            Add("hidden-text", isPaginated
                ? "The paginated view hides marked runs. Their source text remains available in Formatting Codes."
                : showsHiddenText
                ? "This editing view displays text marked hidden. Visibility in exported documents depends on the file format."
                : "This editing view hides marked runs. Their source text remains available to selection and copy.");

        if (!isPaginated)
        {
            if (!document.RunningContent.IsEmpty ||
                document.Sections.Any(section => section.RunningContent is { IsEmpty: false } ||
                    section.PageStyles?.Styles.Any(style => !style.RunningContent.IsEmpty) == true))
                Add("running-content", "The editor can show one header and footer preview. Page-specific and section-specific headers and footers require paginated output.");

            if (styles.Any(style => style.TabStops is { Count: > 0 }))
                Add("tabs", "The editor resolves custom stops within the continuous text area. Page boundaries and font metrics can move stops in print.");

            if (styles.Any(style => style.List is not null))
                Add("list-markers", "The editor uses the document’s list definitions. Marker positions can differ between the continuous view and print.");

            if (styles.Any(style => style.LineHeightRule != LineHeightRule.Proportional ||
                (style.LineSpacing > 0 && style.LineSpacing != 1) || style.SpacingBefore != 0 || style.SpacingAfter != 0 ||
                (style.LeftIndent is float left && (left != 0 || style.IndentLevel > 0)) ||
                style.RightIndent is < 0 or > 0 || style.FirstLineIndent is < 0 or > 0))
                Add("paragraph-layout", "The editor applies paragraph spacing, line heights, and indents in a continuous view. Print also applies page layout rules.");
        }

        return notes;

        void Add(string code, string message) => notes.Add(DocumentDiagnostic.Info("writer.view." + code, message));
    }
}
