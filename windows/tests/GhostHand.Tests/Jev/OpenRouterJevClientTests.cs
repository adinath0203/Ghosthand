using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using GhostHand.Core.Jev;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace GhostHand.Tests.Jev;

public class OpenRouterJevClientTests
{
    private readonly JevOptions _options = new()
    {
        Provider = JevProvider.OpenRouter,
        ApiKey = "sk-or-v1-test_secret_key_12345678",
        BaseUrl = JevOptions.OpenRouterDefaultBaseUrl,
        ModelId = "openai/gpt-4o-mini",
        TimeoutSeconds = 5,
        MaxRetries = 0
    };

    private static EvaluateRequest SampleRequest() => new()
    {
        Model = "ignored",
        State = new { task = "search for Adele" },
        Questions = new Dictionary<string, QuestionDefinition>
        {
            ["goalAchieved"] = QuestionDefinition.Boolean("Done?"),
            ["nextAction"] = QuestionDefinition.Choice(new Dictionary<string, string>
            {
                ["click:e1"] = "Click Search",
                ["press:enter"] = "Press Enter"
            }),
            ["actionRisk"] = QuestionDefinition.Score(new List<string> { "harmless", "reversible edit", "irreversible" })
        }
    };

    private static (HttpClient client, Func<string?> sentBody) MockHttp(string modelContent)
    {
        string? captured = null;
        var chatResponse = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content = modelContent } } },
            usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
        });

        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage req, CancellationToken _) =>
            {
                req.RequestUri!.ToString().Should().Be("https://openrouter.ai/api/v1/chat/completions");
                captured = await req.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(chatResponse) };
            });

        return (new HttpClient(handler.Object), () => captured);
    }

    [Fact]
    public async Task OR01_MapsModelJsonToEvaluateAnswers()
    {
        var (http, sentBody) = MockHttp("""
            ```json
            {"goalAchieved":{"probability":0.1},
             "nextAction":{"choice":"CLICK:E1","confidence":0.8},
             "actionRisk":{"score":1,"probabilities":[0.9,0.08,0.02]}}
            ```
            """);
        var client = new OpenRouterJevClient(http, _options, NullLogger<OpenRouterJevClient>.Instance);

        var response = await client.EvaluateAsync(SampleRequest());

        response.TryGetBooleanAnswer("goalAchieved", out var p, out var achieved).Should().BeTrue();
        p.Should().BeApproximately(0.1, 1e-9);
        achieved.Should().BeFalse();

        response.TryGetChoiceAnswer("nextAction", out var choice, out var conf, out _).Should().BeTrue();
        choice.Should().Be("click:e1");
        conf.Should().BeApproximately(0.8, 1e-9);

        response.TryGetScoreAnswer("actionRisk", out var score, out var probs).Should().BeTrue();
        score.Should().Be(1);
        probs.Should().HaveCount(3);

        response.Usage!.TotalTokens.Should().Be(15);

        var body = sentBody()!;
        body.Should().Contain("\"model\":\"openai/gpt-4o-mini\"");
        body.Should().Contain("json_object");
    }

    [Fact]
    public async Task OR02_UnknownChoice_IsDroppedSoDecisionModelAsksUser()
    {
        var (http, _) = MockHttp("""{"nextAction":{"choice":"click:e999","confidence":0.9}}""");
        var client = new OpenRouterJevClient(http, _options, NullLogger<OpenRouterJevClient>.Instance);

        var response = await client.EvaluateAsync(SampleRequest());

        response.TryGetChoiceAnswer("nextAction", out _, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"nextAction":{"choice":2,"confidence":0.7}}""")]
    [InlineData("""{"nextAction":{"choice":"2"}}""")]
    [InlineData("""{"nextAction":2}""")]
    [InlineData("""{"answers":{"nextAction":{"choice":"option 2"}}}""")]
    public async Task OR04_NumberedChoice_MapsBackToOptionKey(string modelContent)
    {
        var (http, sentBody) = MockHttp(modelContent);
        var client = new OpenRouterJevClient(http, _options, NullLogger<OpenRouterJevClient>.Instance);

        var response = await client.EvaluateAsync(SampleRequest());

        response.TryGetChoiceAnswer("nextAction", out var choice, out _, out _).Should().BeTrue();
        choice.Should().Be("press:enter");
        // Options are embedded (JSON-escaped) inside the user message
        sentBody()!.Should().Contain("\\\"2\\\":\\\"Press Enter\\\"");
    }

    [Fact]
    public void OR05_JevDecisionsResponse_MapsToEvaluateAnswers()
    {
        // Captured from https://openrouter.ai/api/alpha/decisions with model ~typesafe/jev-latest
        var body = """
            {"model":"typesafe/jev-1.13-20260917",
             "answers":{
               "goalAchieved":{"type":"noul","noul":0.44},
               "nextAction":{"type":"choice","choice":"click:e1","probabilities":{"click:e1":0.79,"press:enter":0.21},"confidence":0.59},
               "actionRisk":{"type":"score","score":0.06,"legend":{"0":"harmless","1":"reversible edit","2":"irreversible"},
                             "probabilities":{"0":0.04,"1":0.06,"2":0.90},"confidence":0.91}},
             "usage":{"input_tokens":405,"output_tokens":68,"cost":1.701e-05},
             "id":"gen-dec-1","provider":"TypeSafe"}
            """;

        var response = OpenRouterJevClient.ParseDecisionsResponse(body, SampleRequest());

        response.TryGetBooleanAnswer("goalAchieved", out var p, out var achieved).Should().BeTrue();
        p.Should().BeApproximately(0.44, 1e-9);
        achieved.Should().BeFalse();

        response.TryGetChoiceAnswer("nextAction", out var choice, out var conf, out _).Should().BeTrue();
        choice.Should().Be("click:e1");
        conf.Should().BeApproximately(0.79, 1e-9);

        response.TryGetScoreAnswer("actionRisk", out var score, out var probs).Should().BeTrue();
        score.Should().Be(3, "most likely level is the third (irreversible), 1-based");
        probs.Should().Equal(0.04, 0.06, 0.90);

        response.Usage!.TotalTokens.Should().Be(473);
        response.ProviderMetadata!.Gateway!.Cost.Should().BeApproximately(1.701e-05, 1e-12);
    }

    [Fact]
    public async Task OR06_JevModel_UsesDecisionsEndpointWithNoulType()
    {
        string? url = null, sent = null;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage req, CancellationToken _) =>
            {
                url = req.RequestUri!.ToString();
                sent = await req.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"answers":{}}""") };
            });

        var options = new JevOptions
        {
            Provider = JevProvider.OpenRouter, ApiKey = "sk-or-v1-test_12345678",
            BaseUrl = JevOptions.OpenRouterDefaultBaseUrl, ModelId = "~typesafe/jev-latest", MaxRetries = 0
        };
        var client = new OpenRouterJevClient(new HttpClient(handler.Object), options, NullLogger<OpenRouterJevClient>.Instance);

        await client.EvaluateAsync(SampleRequest());

        url.Should().Be("https://openrouter.ai/api/alpha/decisions");
        sent.Should().Contain("\"type\":\"noul\"").And.Contain("\"model\":\"~typesafe/jev-latest\"").And.Contain("\"criteria\":");
    }

    [Fact]
    public void OR03_Factory_SelectsClientByProvider()
    {
        using var http = new HttpClient();
        JevClientFactory.Create(http, _options).Should().BeOfType<OpenRouterJevClient>();
        JevClientFactory.Create(http, new JevOptions()).Should().BeOfType<JevClient>();
    }
}
