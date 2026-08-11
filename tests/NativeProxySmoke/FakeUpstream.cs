using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace NativeProxySmoke;

public sealed class FakeUpstream
{
    private readonly WebApplication _app;
    private readonly object _requestGate = new();
    private string _lastChatRequest = "{}";

    public FakeUpstream(string[]? args = null)
    {
        var builder = WebApplication.CreateBuilder(args ?? Array.Empty<string>());
        builder.WebHost.UseUrls("http://127.0.0.1:18889");
        _app = builder.Build();
        MapRoutes();
    }

    public void Run() => _app.Run();

    private void MapRoutes()
    {
        _app.Use(next => async (HttpContext context) =>
        {
            try
            {
                await next(context);
            }
            catch (Exception ex)
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "fake-upstream-errors.log"),
                    $"{DateTime.Now:O} {ex}\n\n");
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync("{\"error\":\"fake internal error\"}");
            }
        });

        _app.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            var body = await JsonDocument.ParseAsync(context.Request.Body);
            lock (_requestGate) _lastChatRequest = body.RootElement.GetRawText();
            var stream = body.RootElement.TryGetProperty("stream", out var s) && s.GetBoolean();
            context.Response.ContentType = "application/json; charset=utf-8";
            if (stream)
            {
                context.Response.ContentType = "text/event-stream; charset=utf-8";
                await context.Response.WriteAsync(Chunk("CUSTOM_MODEL_OK", null, null));
                await context.Response.WriteAsync(Chunk(null, "stop", "{\"prompt_tokens\":10,\"completion_tokens\":3,\"total_tokens\":13}"));
                await context.Response.WriteAsync("data: [DONE]\n\n");
            }
            else
            {
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    id = "chatcmpl-fake",
                    @object = "chat.completion",
                    created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    model = "k3-test",
                    choices = new[]
                    {
                        new { index = 0, message = new { role = "assistant", content = "CUSTOM_MODEL_OK" }, finish_reason = "stop" }
                    },
                    usage = new { prompt_tokens = 10, completion_tokens = 3, total_tokens = 13 }
                }));
            }
        });

        _app.MapGet("/test/last-chat-request", () =>
        {
            lock (_requestGate) return Results.Text(_lastChatRequest, "application/json");
        });

        _app.MapPost("/v1/responses", async (HttpContext context) =>
        {
            var body = await JsonDocument.ParseAsync(context.Request.Body);
            var stream = body.RootElement.TryGetProperty("stream", out var s) && s.GetBoolean();
            if (stream)
            {
                context.Response.ContentType = "text/event-stream; charset=utf-8";
                await context.Response.WriteAsync("event: response.created\ndata: " + JsonSerializer.Serialize(new
                {
                    type = "response.created",
                    response = new { id = "resp_fake", @object = "response", status = "in_progress", model = "k3-test", output = new object[] { }, usage = (object?)null }
                }) + "\n\n");
                await context.Response.WriteAsync("event: response.output_text.delta\ndata: " + JsonSerializer.Serialize(new
                {
                    type = "response.output_text.delta",
                    item_id = "msg_fake",
                    output_index = 0,
                    content_index = 0,
                    delta = "RESPONSES_UPSTREAM_OK"
                }) + "\n\n");
                await context.Response.WriteAsync("event: response.output_text.done\ndata: " + JsonSerializer.Serialize(new
                {
                    type = "response.output_text.done",
                    item_id = "msg_fake",
                    output_index = 0,
                    content_index = 0,
                    text = "RESPONSES_UPSTREAM_OK"
                }) + "\n\n");
                await context.Response.WriteAsync("event: response.completed\ndata: " + JsonSerializer.Serialize(new
                {
                    type = "response.completed",
                    response = new
                    {
                        id = "resp_fake",
                        @object = "response",
                        status = "completed",
                        model = "k3-test",
                        output = new object[]
                        {
                            new { type = "message", id = "msg_fake", status = "completed", role = "assistant",
                                  content = new object[] { new { type = "output_text", text = "RESPONSES_UPSTREAM_OK", annotations = new object[] { } } } }
                        },
                        usage = new { input_tokens = 5, output_tokens = 2, total_tokens = 7 }
                    }
                }) + "\n\n");
                await context.Response.WriteAsync("data: [DONE]\n\n");
            }
            else
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    id = "resp_fake",
                    @object = "response",
                    status = "completed",
                    model = "k3-test",
                    output = new object[]
                    {
                        new { type = "message", id = "msg_fake", status = "completed", role = "assistant",
                              content = new object[] { new { type = "output_text", text = "RESPONSES_UPSTREAM_OK", annotations = new object[] { } } } }
                    },
                    usage = new { input_tokens = 5, output_tokens = 2, total_tokens = 7 }
                }));
            }
        });

        _app.MapPost("/v1/messages", async (HttpContext context) =>
        {
            var body = await JsonDocument.ParseAsync(context.Request.Body);
            var stream = body.RootElement.TryGetProperty("stream", out var s) && s.GetBoolean();
            if (stream)
            {
                context.Response.ContentType = "text/event-stream; charset=utf-8";
                await context.Response.WriteAsync("event: message_start\ndata: " + JsonSerializer.Serialize(new
                {
                    type = "message_start",
                    message = new { id = "msg_fake", type = "message", role = "assistant", model = "k3-test",
                                    content = new object[] { }, stop_reason = (object?)null,
                                    usage = new { input_tokens = 0, output_tokens = 0 } }
                }) + "\n\n");
                await context.Response.WriteAsync("event: content_block_start\ndata: " + JsonSerializer.Serialize(new
                {
                    type = "content_block_start",
                    index = 0,
                    content_block = new { type = "text", text = "" }
                }) + "\n\n");
                await context.Response.WriteAsync("event: content_block_delta\ndata: " + JsonSerializer.Serialize(new
                {
                    type = "content_block_delta",
                    index = 0,
                    delta = new { type = "text_delta", text = "ANTHROPIC_UPSTREAM_OK" }
                }) + "\n\n");
                await context.Response.WriteAsync("event: content_block_stop\ndata: " + JsonSerializer.Serialize(new
                {
                    type = "content_block_stop",
                    index = 0
                }) + "\n\n");
                await context.Response.WriteAsync("event: message_delta\ndata: " + JsonSerializer.Serialize(new
                {
                    type = "message_delta",
                    delta = new { stop_reason = "end_turn" },
                    usage = new { input_tokens = 8, output_tokens = 4 }
                }) + "\n\n");
                await context.Response.WriteAsync("event: message_stop\ndata: " + JsonSerializer.Serialize(new { type = "message_stop" }) + "\n\n");
            }
            else
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    id = "msg_fake",
                    type = "message",
                    role = "assistant",
                    model = "k3-test",
                    content = new object[] { new { type = "text", text = "ANTHROPIC_UPSTREAM_OK" } },
                    stop_reason = "end_turn",
                    stop_sequence = (object?)null,
                    usage = new { input_tokens = 8, output_tokens = 4 }
                }));
            }
        });

        _app.MapPost("/v1beta/models/{model}:generateContent", async (HttpContext context, string model) =>
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                candidates = new object[]
                {
                    new
                    {
                        content = new { parts = new object[] { new { text = "GOOGLE_UPSTREAM_OK" } } }
                    }
                },
                usageMetadata = new { promptTokenCount = 6, candidatesTokenCount = 2 }
            }));
        });

        _app.MapPost("/v1beta/models/{model}:streamGenerateContent", async (HttpContext context, string model) =>
        {
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(new
            {
                candidates = new object[]
                {
                    new
                    {
                        content = new { parts = new object[] { new { text = "GOOGLE_UPSTREAM_OK" } } }
                    }
                },
                usageMetadata = new { promptTokenCount = 6, candidatesTokenCount = 2 }
            }) + "\n\n");
            await context.Response.WriteAsync("data: [DONE]\n\n");
        });

        _app.MapGet("/healthz", () => Results.Json(new { status = "ok" }));
    }

    private static string Chunk(string? content, string? finishReason, string? usageJson)
    {
        var chunk = new JsonObject
        {
            ["id"] = "chatcmpl-fake",
            ["object"] = "chat.completion.chunk",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = "k3-test",
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = content
                    },
                    ["finish_reason"] = finishReason
                }
            }
        };
        if (usageJson is not null)
            chunk["usage"] = JsonNode.Parse(usageJson);
        return $"data: {chunk.ToJsonString()}\n\n";
    }
}
