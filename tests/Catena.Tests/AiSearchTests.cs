using System.Net;
using System.Text;
using System.Text.Json;
using Catena.AI;
using Catena.Contracts;

namespace Catena.Tests;
public sealed class AiSearchTests
{
    private static readonly AiConnection Configuration = new() { BaseUrl = "https://model.example/v1", Model = "my-model", ApiKey = "test-secret" };
    [Theory]
    [InlineData("https://model.example/v1/", "https://model.example/v1/chat/completions")]
    [InlineData("http://localhost:1234/v1", "http://localhost:1234/v1/chat/completions")]
    [InlineData("https://model.example/custom/chat/completions", "https://model.example/custom/chat/completions")]
    public void CompatibleEndpointIsNormalized(string input, string expected) => Assert.Equal(expected, OpenAiSearchPlanner.CompletionEndpoint(input).AbsoluteUri);
    [Theory]
    [InlineData("http://public.example/v1")]
    [InlineData("https://user:secret@model.example/v1")]
    [InlineData("https://model.example/v1?api_key=secret")]
    public void CredentialsAndNonLocalPlainHttpAreRejected(string input) => Assert.Throws<SearchAssistanceException>(() => OpenAiSearchPlanner.CompletionEndpoint(input));

    [Fact] public async Task SendsOnlyUserDescriptionAndReadsValidatedPlan()
    {
        string? body = null;
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.Equal("https://model.example/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-secret", request.Headers.Authorization!.Parameter);
            body = await request.Content!.ReadAsStringAsync(token);
            using var payload = JsonDocument.Parse(body);
            Assert.Equal("my-model", payload.RootElement.GetProperty("model").GetString());
            Assert.Equal(2, payload.RootElement.GetProperty("messages").GetArrayLength());
            Assert.Equal("找上个月的预算表", payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
            return Reply(new { terms = new[] { "预算" }, extensions = new[] { "xlsx", "xls" }, kind = "file", modifiedFrom = "2026-08-01", modifiedBefore = "2026-09-01", explanation = "上个月修改的预算表" });
        }));
        var plan = await new OpenAiSearchPlanner(client).PlanAsync("找上个月的预算表", Configuration, TestContext.Current.CancellationToken);
        Assert.Equal("预算", Assert.Single(plan.Terms)); Assert.Equal(new DateOnly(2026, 8, 1), plan.ModifiedFrom);
        Assert.DoesNotContain("test-secret", body!); Assert.DoesNotContain("file:///", body!);
    }
    [Fact] public async Task ServerErrorDoesNotExposeResponseOrKey()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("test-secret vendor diagnostic") })));
        var error = await Assert.ThrowsAsync<SearchAssistanceException>(() => new OpenAiSearchPlanner(client).PlanAsync("文件", Configuration, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("test-secret", error.Message); Assert.DoesNotContain("vendor", error.Message);
    }
    [Fact] public async Task InvalidOrUnboundedPlanCannotExecute()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Reply(new { terms = Array.Empty<string>(), explanation = "empty" }))));
        await Assert.ThrowsAsync<SearchAssistanceException>(() => new OpenAiSearchPlanner(client).PlanAsync("找文件", Configuration, TestContext.Current.CancellationToken));
    }
    [Fact] public async Task UserCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new Handler(async (_, token) => { cancellation.Cancel(); await Task.Delay(Timeout.Infinite, token); return new(); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OpenAiSearchPlanner(client).PlanAsync("找文件", Configuration, cancellation.Token));
    }
    [Fact] public async Task NullModelFieldsAreRejectedWithoutCrashing()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Reply(new { terms = new[] { "预算" }, extensions = new string?[] { null } }))));
        await Assert.ThrowsAsync<SearchAssistanceException>(() => new OpenAiSearchPlanner(client).PlanAsync("找文件", Configuration, TestContext.Current.CancellationToken));
    }
    [Fact] public async Task RejectsLargeResponse()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 70000)) })));
        var error = await Assert.ThrowsAsync<SearchAssistanceException>(() => new OpenAiSearchPlanner(client).PlanAsync("找文件", Configuration, TestContext.Current.CancellationToken));
        Assert.Contains("过长", error.Message);
    }
    private static HttpResponseMessage Reply(object plan) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(plan) } } } }), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken); }
}
