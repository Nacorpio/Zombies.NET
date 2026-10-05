using System.Text.Json.Nodes;
using Zombies.ContentJudge.SystemOne;

namespace Zombies.ContentJudge.Tests;

public sealed class ValidationTests
{
    private static NamedQuestion Noul(string id) => new(id, new NoulQuestion("Is it?"));

    private static SystemOneRequest Request(params NamedQuestion[] questions) => new("jev-latest", "state", questions);

    private static SystemOneRequest WithImages(params byte[][] images) =>
        new("clef-flash", "state", [Noul("q")]) { Images = [.. images.Select(JudgeImage.FromBytes)] };

    [Theory]
    [InlineData("a")]
    [InlineData("definition.plausible")]
    [InlineData("A-b_c.9")]
    public void QuestionId_LettersDigitsUnderscoreDotDashAreValid(string id) =>
        Assert.Empty(Request(Noul(id)).Validate());

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("colon:id")]
    [InlineData("slash/id")]
    [InlineData("ümlaut")]
    [InlineData("trailing\n")]
    public void QuestionId_OtherCharactersAreInvalid(string id) =>
        Assert.Contains(Request(Noul(id)).Validate(), p => p.Contains("Question id", StringComparison.Ordinal));

    [Fact]
    public void QuestionId_AtMost100Characters()
    {
        Assert.Empty(Request(Noul(new string('a', 100))).Validate());
        Assert.NotEmpty(Request(Noul(new string('a', 101))).Validate());
    }

    [Fact]
    public void Questions_OneTo64PerRequest()
    {
        Assert.NotEmpty(Request().Validate());
        Assert.Empty(Request([.. Enumerable.Range(0, 64).Select(i => Noul($"q{i}"))]).Validate());
        Assert.NotEmpty(Request([.. Enumerable.Range(0, 65).Select(i => Noul($"q{i}"))]).Validate());
    }

    [Fact]
    public void Questions_DuplicateIdIsInvalid() =>
        Assert.NotEmpty(Request(Noul("q"), Noul("q")).Validate());

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public void Score_TwoToTenLevels(int levels, bool valid)
    {
        var request = Request(new NamedQuestion("s", new ScoreQuestion("How bad?", [.. Enumerable.Range(0, levels).Select(i => (JsonNode)$"level {i}")])));

        Assert.Equal(valid, request.Validate().Count == 0);
    }

    [Fact]
    public void Choice_NeedsAtLeastTwoUniqueOptions()
    {
        Assert.NotEmpty(Request(new NamedQuestion("c", new ChoiceQuestion("Which?", [new("only", null)]))).Validate());
        Assert.Empty(Request(new NamedQuestion("c", new ChoiceQuestion("Which?", [new("a", null), new("b", "B")]))).Validate());
    }

    [Fact]
    public void Instructions_EmptyStringIsInvalid() =>
        Assert.NotEmpty(Request(new NamedQuestion("q", new NoulQuestion(" "))).Validate());

    [Fact]
    public void Images_UpToFourAreValid() =>
        Assert.Empty(WithImages(Fakes.Png(), Fakes.Png(), Fakes.Png(), Fakes.Png()).Validate());

    [Fact]
    public void Images_FiveAreInvalid() =>
        Assert.Contains(WithImages(Fakes.Png(), Fakes.Png(), Fakes.Png(), Fakes.Png(), Fakes.Png()).Validate(), p => p.Contains("at most 4", StringComparison.Ordinal));

    [Fact]
    public void Images_Over4MiBEachIsInvalid()
    {
        Assert.Empty(WithImages(Fakes.Png(length: 4 * 1024 * 1024)).Validate());
        Assert.Contains(WithImages(Fakes.Png(length: (4 * 1024 * 1024) + 1)).Validate(), p => p.Contains("4 MiB", StringComparison.Ordinal));
    }

    [Fact]
    public void Images_Over8MiBTotalIsInvalid()
    {
        var threeMiB = 3 * 1024 * 1024;

        Assert.Contains(WithImages(Fakes.Png(length: threeMiB), Fakes.Png(length: threeMiB), Fakes.Png(length: threeMiB)).Validate(), p => p.Contains("8 MiB", StringComparison.Ordinal));
    }

    [Fact]
    public void Images_Over16MegapixelsIsInvalid()
    {
        Assert.Empty(WithImages(Fakes.Png(4000, 4000)).Validate());
        Assert.Contains(WithImages(Fakes.Png(5000, 4000)).Validate(), p => p.Contains("16 megapixels", StringComparison.Ordinal));
    }

    [Fact]
    public void Images_RefusedForAModelWithoutImages() =>
        Assert.Contains(WithImages(Fakes.Png()).Validate(imagesAllowed: false), p => p.Contains("does not accept images", StringComparison.Ordinal));

    [Fact]
    public void Images_MislabelledTypeIsInvalid()
    {
        var request = new SystemOneRequest("clef-flash", "state", [Noul("q")]) { Images = [new JudgeImage(JudgeImage.Jpeg, Fakes.Png())] };

        Assert.NotEmpty(request.Validate());
    }

    [Fact]
    public void Images_JpegAndWebPSizesAreRead()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x20, 0x00, 0x40, 0x03];
        byte[] webp = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, .. "VP8X"u8, 10, 0, 0, 0, 0, 0, 0, 0, 63, 0, 0, 31, 0, 0];

        Assert.True(JudgeImage.TryReadSize(jpeg, out var jw, out var jh));
        Assert.Equal((64, 32), (jw, jh));
        Assert.True(JudgeImage.TryReadSize(webp, out var ww, out var wh));
        Assert.Equal((64, 32), (ww, wh));
    }
}
