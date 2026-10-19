using Zombies.Engine.Render;

namespace Zombies.Engine.Tests;

public sealed class PlayerAimTests
{
    [Fact]
    public void MouseTurns_AddUp_EvenWhenTheCameraIsPutBackEveryFrame()
    {
        var aim = new PlayerAim();
        var camera = new Camera();
        for (var frame = 0; frame < 100; frame++)
        {
            aim.Turn(4f, 0f);

            // What the first-person view does each frame: place the camera at the predicted player, whose yaw lags the aim.
            camera.Yaw = 0f;
            camera.Face(aim);
        }

        Assert.Equal(100 * 4f * 0.0025f, aim.Yaw, 4);
        Assert.Equal(aim.Yaw, camera.Yaw);
    }

    [Fact]
    public void LookingUpAndDown_StopsShortOfStraightUpOrDown()
    {
        var aim = new PlayerAim();
        aim.Turn(0f, -100000f);
        Assert.True(aim.Pitch is > 1.5f and < MathF.PI / 2f);
        aim.Turn(0f, 200000f);
        Assert.True(aim.Pitch is < -1.5f and > -MathF.PI / 2f);
    }

    [Fact]
    public void MovingTheMouseDown_LooksDown()
    {
        var aim = new PlayerAim();
        aim.Turn(0f, 10f);
        Assert.True(aim.Pitch < 0f);
    }
}
