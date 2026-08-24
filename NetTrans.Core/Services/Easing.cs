namespace NetTrans.Services;

/// <summary>
/// The handoff uses one easing curve everywhere: cubic-bezier(.32,.72,0,1).
/// XAML animations express it as a KeySpline; window moves are driven from C#
/// and need to evaluate it directly, which is what this does.
/// </summary>
public static class Easing
{
    private const double X1 = 0.32, Y1 = 0.72, X2 = 0.0, Y2 = 1.0;

    /// <summary>Maps linear progress 0..1 through cubic-bezier(.32,.72,0,1).</summary>
    public static double Standard(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;

        // Solve x(u) = t for u by Newton-Raphson, then return y(u).
        double u = t;
        for (int i = 0; i < 6; i++)
        {
            double x = BezierAt(u, X1, X2) - t;
            double dx = BezierSlopeAt(u, X1, X2);
            if (Math.Abs(dx) < 1e-6) break;
            u -= x / dx;
        }

        return BezierAt(Math.Clamp(u, 0, 1), Y1, Y2);
    }

    private static double BezierAt(double u, double p1, double p2) =>
        (((1 - 3 * p2 + 3 * p1) * u + (3 * p2 - 6 * p1)) * u + 3 * p1) * u;

    private static double BezierSlopeAt(double u, double p1, double p2) =>
        3 * (1 - 3 * p2 + 3 * p1) * u * u + 2 * (3 * p2 - 6 * p1) * u + 3 * p1;
}
