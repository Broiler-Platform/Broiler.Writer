using System.Collections.Generic;
using System.Linq;
using Broiler.Documents.Model;
using Broiler.Graphics;
using Broiler.UI;
using Broiler.UI.Menu;
using Xunit;

namespace Broiler.Writer.FormatCodes.Tests;

/// <summary>
/// Insert Code → Page break, end to end from the menu.
/// </summary>
/// <remarks>
/// <para>
/// The pane has always drawn <c>[Page Break]</c> for a paragraph that states one
/// and offered to delete it, and there was no way to state one. The component
/// half of that was a missing arm in
/// <c>FormatCodeInsertPalette.Create</c> (Broiler.Documents#88); this is the
/// menu that reaches it.
/// </para>
/// <para>
/// It is also the first <em>paragraph</em> intent the Writer's Insert Code
/// palette raises - every other entry on that menu is an inline style or a text
/// replacement - so the assertions go through to the document rather than
/// stopping at the dispatcher.
/// </para>
/// </remarks>
public sealed class WriterInsertPageBreakTests
{
    private static WriterApp CreateApp() => new(
        new WriterUiHost(() => new BSize(1120, 780), () => 1, () => { }, _ => { }),
        () => { });

    /// <summary>Every menu item under the Insert Code submenu, by id.</summary>
    private static List<string> InsertCodeIds(WriterApp app)
    {
        UiMenuItem? insertCode = Descend(app.Menu.Items).FirstOrDefault(item => item.Id == "insert-code");
        Assert.NotNull(insertCode);
        return insertCode!.Children.Select(child => child.Id).ToList();
    }

    private static IEnumerable<UiMenuItem> Descend(IEnumerable<UiMenuItem> items)
    {
        foreach (UiMenuItem item in items)
        {
            yield return item;
            foreach (UiMenuItem child in Descend(item.Children))
                yield return child;
        }
    }

    [Fact(Timeout = 600000)]
    public void The_Menu_Offers_A_Page_Break_After_The_Smaller_Ones()
    {
        List<string> ids = InsertCodeIds(CreateApp());

        Assert.Contains("code-page-break", ids);

        // The three breaks in ascending order, which is the only reason the
        // position is worth asserting: a page break under a line break reads as
        // one list, and anywhere else reads as an afterthought.
        Assert.Equal(
            ["code-line-break", "code-paragraph-break", "code-page-break"],
            ids.SkipWhile(id => id != "code-line-break").ToArray());
    }

    [Fact(Timeout = 600000)]
    public void The_Command_States_The_Break_On_The_Caret_Paragraph()
    {
        using WriterApp app = CreateApp();
        app.Editor.SetPlainText("first\nsecond");
        app.Editor.Selection = RichTextRange.Caret(app.Document.End);

        Assert.True(app.Menu.CommandDispatcher!.TryExecute("formatcodes.insert.page-break"));

        Assert.False(app.Document.Paragraphs[0].Style.PageBreakBefore);
        Assert.True(app.Document.Paragraphs[1].Style.PageBreakBefore);
    }

    [Fact(Timeout = 600000)]
    public void The_Pane_Then_Draws_The_Code_It_Could_Only_Delete_Before()
    {
        using WriterApp app = CreateApp();
        app.Editor.SetPlainText("first\nsecond");
        app.Editor.Selection = RichTextRange.Caret(app.Document.End);

        // The pane is visible by default, so nothing has to be turned on here -
        // toggling would turn it off.
        app.Menu.CommandDispatcher!.TryExecute("formatcodes.insert.page-break");

        Assert.Contains(
            app.RenderFrame().Commands.OfType<BRenderCommand.DrawText>(),
            command => command.Text.Text.Contains("[Page Break]", System.StringComparison.Ordinal));
    }

    [Fact(Timeout = 600000)]
    public void The_Document_View_Is_Unchanged_By_It()
    {
        // Pinned rather than left for a reviewer to discover. StandardRichEdit
        // draws one sheet that grows instead of starting a second, so a break it
        // is told about has nowhere to show. The break is real - the assertions
        // above are on the document - and it reaches paper through the codecs,
        // the CLI layout and the PDF writer. It just does not reach this surface,
        // and will not until a paginating one exists.
        using WriterApp app = CreateApp();
        app.Editor.SetPlainText("first\nsecond");
        app.Editor.Selection = RichTextRange.Caret(app.Document.End);

        // The pane is on by default and it *does* change - drawing the code is
        // its whole job - so it has to come off for this to be a question about
        // the document view rather than about the pane beside it.
        Assert.True(app.Menu.CommandDispatcher!.TryExecute("view.formatting-codes"));

        (string Text, double X, double Y)[] before = Drawn(app);
        Assert.True(app.Menu.CommandDispatcher.TryExecute("formatcodes.insert.page-break"));
        (string Text, double X, double Y)[] after = Drawn(app);

        Assert.True(app.Document.Paragraphs[1].Style.PageBreakBefore);
        Assert.Equal(before, after);
    }

    private static (string Text, double X, double Y)[] Drawn(WriterApp app) =>
        app.RenderFrame().Commands
            .OfType<BRenderCommand.DrawText>()
            .Select(command => (command.Text.Text, command.Origin.X, command.Origin.Y))
            .ToArray();
}
