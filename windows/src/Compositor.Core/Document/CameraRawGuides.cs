namespace Compositor.Core.Document;

/// <summary>How the geometry group is told what to straighten: by the amounts on its sliders, or by lines
/// drawn on the picture.</summary>
public enum CameraRawUprightMode
{
    Off,
    Guided,
}

/// <summary>
/// A line drawn on the picture that should be level or upright, in fractions of the layer's own width and
/// height: (0, 0) is its top left, so y runs downward as the document's does. The Mac build's own note says
/// its guides measure up from the lower left, but the point they are divided out of is an image-space one,
/// which runs downward, and the turn it works out from a guide is only right that way round.
/// </summary>
public readonly record struct CameraRawGeometryGuide(double StartX, double StartY, double EndX, double EndY)
{
    /// <summary>How long the line is, in the same fractions of the picture's sides.</summary>
    public double Length
    {
        get
        {
            var dx = EndX - StartX;
            var dy = EndY - StartY;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    /// <summary>Whether the line is long enough to read: a stub must not warp the picture.</summary>
    public bool IsUsable => Length > 0.01;

    /// <summary>The line's angle below the horizontal, in degrees, measured the way the document's y runs.</summary>
    public double Degrees => Math.Atan2(EndY - StartY, EndX - StartX) * 180 / Math.PI;
}

/// <summary>
/// What lines drawn on the picture ask the geometry to do, worked out as the Mac build works it out: the
/// first line levels itself by turning the picture back through its own angle, and a second line steeper
/// than forty-five degrees asks for a fixed keystone of twenty-five — upright if it leans one way and across
/// if it leans the other. What the lines ask for is added to whatever the sliders say.
/// </summary>
public static class GuidedUpright
{
    /// <summary>How far a second guide asks the keystone to go, which is the Mac build's fixed twenty-five.</summary>
    public const double Keystone = 25;

    public static (double Vertical, double Horizontal, double Rotate) Corrections(
        IReadOnlyList<CameraRawGeometryGuide> guides) => Corrections(guides, 1, 1);

    /// <summary>Reads normalized guides on the image pixel grid, accounting for its aspect ratio.</summary>
    public static (double Vertical, double Horizontal, double Rotate) Corrections(
        IReadOnlyList<CameraRawGeometryGuide> guides, double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return (0, 0, 0);
        double Degrees(CameraRawGeometryGuide guide) =>
            Math.Atan2((guide.EndY - guide.StartY) * height, (guide.EndX - guide.StartX) * width) * 180 / Math.PI;
        if (guides.Count == 0) return (0, 0, 0);
        var first = guides[0];
        if (!(first.Length > 1e-4)) return (0, 0, 0);
        // A line is levelled by turning the picture back through its own angle; an angle past a quarter turn
        // is the same line read the other way round, so it is brought back inside the range a turn allows.
        var rotate = -Degrees(first);
        if (rotate > 45) rotate -= 90;
        else if (rotate < -45) rotate += 90;
        var vertical = 0.0;
        var horizontal = 0.0;
        if (guides.Count > 1)
        {
            var second = guides[1];
            if (second.Length > 1e-4)
            {
                var steep = Math.Abs(Degrees(second)) > 45;
                if (steep) vertical = Degrees(second) > 0 ? Keystone : -Keystone;
                else horizontal = Degrees(second) > 0 ? Keystone : -Keystone;
            }
        }
        return (vertical, horizontal, rotate);
    }
}
