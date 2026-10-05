using System.Globalization;
using System.Numerics;
using System.Text;
using Zombies.Engine.Animation;
using Zombies.Engine.Core.Modding;

namespace Zombies.Engine.Tests.Rigs;

/// <summary>A small skeleton in whole meters (8 voxels each) so the expected numbers in the tests can be worked out by hand, plus loaders for the real rigs in the base mod.</summary>
internal static class RigTestData
{
    /// <summary>Everything is a whole number of meters apart: the torso is 1 up, the head 1 above that, the arms 1 to each side, the legs 1 down.</summary>
    public const string MiniSkeleton = """
        {
          "id": "test:rig/mini",
          "bones": [
            { "name": "root" },
            { "name": "torso", "parent": "root", "part": "torso", "offset": [0, 8, 0] },
            { "name": "head", "parent": "torso", "part": "head", "offset": [0, 8, 0],
              "boxes": [ { "min": [-2, 0, -2], "size": [4, 4, 4], "uv": [0, 0] } ] },
            { "name": "arm_l", "parent": "torso", "part": "leftArm", "offset": [-8, 8, 0] },
            { "name": "hand_l", "parent": "arm_l", "offset": [0, -8, 0] },
            { "name": "arm_r", "parent": "torso", "part": "rightArm", "offset": [8, 8, 0] },
            { "name": "hand_r", "parent": "arm_r", "offset": [0, -8, 0] },
            { "name": "leg_l", "parent": "root", "part": "leftLeg", "offset": [-8, 8, 0] },
            { "name": "foot_l", "parent": "leg_l", "offset": [0, -8, 0] },
            { "name": "leg_r", "parent": "root", "part": "rightLeg", "offset": [8, 8, 0] },
            { "name": "foot_r", "parent": "leg_r", "offset": [0, -8, 0] }
          ],
          "attachPoints": [
            { "name": "held_right", "bone": "hand_r" },
            { "name": "held_left", "bone": "hand_l" }
          ],
          "walk": {
            "strideLength": 2,
            "fullSpeed": 4,
            "swings": [
              { "bone": "leg_l", "rotation": [40, 0, 0], "phase": 0, "upright": true },
              { "bone": "leg_r", "rotation": [40, 0, 0], "phase": 0.5, "upright": true },
              { "bone": "arm_l", "rotation": [-30, 0, 0], "phase": 0 },
              { "bone": "arm_r", "rotation": [-30, 0, 0], "phase": 0.5 }
            ]
          },
          "lookAt": [
            { "bone": "torso", "yawWeight": 0.25, "maxYaw": 90 },
            { "bone": "head", "yawWeight": 0.75, "pitchWeight": 1, "maxYaw": 80, "maxPitch": 80 }
          ]
        }
        """;

    public static Skeleton Mini() => Parse(MiniSkeleton);

    public static Skeleton Parse(string json)
    {
        Assert.True(Skeleton.TryParse(json, out var skeleton, out var error), error);
        return skeleton;
    }

    public static ClipSet Clips(string json)
    {
        Assert.True(ClipSet.TryParse(json, out var clips, out var error), error);
        return clips;
    }

    public static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Zombies.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }

    /// <summary>The text of a file in the base mod's <c>rigs/</c> folder, read the way the game reads it: through the mod source.</summary>
    public static string BaseRig(string fileName)
    {
        var package = DirectoryModSource.Read(Path.Combine(RepoRoot(), "mods")).Single(p => p.Source == "base");
        var asset = package.Assets.Single(a => a.Path == "rigs/" + fileName);
        return Encoding.UTF8.GetString(asset.Bytes).TrimStart('﻿');
    }

    public static Skeleton BaseSkeleton(string name) => Parse(BaseRig(name + ".skeleton.json"));

    public static ClipSet BaseClips(string name) => Clips(BaseRig(name + ".clips.json"));

    public static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-3f) =>
        Assert.True(
            Vector3.Distance(expected, actual) <= tolerance,
            $"Expected {expected.ToString("F3", CultureInfo.InvariantCulture)} but was {actual.ToString("F3", CultureInfo.InvariantCulture)}.");

    public static void IsRest(RigPose pose, string bone, Skeleton skeleton)
    {
        var rest = skeleton.Bones[skeleton.IndexOf(bone)].Rest;
        var local = pose.Local(skeleton.IndexOf(bone));
        Near(rest.Position, local.Position);
        Assert.True(MathF.Abs(Quaternion.Dot(rest.Rotation, local.Rotation)) > 0.99999f, $"{bone} is not at rest.");
    }

    public static AnimationInput Moving(float speed, MissingPartSet missing = MissingPartSet.None) => new(speed, 0f, 0f, missing);

    public static AnimationInput Still(MissingPartSet missing = MissingPartSet.None) => new(0f, 0f, 0f, missing);

    public static AnimationInput Looking(float yaw, float pitch, MissingPartSet missing = MissingPartSet.None) => new(0f, yaw, pitch, missing);
}
