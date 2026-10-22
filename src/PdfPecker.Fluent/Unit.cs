namespace PdfPecker.Fluent;

/// <summary>A unit for lengths. Every length method takes points unless a unit is given; one point is 1/72 inch.</summary>
public enum Unit
{
    /// <summary>A PDF point, 1/72 inch.</summary>
    Point,

    /// <summary>A metre.</summary>
    Meter,

    /// <summary>A centimetre.</summary>
    Centimetre,

    /// <summary>A millimetre.</summary>
    Millimetre,

    /// <summary>A foot, 12 inches.</summary>
    Feet,

    /// <summary>An inch, 72 points.</summary>
    Inch,

    /// <summary>A mil, 1/1000 inch.</summary>
    Mil,
}

/// <summary>Converts lengths to points and checks them, so a NaN or a huge value fails at the call that passed it.</summary>
internal static class Length
{
    /// <summary>The largest length accepted, in points: far beyond any page (PDF limits pages to 14,400 points).</summary>
    public const float MaxPoints = 1_000_000f;

    public static float ToPoints(float value, Unit unit) => value * unit switch
    {
        Unit.Point => 1f,
        Unit.Meter => 7200f / 2.54f,
        Unit.Centimetre => 72f / 2.54f,
        Unit.Millimetre => 72f / 25.4f,
        Unit.Feet => 864f,
        Unit.Inch => 72f,
        Unit.Mil => 0.072f,
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown unit."),
    };

    /// <summary>A length of zero or more, in points.</summary>
    public static float Points(float value, Unit unit, string paramName)
    {
        float points = ToPoints(value, unit);
        if (!float.IsFinite(points) || points < 0 || points > MaxPoints)
            throw new ArgumentOutOfRangeException(paramName, value, "A length must be a finite number from 0 to 1,000,000 points.");
        return points;
    }

    /// <summary>A positive length, in points.</summary>
    public static float Positive(float value, Unit unit, string paramName)
    {
        float points = Points(value, unit, paramName);
        if (points <= 0) throw new ArgumentOutOfRangeException(paramName, value, "The length must be greater than zero.");
        return points;
    }

    /// <summary>A positive proportion, such as a relative column width.</summary>
    public static float Weight(float value, string paramName)
    {
        if (!float.IsFinite(value) || value <= 0 || value > MaxPoints)
            throw new ArgumentOutOfRangeException(paramName, value, "The size must be a finite number greater than zero.");
        return value;
    }
}
