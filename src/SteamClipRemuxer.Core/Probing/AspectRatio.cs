namespace SteamClipRemuxer.Core.Probing;

/// <summary>
/// An exact aspect ratio, always stored in lowest terms.
/// </summary>
public readonly record struct AspectRatio
{
    public int Numerator { get; }
    public int Denominator { get; }

    public AspectRatio(int numerator, int denominator)
    {
        if (denominator == 0)
            throw new ArgumentOutOfRangeException(nameof(denominator), "Aspect ratio denominator cannot be zero.");
        if (numerator <= 0)
            throw new ArgumentOutOfRangeException(nameof(numerator), "Aspect ratio numerator must be positive.");
        if (denominator < 0)
            throw new ArgumentOutOfRangeException(nameof(denominator), "Aspect ratio denominator must be positive.");

        int g = Gcd(numerator, denominator);
        Numerator = numerator / g;
        Denominator = denominator / g;
    }

    public static readonly AspectRatio Square = new(1, 1);
    public static readonly AspectRatio Widescreen = new(16, 9);

    public double Value => (double)Numerator / Denominator;

    /// <summary>
    /// Parses ffprobe's "N:D" or "N/D" form. Returns null for absent or degenerate values
    /// ("0:1" is what ffprobe emits when a ratio is unknown).
    /// </summary>
    public static AspectRatio? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string[] parts = text.Split(':', '/');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], out int n) || !int.TryParse(parts[1], out int d)) return null;
        if (n <= 0 || d <= 0) return null;

        return new AspectRatio(n, d);
    }

    /// <summary>
    /// The display aspect a frame of the given pixel dimensions produces under this
    /// sample aspect ratio: DAR = SAR * (width / height).
    /// </summary>
    public AspectRatio DisplayAspectFor(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        return new AspectRatio(Numerator * width, Denominator * height);
    }

    /// <summary>
    /// The sample aspect ratio required to make the given pixel dimensions display at
    /// <paramref name="target"/>. Rearranged from DAR = SAR * (W / H), so
    /// SAR = DAR * (H / W).
    /// </summary>
    /// <remarks>
    /// The dimensions passed here must be the SOURCE's own pixel dimensions. Computing this
    /// from a desired output resolution produces a ratio unrelated to the actual pixels; that
    /// was the defect in the original implementation.
    /// </remarks>
    public static AspectRatio SarForTargetDisplay(int width, int height, AspectRatio target)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        return new AspectRatio(target.Numerator * height, target.Denominator * width);
    }

    private static int Gcd(int a, int b)
    {
        a = Math.Abs(a);
        b = Math.Abs(b);
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }
        return a == 0 ? 1 : a;
    }

    public override string ToString() => $"{Numerator}:{Denominator}";
}
