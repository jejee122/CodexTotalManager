using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexOpenCodexNative.Adapters;
using CodexOpenCodexNative.Models;

namespace CodexOpenCodexNative.Responses;

public sealed class AnthropicOutboundBridge
{
    private bool _textBlockOpen;
    private bool _toolBlockOpen;
    private int _toolCallIndex = -1;
    private int _blockIndex;
    public string Status { get; private set; } = "completed";
    public OcxUsage? Usage { get; private set; }
    public string? ErrorMessage { get; private set; }

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
                    foreach (var close in CloseToolBlock())
                        yield return close;
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
                case "function_call":
                {
                    if (_textBlockOpen)
                    {
                        yield return StopBlock(_blockIndex);
                        _textBlockOpen = false;
                        _blockIndex++;
                    }
                    if (!_toolBlockOpen || _toolCallIndex != adapterEvent.ToolCallIndex)
                    {
                        foreach (var close in CloseToolBlock())
                            yield return close;
                        _toolBlockOpen = true;
                        _toolCallIndex = adapterEvent.ToolCallIndex;
                        yield return Frame("content_block_start", new JsonObject
                        {
                            ["type"] = "content_block_start",
                            ["index"] = _blockIndex,
                            ["content_block"] = new JsonObject
                            {
                                ["type"] = "tool_use",
                                ["id"] = adapterEvent.CallId ?? "toolu_" + Guid.NewGuid().ToString("N")[..24],
                                ["name"] = adapterEvent.FunctionName ?? string.Empty,
                                ["input"] = new JsonObject()
                            }
                        });
                    }
                    if (adapterEvent.Arguments is { Length: > 0 })
                    {
                        yield return Frame("content_block_delta", new JsonObject
                        {
                            ["type"] = "content_block_delta",
                            ["index"] = _blockIndex,
                            ["delta"] = new JsonObject
                            {
                                ["type"] = "input_json_delta",
                                ["partial_json"] = adapterEvent.Arguments
                            }
                        });
                    }
                    break;
                }
                case "function_call_done":
                {
                    foreach (var close in CloseToolBlock())
                        yield return close;
                    break;
                }
                case "usage":
                {
                    if (adapterEvent.Usage is not null)
                    {
                        Usage = adapterEvent.Usage;
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
                        yield return StopBlock(_blockIndex);
                        _textBlockOpen = false;
                        _blockIndex++;
                    }
                    foreach (var close in CloseToolBlock())
                        yield return close;
                    var stopReason = adapterEvent.FinishReason switch
                    {
                        "tool_calls" => "tool_use",
                        "max_tokens" or "length" => "max_tokens",
                        "content_filter" => "refusal",
                        _ => "end_turn"
                    };
                    Status = stopReason is "max_tokens" or "refusal" ? "incomplete" : "completed";
                    yield return Frame("message_delta", new JsonObject
                    {
                        ["type"] = "message_delta",
                        ["delta"] = new JsonObject { ["stop_reason"] = stopReason },
                        ["usage"] = usage
                    });
                    yield return Frame("message_stop", new JsonObject { ["type"] = "message_stop" });
                    yield break;
                }
                case "error":
                {
                    Status = "error";
                    ErrorMessage = adapterEvent.Text ?? "上游错误";
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
                case "incomplete":
                {
                    Status = "incomplete";
                    ErrorMessage = adapterEvent.Text ?? "上游流未完整结束";
                    if (_textBlockOpen)
                    {
                        yield return StopBlock(_blockIndex);
                        _textBlockOpen = false;
                        _blockIndex++;
                    }
                    foreach (var close in CloseToolBlock())
                        yield return close;
                    yield return Frame("error", new JsonObject
                    {
                        ["type"] = "error",
                        ["error"] = new JsonObject
                        {
                            ["type"] = "upstream_incomplete",
                            ["message"] = ErrorMessage
                        }
                    });
                    yield break;
                }
            }
        }

        if (cancellationToken.IsCancellationRequested) yield break;
        if (_textBlockOpen)
        {
            yield return StopBlock(_blockIndex);
        }
        foreach (var close in CloseToolBlock())
            yield return close;
        Status = "incomplete";
        ErrorMessage = "上游流未发送完成事件";
        yield return Frame("error", new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject
            {
                ["type"] = "upstream_incomplete",
                ["message"] = ErrorMessage
            }
        });
    }

    private static string Frame(string name, JsonObject payload) =>
        $"event: {name}\ndata: {payload.ToJsonString()}\n\n";

    private static string StopBlock(int index) =>
        Frame("content_block_stop", new JsonObject
        {
            ["type"] = "content_block_stop",
            ["index"] = index
        });

    private IEnumerable<string> CloseToolBlock()
    {
        if (!_toolBlockOpen) yield break;
        yield return StopBlock(_blockIndex);
        _toolBlockOpen = false;
        _toolCallIndex = -1;
        _blockIndex++;
    }
}
