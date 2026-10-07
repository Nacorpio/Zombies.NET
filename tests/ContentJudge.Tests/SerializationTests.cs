using System.Text.Json.Nodes;
using Zombies.ContentJudge.SystemOne;

namespace Zombies.ContentJudge.Tests;

public sealed class SerializationTests
{
    private static void AssertSameJson(string expected, string actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)), $"Expected {expected}\nActual {actual}");

    [Theory]
    [InlineData(Samples.NoulRequest)]
    [InlineData(Samples.ChoiceRequest)]
    [InlineData(Samples.ScoreRequest)]
    [InlineData(Samples.StructuredInstructionsRequest)]
    [InlineData(Samples.ClefRequest)]
    public void Request_SamplePayloadParsesAndSerializesBackUnchanged(string sample)
    {
        var request = SystemOneRequest.Parse(sample);

        AssertSameJson(sample, request.ToJsonString());
        Assert.Empty(request.Validate());
    }

    [Fact]
    public void Request_BuiltFromTypesMatchesTheClefSample()
    {
        var request = new SystemOneRequest("clef-flash", "Checkout has been failing for every customer for the last hour.",
        [
            new NamedQuestion("urgent", new NoulQuestion("Is this support request urgent?")),
            new NamedQuestion("team", new ChoiceQuestion("Which team should handle this request?",
            [
                new("billing", "Payments, invoices, and refunds"),
                new("technical", "Outages, errors, and configuration"),
                new("sales", "Plans and upgrades"),
            ])),
            new NamedQuestion("severity", new ScoreQuestion("How severe is the customer impact?", ["No impact", "Minor", "Major", "Critical"])),
        ]);

        AssertSameJson(Samples.ClefRequest, request.ToJsonString());
    }

    [Fact]
    public void Request_ParsesTypedQuestions()
    {
        var request = SystemOneRequest.Parse(Samples.ClefRequest);

        Assert.Equal(["urgent", "team", "severity"], request.Questions.Select(q => q.Id));
        Assert.IsType<NoulQuestion>(request.Questions[0].Question);
        var choice = Assert.IsType<ChoiceQuestion>(request.Questions[1].Question);
        Assert.Equal(["billing", "technical", "sales"], choice.Options.Select(o => o.Key));
        var score = Assert.IsType<ScoreQuestion>(request.Questions[2].Question);
        Assert.Equal(4, score.Levels.Count);
        Assert.Equal("Critical", score.Levels[3].GetValue<string>());
    }

    [Fact]
    public void Request_KeepsNoulCriteria()
    {
        var noul = Assert.IsType<NoulQuestion>(SystemOneRequest.Parse(Samples.NoulRequest).Questions[0].Question);

        Assert.Equal("Explicitly time-sensitive", noul.WhenTrue!.GetValue<string>());
        Assert.Equal("No urgency expressed", noul.WhenFalse!.GetValue<string>());
    }

    [Fact]
    public void Response_ParsesNoulSample()
    {
        var response = SystemOneResponse.Parse(Samples.NoulResponse);

        Assert.Equal("jev-1.13.0", response.Model);
        Assert.Equal(0.95, Assert.IsType<NoulAnswer>(response.Answers["is_urgent"]).Noul);
        Assert.Equal(new Usage(307, 20), response.Usage);
        AssertSameJson(Samples.NoulResponse, response.ToJsonString());
    }

    [Fact]
    public void Response_ParsesChoiceSample()
    {
        var answer = Assert.IsType<ChoiceAnswer>(SystemOneResponse.Parse(Samples.ChoiceResponse).Answers["department"]);

        Assert.Equal("billing", answer.Choice);
        Assert.Equal(0.88, answer.Probabilities["billing"]);
        Assert.Equal(0.81, answer.Confidence);
        AssertSameJson(Samples.ChoiceResponse, SystemOneResponse.Parse(Samples.ChoiceResponse).ToJsonString());
    }

    [Fact]
    public void Response_ParsesScoreSample()
    {
        var answer = Assert.IsType<ScoreAnswer>(SystemOneResponse.Parse(Samples.ScoreResponse).Answers["frustration"]);

        Assert.Equal(1.05, answer.Score);
        Assert.Equal("Very angry", answer.Legend["2"]!.GetValue<string>());
        Assert.Equal(0.95, answer.Probabilities["1"]);
        Assert.Equal(0.92, answer.Confidence);
        AssertSameJson(Samples.ScoreResponse, SystemOneResponse.Parse(Samples.ScoreResponse).ToJsonString());
    }

    [Fact]
    public void Response_UnwrapsTheWorkersAiEnvelope()
    {
        var response = SystemOneResponse.Parse(Samples.CloudflareEnvelope);

        Assert.Equal("clef-flash", response.Model);
        Assert.Equal(0.9, Assert.IsType<NoulAnswer>(response.Answers["urgent"]).Noul);
    }

    [Fact]
    public void Response_FailedEnvelopeThrows()
    {
        var ex = Assert.Throws<FormatException>(() => SystemOneResponse.Parse("""{ "result": null, "success": false, "errors": [{ "code": 5006, "message": "bad input" }], "messages": [] }"""));

        Assert.Contains("bad input", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "model": "jev" }""")]
    [InlineData("""{ "answers": { "a": { "type": "noul" } } }""")]
    [InlineData("""{ "answers": { "a": { "type": "guess", "noul": 1 } } }""")]
    [InlineData("""{ "answers": { "a": { "type": "choice", "choice": "x", "confidence": 1 } } }""")]
    public void Response_MalformedBodyThrowsFormatException(string body) =>
        Assert.Throws<FormatException>(() => SystemOneResponse.Parse(body));

    [Theory]
    [InlineData("""{ "model": "m", "state": "s", "questions": { "q": { "type": "guess", "instructions": "x" } } }""")]
    [InlineData("""{ "model": "m", "state": "s", "questions": { "q": { "type": "choice", "instructions": "x" } } }""")]
    [InlineData("""{ "model": "m", "state": "s", "questions": { "q": { "type": "score", "instructions": "x", "criteria": {} } } }""")]
    [InlineData("""{ "model": "m", "questions": {} }""")]
    public void Request_MalformedBodyThrowsFormatException(string body) =>
        Assert.Throws<FormatException>(() => SystemOneRequest.Parse(body));

    [Fact]
    public void Image_IsSentAsBase64DataUrlAndReadBack()
    {
        var png = Fakes.Png();
        var request = new SystemOneRequest("clef-flash", "icon", [new NamedQuestion("q", new NoulQuestion("Is it a can?"))]) { Images = [JudgeImage.FromBytes(png)] };

        var json = JsonNode.Parse(request.ToJsonString())!;
        var entry = json["images"]![0]!.GetValue<string>();

        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(png), entry);
        var back = SystemOneRequest.Parse(request.ToJsonString()).Images.Single();
        Assert.Equal("image/png", back.ContentType);
        Assert.Equal(png, back.Bytes);
    }

    [Fact]
    public void Image_ObjectFormIsAccepted()
    {
        var png = Fakes.Png();
        var image = JudgeImage.FromJson(new JsonObject { ["content_type"] = "image/png", ["base64"] = Convert.ToBase64String(png) });

        Assert.Equal(png, image.Bytes);
    }

    [Theory]
    [InlineData("https://example.com/icon.png")]
    [InlineData("data:image/png,rawbytes")]
    [InlineData("data:image/png;base64,***")]
    public void Image_UrlsAndNonBase64AreRefused(string entry) =>
        Assert.Throws<FormatException>(() => JudgeImage.FromJson(entry));

    [Fact]
    public void Image_UnknownBytesAreRefused() =>
        Assert.Throws<FormatException>(() => JudgeImage.FromBytes([1, 2, 3, 4]));
}
