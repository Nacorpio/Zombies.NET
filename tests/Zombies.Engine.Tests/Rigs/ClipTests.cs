using System.Numerics;
using Zombies.Engine.Animation;

namespace Zombies.Engine.Tests.Rigs;

public sealed class ClipTests
{
    private const string Wave = """
        {
          "clips": [
            {
              "name": "wave", "duration": 1, "loop": true,
              "tracks": [
                { "bone": "torso", "keys": [
                    { "time": 0 },
                    { "time": 0.5, "offset": [8, 0, 0], "rotation": [0, 90, 0] }
                ] }
              ]
            },
            {
              "name": "once", "duration": 1, "loop": false,
              "tracks": [
                { "bone": "torso", "keys": [
                    { "time": 0.25, "offset": [8, 0, 0] },
                    { "time": 0.75, "offset": [16, 0, 0] }
                ] }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Clips_LoadFromJson_WithTheirTracksAndKeys()
    {
        var clips = RigTestData.Clips(Wave);

        Assert.Equal(2, clips.Count);
        var wave = clips.Get("wave");
        Assert.NotNull(wave);
        Assert.True(wave.Loop);
        Assert.Equal(1f, wave.Duration);
        var track = Assert.Single(wave.Tracks);
        Assert.Equal("torso", track.Bone);
        Assert.Equal(2, track.Keys.Count);
        Assert.Null(clips.Get("nope"));
    }

    [Fact]
    public void KeyframeOffsets_AreVoxelsInTheFile_AndMetersAfterLoading()
    {
        var track = RigTestData.Clips(Wave).Get("wave")!.Tracks[0];

        RigTestData.Near(new Vector3(1f, 0f, 0f), track.Keys[1].OffsetMeters);
    }

    [Fact]
    public void Sampling_BlendsBetweenTheKeysEitherSide()
    {
        var track = RigTestData.Clips(Wave).Get("wave")!.Tracks[0];

        var quarter = track.Sample(0.25f, loop: true, duration: 1f);

        RigTestData.Near(new Vector3(0.5f, 0f, 0f), quarter.Position);

        // Halfway to a quarter turn about Y is an eighth of a turn, which takes forward (-Z) toward -X.
        var turned = Vector3.Transform(-Vector3.UnitZ, quarter.Rotation);
        RigTestData.Near(new Vector3(-MathF.Sin(MathF.PI / 4f), 0f, -MathF.Cos(MathF.PI / 4f)), turned, 1e-2f);
    }

    [Fact]
    public void ALoopingClip_BlendsItsLastKeyBackIntoItsFirst_AndWrapsTime()
    {
        var track = RigTestData.Clips(Wave).Get("wave")!.Tracks[0];

        RigTestData.Near(new Vector3(0.5f, 0f, 0f), track.Sample(0.75f, loop: true, duration: 1f).Position);
        RigTestData.Near(new Vector3(0.5f, 0f, 0f), track.Sample(1.75f, loop: true, duration: 1f).Position);
        RigTestData.Near(Vector3.Zero, track.Sample(2f, loop: true, duration: 1f).Position);
    }

    [Fact]
    public void AClipThatPlaysOnce_HoldsItsFirstAndLastKeys()
    {
        var track = RigTestData.Clips(Wave).Get("once")!.Tracks[0];

        RigTestData.Near(new Vector3(1f, 0f, 0f), track.Sample(0f, loop: false, duration: 1f).Position);
        RigTestData.Near(new Vector3(1.5f, 0f, 0f), track.Sample(0.5f, loop: false, duration: 1f).Position);
        RigTestData.Near(new Vector3(2f, 0f, 0f), track.Sample(5f, loop: false, duration: 1f).Position);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{ nope")]
    [InlineData("""{ "clips": {} }""")]
    [InlineData("""{ "clips": [ { "duration": 1, "tracks": [] } ] }""")]
    [InlineData("""{ "clips": [ { "name": "a", "tracks": [] } ] }""")]
    [InlineData("""{ "clips": [ { "name": "a", "duration": 0, "tracks": [] } ] }""")]
    [InlineData("""{ "clips": [ { "name": "a", "duration": 1 } ] }""")]
    [InlineData("""{ "clips": [ { "name": "a", "duration": 1, "tracks": [] }, { "name": "a", "duration": 1, "tracks": [] } ] }""")]
    [InlineData("""{ "clips": [ { "name": "a", "duration": 1, "tracks": [ { "bone": "b", "keys": [] } ] } ] }""")]
    [InlineData("""{ "clips": [ { "name": "a", "duration": 1, "tracks": [ { "bone": "b", "keys": [ {}, {} ] } ] } ] }""")]
    [InlineData("""{ "clips": [ { "name": "a", "duration": 1, "tracks": [ { "bone": "b", "keys": [ { "time": 0.5 }, { "time": 0.25 } ] } ] } ] }""")]
    [InlineData("""{ "clips": [ { "name": "a", "duration": 1, "tracks": [ { "bone": "b", "keys": [ { "time": 2 } ] } ] } ] }""")]
    [InlineData("""{ "clips": [ { "name": "a", "duration": 1, "tracks": [ { "bone": "b", "keys": [ { "rotation": [1] } ] } ] } ] }""")]
    [InlineData("""{ "clips": [ { "name": "a", "duration": 1, "tracks": [ { "bone": "b", "keys": [ {} ] }, { "bone": "b", "keys": [ {} ] } ] } ] }""")]
    public void ABadClipFile_IsRefusedWithAReason_AndNeverThrows(string json)
    {
        Assert.False(ClipSet.TryParse(json, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void AClipThatNamesAnUnknownBone_IsRefusedWhenTheAnimatorIsBuilt()
    {
        var clips = RigTestData.Clips("""{ "clips": [ { "name": "wag", "duration": 1, "tracks": [ { "bone": "tail", "keys": [ {} ] } ] } ] }""");

        var error = Assert.Throws<ArgumentException>(() => new Animator(RigTestData.Mini(), clips));

        Assert.Contains("tail", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASetBuiltFromClips_RefusesTwoOfTheSameName()
    {
        var clip = new Clip("walk", 1f, true, []);

        Assert.Throws<ArgumentException>(() => new ClipSet([clip, clip]));
    }

    [Theory]
    [InlineData(MissingPartSet.None, "walk")]
    [InlineData(MissingPartSet.LeftArm, "walk")]
    [InlineData(MissingPartSet.Head, "walk")]
    [InlineData(MissingPartSet.LeftLeg, "walk_crawl")]
    [InlineData(MissingPartSet.RightLeg, "walk_crawl")]
    [InlineData(MissingPartSet.LeftLeg | MissingPartSet.RightLeg, "walk_crawl")]
    [InlineData(MissingPartSet.LeftLeg | MissingPartSet.LeftArm, "walk_crawl")]
    public void TheCrawlVariant_IsChosenFromTheMissingPartFlags(MissingPartSet missing, string expected)
    {
        var clips = new ClipSet([new Clip("walk", 1f, true, []), new Clip("walk_crawl", 1f, true, [])]);

        Assert.Equal(expected, clips.Resolve("walk", missing)?.Name);
    }

    [Fact]
    public void WithoutACrawlVariant_ACrawlingCharacterFallsBackToTheClipItself_AndAnUnknownClipIsNull()
    {
        var clips = new ClipSet([new Clip("idle", 1f, true, [])]);

        Assert.Equal("idle", clips.Resolve("idle", MissingPartSet.LeftLeg)?.Name);
        Assert.Null(clips.Resolve("walk", MissingPartSet.None));
        Assert.Null(ClipSet.Empty.Resolve("walk", MissingPartSet.LeftLeg));
    }
}
