using System;
using System.Collections.Generic;
using System.Linq;
using Broiler.Graphics;
using Broiler.Writer.FormatCodes;
using Xunit;

namespace Broiler.Writer.FormatCodes.Tests;

/// <summary>
/// Coverage for the toolbar icon geometry.
/// </summary>
/// <remarks>
/// What a test can usefully say about a drawing is narrow: whether it drew anything, whether it
/// stayed inside the box it was given, and whether it used the colour it was handed. Whether it
/// looks like a folder is not something to assert, and pretending otherwise produces a test that
/// breaks every time the folder is improved. So these are the invariants a caller depends on -
/// an icon that silently draws nothing, or that paints over its neighbours on the bar, or that
/// ignores the disabled colour, is a real defect and each is caught here.
/// </remarks>
public sealed class WriterIconTests
{
    public static TheoryData<string> IconNames()
    {
        var data = new TheoryData<string>();
        foreach (string name in All.Keys)
            data.Add(name);
        return data;
    }

    private static readonly Dictionary<string, Action<BRenderList, BRect, BColor>> All = new(StringComparer.Ordinal)
    {
        ["New"] = WriterIcons.NewDocument,
        ["Open"] = WriterIcons.OpenDocument,
        ["Save"] = WriterIcons.Save,
        ["Save as"] = WriterIcons.SaveAs,
        ["Undo"] = WriterIcons.Undo,
        ["Redo"] = WriterIcons.Redo,
        ["Clear formatting"] = WriterIcons.ClearFormatting,
        ["Align left"] = WriterIcons.AlignLeft,
        ["Align center"] = WriterIcons.AlignCenter,
        ["Align right"] = WriterIcons.AlignRight,
        ["Justify"] = WriterIcons.AlignJustify,
        ["Bullets"] = WriterIcons.BulletList,
        ["Numbered"] = WriterIcons.NumberedList,
        ["Indent"] = WriterIcons.Indent,
        ["Outdent"] = WriterIcons.Outdent,
    };

    [Theory(Timeout = 600000)]
    [MemberData(nameof(IconNames))]
    public void Every_Icon_Draws_Something(string name)
    {
        var list = new BRenderList();
        All[name](list, new BRect(0, 0, 16, 16), BColor.Black);

        Assert.NotEmpty(list.Commands);
        list.Validate();
    }

    [Theory(Timeout = 600000)]
    [MemberData(nameof(IconNames))]
    public void Every_Icon_Stays_Inside_The_Box_It_Was_Given(string name)
    {
        var box = new BRect(40, 24, 16, 16);
        var list = new BRenderList();
        All[name](list, box, BColor.Black);

        foreach (BRect drawn in DrawnGeometry(list))
        {
            // A toolbar packs these a few pixels apart, so an icon that overruns its box paints
            // over the button next to it.
            Assert.True(drawn.Left >= box.Left - 0.01, $"{name} drew at x={drawn.Left}, left of its box at {box.Left}.");
            Assert.True(drawn.Top >= box.Top - 0.01, $"{name} drew at y={drawn.Top}, above its box at {box.Top}.");
            Assert.True(drawn.Right <= box.Right + 0.01, $"{name} drew to x={drawn.Right}, past its box at {box.Right}.");
            Assert.True(drawn.Bottom <= box.Bottom + 0.01, $"{name} drew to y={drawn.Bottom}, below its box at {box.Bottom}.");
        }
    }

    [Theory(Timeout = 600000)]
    [MemberData(nameof(IconNames))]
    public void Every_Icon_Uses_Only_The_Colour_It_Was_Given(string name)
    {
        BColor ink = BColor.FromArgb(0xFF, 0x12, 0x34, 0x56);
        var list = new BRenderList();
        All[name](list, new BRect(0, 0, 16, 16), ink);

        // The control resolves one foreground for its state - hover, pressed, disabled - and hands
        // it over. An icon that hard-coded a colour would stay black on a disabled button.
        foreach (BColor used in DrawnColours(list))
            Assert.Equal(ink, used);
    }

    [Theory(Timeout = 600000)]
    [MemberData(nameof(IconNames))]
    public void Every_Icon_Scales_With_Its_Box(string name)
    {
        var small = new BRenderList();
        var large = new BRenderList();
        All[name](small, new BRect(0, 0, 16, 16), BColor.Black);
        All[name](large, new BRect(0, 0, 32, 32), BColor.Black);

        Assert.Equal(small.Commands.Count, large.Commands.Count);

        BRect smallBounds = Union(DrawnGeometry(small));
        BRect largeBounds = Union(DrawnGeometry(large));
        Assert.True(
            largeBounds.Width > smallBounds.Width * 1.5,
            $"{name} did not grow with its box: {smallBounds.Width} then {largeBounds.Width}.");
    }

    private static IEnumerable<BRect> DrawnGeometry(BRenderList list)
    {
        foreach (BRenderCommand command in list.Commands)
        {
            switch (command)
            {
                case BRenderCommand.FillRect c:
                    yield return c.Rect;
                    break;
                case BRenderCommand.FillRoundedRect c:
                    yield return c.Rect;
                    break;
                case BRenderCommand.StrokeRoundedRect c:
                    yield return c.Rect;
                    break;
                case BRenderCommand.FillTriangle c:
                    yield return BRect.FromLTRB(
                        Math.Min(c.A.X, Math.Min(c.B.X, c.C.X)),
                        Math.Min(c.A.Y, Math.Min(c.B.Y, c.C.Y)),
                        Math.Max(c.A.X, Math.Max(c.B.X, c.C.X)),
                        Math.Max(c.A.Y, Math.Max(c.B.Y, c.C.Y)));
                    break;
            }
        }
    }

    private static IEnumerable<BColor> DrawnColours(BRenderList list)
    {
        foreach (BRenderCommand command in list.Commands)
        {
            switch (command)
            {
                case BRenderCommand.FillRect c:
                    yield return c.Color;
                    break;
                case BRenderCommand.FillRoundedRect c:
                    yield return c.Color;
                    break;
                case BRenderCommand.StrokeRoundedRect c:
                    yield return c.Color;
                    break;
                case BRenderCommand.FillTriangle c:
                    yield return c.Color;
                    break;
                case BRenderCommand.DrawText c:
                    yield return c.Text.Color;
                    break;
            }
        }
    }

    private static BRect Union(IEnumerable<BRect> rects)
    {
        BRect[] all = rects.ToArray();
        Assert.NotEmpty(all);
        return BRect.FromLTRB(
            all.Min(r => r.Left),
            all.Min(r => r.Top),
            all.Max(r => r.Right),
            all.Max(r => r.Bottom));
    }
}
