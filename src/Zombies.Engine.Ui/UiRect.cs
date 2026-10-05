namespace Zombies.Engine.Ui;

/// <summary>A rectangle in screen pixels, with the origin at the top left.</summary>
public readonly record struct UiRect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;

    public float Bottom => Y + Height;

    /// <summary>Whether a point is inside. The left and top edges are inside and the right and bottom edges are not, so neighbours never both claim a point.</summary>
    public bool Contains(float x, float y) => x >= X && x < Right && y >= Y && y < Bottom;
}
