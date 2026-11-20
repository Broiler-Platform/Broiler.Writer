using Broiler.Graphics.Geometry;
using Broiler.Graphics.RenderList;

namespace Broiler.Writer.FormatCodes.Tests;

internal static class WriterRenderCoordinates
{
    internal static BMatrix3x2 TransformFor(BRenderList frame, BRenderCommand target)
    {
        var stack = new Stack<BMatrix3x2>();
        var transform = BMatrix3x2.Identity;
        foreach (var command in frame.Commands)
        {
            if (ReferenceEquals(command, target)) return transform;
            if (command is BRenderCommand.PushTransform push)
            {
                stack.Push(transform);
                transform = push.Transform * transform;
            }
            else if (command is BRenderCommand.PopTransform) transform = stack.Pop();
        }
        throw new InvalidOperationException("Command is not in the frame.");
    }

    internal static BRect ImageBounds(BRenderList frame, BRenderCommand.DrawImage image)
    {
        var transform = TransformFor(frame, image);
        var start = transform.Transform(new BPoint(image.Destination.Left, image.Destination.Top));
        var end = transform.Transform(new BPoint(image.Destination.Right, image.Destination.Bottom));
        return new(start.X, start.Y, end.X - start.X, end.Y - start.Y);
    }

    internal static IEnumerable<BRenderCommand.DrawText> ScreenText(BRenderList frame) =>
        frame.Commands.OfType<BRenderCommand.DrawText>().Select(text => text with { Origin = TransformFor(frame, text).Transform(text.Origin) });
}
