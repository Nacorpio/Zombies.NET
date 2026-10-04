using System.Numerics;

namespace Zombies.Engine.Render;

/// <summary>Everything the world shader needs to know about the sky at one moment.</summary>
public readonly record struct SunState(
    Vector3 DirectionToSun,
    Vector3 SunColor,
    Vector3 AmbientColor,
    Vector3 SkyColor,
    Vector3 FogColor,
    float SunIntensity)
{
    /// <summary>The sun is high enough to light the ground and cast shadows.</summary>
    public bool CastsShadows => SunIntensity > 0.02f && DirectionToSun.Y > 0.02f;
}

/// <summary>
/// Turns the time of day into sun direction and sky colors. Time is a fraction of a day: 0 is midnight, 0.25 sunrise, 0.5 noon, 0.75 sunset.
/// The sun rises in the east and sets in the west on a slightly tilted arc, so shadows are never perfectly straight along an axis.
/// </summary>
public static class SunModel
{
    private static readonly Vector3 NoonSun = new(1.00f, 0.96f, 0.88f);
    private static readonly Vector3 HorizonSun = new(1.00f, 0.55f, 0.30f);

    private static readonly Vector3 DayAmbient = new(0.50f, 0.58f, 0.72f);
    private static readonly Vector3 NightAmbient = new(0.07f, 0.09f, 0.16f);

    private static readonly Vector3 DaySky = new(0.45f, 0.68f, 0.96f);
    private static readonly Vector3 DuskSky = new(0.90f, 0.52f, 0.38f);
    private static readonly Vector3 NightSky = new(0.02f, 0.03f, 0.08f);

    public static SunState At(float timeOfDay)
    {
        var t = timeOfDay - MathF.Floor(timeOfDay);
        var angle = 2f * MathF.PI * (t - 0.25f);
        var direction = Vector3.Normalize(new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0.25f));
        var elevation = direction.Y;

        var daylight = Smooth(-0.10f, 0.25f, elevation);
        var warmth = 1f - Smooth(0.05f, 0.45f, elevation);
        var intensity = Smooth(-0.02f, 0.20f, elevation);

        var sunColor = Vector3.Lerp(NoonSun, HorizonSun, warmth);
        var ambient = Vector3.Lerp(NightAmbient, DayAmbient, daylight);
        var twilight = Smooth(-0.20f, 0f, elevation) * (1f - Smooth(0.0f, 0.35f, elevation));
        var sky = Vector3.Lerp(Vector3.Lerp(NightSky, DaySky, daylight), DuskSky, twilight * 0.7f);
        var fog = Vector3.Lerp(sky, new Vector3(0.78f, 0.84f, 0.92f), daylight * 0.35f);

        return new SunState(direction, sunColor, ambient, sky, fog, intensity);
    }

    private static float Smooth(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - (2f * t));
    }
}

/// <summary>The time of day, advancing at a configurable pace.</summary>
public sealed class DayClock(float startFraction = 0.4f, float secondsPerDay = 24f * 60f)
{
    public float Fraction { get; private set; } = startFraction - MathF.Floor(startFraction);

    public float SecondsPerDay { get; set; } = secondsPerDay;

    public bool Paused { get; set; }

    public void Advance(float seconds)
    {
        if (!Paused)
        {
            Fraction = (Fraction + (seconds / SecondsPerDay)) % 1f;
        }
    }

    public void Set(float fraction) => Fraction = fraction - MathF.Floor(fraction);
}
