using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexOpenCodexNative.Adapters;

namespace CodexOpenCodexNative.Responses;

public sealed class AnthropicOutboundBridge
{
    private bool _textBlockOpen;
    private int _blockIndex;

    public async IAsyncEnumerable<string> StreamAsync(
        IAsyncEnumerable<AdapterEvent> events,
        string modelId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return Frame("message_start", new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = "msg_" + Guid.NewGuid().ToString("N")[..24],
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = modelId,
                ["content"] = new JsonArray(),
                ["stop_reason"] = null,
                ["stop_sequence"] = null,
                ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 }
            }
        });

        var usage = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 };
        await foreach (var adapterEvent in events)
        {
            if (cancellationToken.IsCancellationRequested) break;
            switch (adapterEvent.Type)
            {
                case "text":
                {
                    if (!_textBlockOpen)
                    {
                        yield return Frame("content_block_start", new JsonObject
                        {
                            ["type"] = "content_block_start",
                            ["index"] = _blockIndex,
                            ["content_block"] = new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = string.Empty
                            }
                        });
                        _textBlockOpen = true;
                    }
                    if (adapterEvent.Text is { Length: > 0 })
                    {
                        yield return Frame("content_block_delta", new JsonObject
                        {
                            ["type"] = "content_block_delta",
                            ["index"] = _blockIndex,
                            ["delta"] = new JsonObject
                            {
                                ["type"] = "text_delta",
                                ["text"] = adapterEvent.Text
                            }
                        });
                    }
                    break;
                }
                case "usage":
                {
                    if (adapterEvent.Usage is not null)
                    {
                        usage["input_tokens"] = adapterEvent.Usage.PromptTokens;
                        usage["output_tokens"] = adapterEvent.Usage.CompletionTokens;
                    }
                    break;
                }
                case "finish":
                case "done":
                {
                    if (_textBlockOpen)
                    {
                        yield return Frame("content_block_stop", new JsonObject
                        {
                            ["type"] = "content_block_stop",
                            ["index"] = _blockIndex
                        });
                        _textBlockOpen = false;
                        _blockIndex++;
                    }
                    yield return Frame("message_delta", new JsonObject
                    {
                        ["type"] = "message_delta",
                        ["delta"] = new JsonObject { ["stop_reason"] = "end_turn" },
                        ["usage"] = usage
                    });
                    yield return Frame("message_stop", new JsonObject { ["type"] = "message_stop" });
                    yield break;
                }
                case "error":
                {
                    yield return Frame("error", new JsonObject
                    {
                        ["type"] = "error",
                        ["error"] = new JsonObject
                        {
                            ["type"] = "proxy_error",
                            ["message"] = adapterEvent.Text ?? "上游错误"
                        }
                    });
                    yield break;
                }
            }
        }

        if (_textBlockOpen)
        {
            yield return Frame("content_block_stop", new JsonObject
            {
                ["type"] = "content_block_stop",
                ["index"] = _blockIndex
            });
        }
        yield return Frame("message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject { ["stop_reason"] = "end_turn" },
            ["usage"] = usage
        });
        yield return Frame("message_stop", new JsonObject { ["type"] = "message_stop" });
    }

    private static string Frame(string name, JsonObject payload) =>
        $"event: {name}\ndata: {payload.ToJsonString()}\n\n";
}
