using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using CodexOpenCodexNative.Adapters;
using CodexOpenCodexNative.Models;

namespace CodexOpenCodexNative.Responses;

public sealed class ResponsesBridge
{
    private const int HeartbeatIntervalMs = 2000;
    private const int HeartbeatIdleMs = 2000;

    private readonly string _modelId;
    private readonly string _responseId;
    private readonly long _createdAt;
    private int _sequenceNumber;
    private int _outputIndex;
    private readonly List<FinishedItem> _finishedItems = new();

    private string? _msgItemId;
    private string _msgText = string.Empty;
    private string? _reasoningItemId;
    private string _reasoningText = string.Empty;
    private readonly Dictionary<int, ToolItem> _toolItems = new();

    public ResponsesBridge(string modelId)
    {
        _modelId = modelId;
        _responseId = "resp_" + Guid.NewGuid().ToString("N")[..24];
        _createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    public string ResponseId => _responseId;

    public OcxUsage? Usage { get; private set; }

    public string Status { get; private set; } = "completed";
    public string? ErrorMessage { get; private set; }

    public async IAsyncEnumerable<string> StreamAsync(
        IAsyncEnumerable<AdapterEvent> events,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<string>();
        var producer = Task.Run(
            () => ProduceFramesAsync(events, channel.Writer, cancellationToken),
            CancellationToken.None);

        await foreach (var frame in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return frame;
        }
        await producer;
    }

    private async Task ProduceFramesAsync(
        IAsyncEnumerable<AdapterEvent> events,
        ChannelWriter<string> writer,
        CancellationToken cancellationToken)
    {
        var lastActivity = DateTime.UtcNow;
        using var heartbeatCts = new CancellationTokenSource();
        var heartbeat = Task.Run(async () =>
        {
            while (!heartbeatCts.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatIntervalMs, heartbeatCts.Token);
                if ((DateTime.UtcNow - lastActivity).TotalMilliseconds >= HeartbeatIdleMs)
                {
                    writer.TryWrite("event: response.heartbeat\ndata: {\"type\":\"response.heartbeat\"}\n\n");
                    lastActivity = DateTime.UtcNow;
                }
            }
        }, CancellationToken.None);

        try
        {
            await writer.WriteAsync(Frame("response.created", new JsonObject
            {
                ["response"] = ResponseSnapshot("in_progress", null)
            }), cancellationToken);
            lastActivity = DateTime.UtcNow;

            var terminalEmitted = false;
            await foreach (var adapterEvent in events)
            {
                if (cancellationToken.IsCancellationRequested) break;
                switch (adapterEvent.Type)
                {
                    case "text":
                    {
                        EnsureMessage();
                        _msgText += adapterEvent.Text ?? string.Empty;
                        if (adapterEvent.Text is { Length: > 0 })
                        {
                            await writer.WriteAsync(Frame("response.output_text.delta", new JsonObject
                            {
                                ["item_id"] = _msgItemId,
                                ["output_index"] = _outputIndex,
                                ["content_index"] = 0,
                                ["delta"] = adapterEvent.Text
                            }), cancellationToken);
                        }
                        break;
                    }
                    case "reasoning":
                    {
                        if (_reasoningItemId is null)
                        {
                            _reasoningItemId = "rs_" + Guid.NewGuid().ToString("N")[..24];
                            var item = new JsonObject
                            {
                                ["type"] = "reasoning",
                                ["id"] = _reasoningItemId,
                                ["summary"] = new JsonArray(),
                                ["content"] = new JsonArray()
                            };
                            await writer.WriteAsync(Frame("response.output_item.added", new JsonObject
                            {
                                ["output_index"] = _outputIndex,
                                ["item"] = item
                            }), cancellationToken);
                        }
                        _reasoningText += adapterEvent.Reasoning ?? string.Empty;
                        if (adapterEvent.Reasoning is { Length: > 0 })
                        {
                            await writer.WriteAsync(Frame("response.reasoning_summary_text.delta", new JsonObject
                            {
                                ["item_id"] = _reasoningItemId,
                                ["output_index"] = _outputIndex,
                                ["summary_index"] = 0,
                                ["delta"] = adapterEvent.Reasoning
                            }), cancellationToken);
                        }
                        break;
                    }
                    case "reasoning_done":
                    {
                        await CloseReasoningAsync(writer, cancellationToken);
                        break;
                    }
                    case "function_call":
                    {
                        await CloseMessageAsync(writer, cancellationToken);
                        await CloseReasoningAsync(writer, cancellationToken);
                        if (!_toolItems.TryGetValue(adapterEvent.ToolCallIndex, out var tool))
                        {
                            var itemId = "fc_" + Guid.NewGuid().ToString("N")[..24];
                            tool = new ToolItem(
                                adapterEvent.ToolCallIndex,
                                _outputIndex++,
                                itemId,
                                adapterEvent.CallId ?? itemId,
                                adapterEvent.FunctionName ?? string.Empty);
                            _toolItems[adapterEvent.ToolCallIndex] = tool;
                            await writer.WriteAsync(Frame("response.output_item.added", new JsonObject
                            {
                                ["output_index"] = tool.OutputIndex,
                                ["item"] = new JsonObject
                                {
                                    ["type"] = "function_call",
                                    ["id"] = tool.ItemId,
                                    ["call_id"] = tool.CallId,
                                    ["name"] = tool.Name,
                                    ["arguments"] = string.Empty
                                }
                            }), cancellationToken);
                        }
                        if (!string.IsNullOrEmpty(adapterEvent.FunctionName))
                            tool.Name = adapterEvent.FunctionName;
                        if (adapterEvent.Arguments is { Length: > 0 })
                        {
                            tool.Arguments.Append(adapterEvent.Arguments);
                            await writer.WriteAsync(Frame("response.function_call_arguments.delta", new JsonObject
                            {
                                ["item_id"] = tool.ItemId,
                                ["output_index"] = tool.OutputIndex,
                                ["delta"] = adapterEvent.Arguments
                            }), cancellationToken);
                        }
                        break;
                    }
                    case "function_call_done":
                    {
                        await CloseToolAsync(adapterEvent.ToolCallIndex, writer, cancellationToken);
                        break;
                    }
                    case "usage":
                    {
                        Usage = adapterEvent.Usage;
                        await writer.WriteAsync(Frame("response.usage", new JsonObject
                        {
                            ["usage"] = adapterEvent.Usage is null
                                ? ResponsesUsage.Build(null)
                                : ResponsesUsage.Build(JsonSerializer.SerializeToElement(adapterEvent.Usage))
                        }), cancellationToken);
                        break;
                    }
                    case "finish":
                    case "done":
                    {
                        if (!terminalEmitted)
                        {
                            await CloseAllAsync(writer, cancellationToken);
                            var incomplete = adapterEvent.Type == "finish"
                                             && adapterEvent.FinishReason is "max_tokens" or "length";
                            if (incomplete)
                            {
                                Status = "incomplete";
                                await writer.WriteAsync(Frame("response.incomplete", new JsonObject
                                {
                                    ["response"] = new JsonObject
                                    {
                                        ["id"] = _responseId,
                                        ["object"] = "response",
                                        ["created_at"] = _createdAt,
                                        ["status"] = "incomplete",
                                        ["model"] = _modelId,
                                        ["output"] = SerializeFinishedItems(),
                                        ["usage"] = ResponsesUsage.Build(null),
                                        ["incomplete_details"] = new JsonObject
                                        {
                                            ["reason"] = "max_output_tokens"
                                        }
                                    }
                                }), cancellationToken);
                            }
                            else
                            {
                                await writer.WriteAsync(Frame("response.completed", new JsonObject
                                {
                                    ["response"] = ResponseSnapshot("completed", null, endTurn: true)
                                }), cancellationToken);
                            }
                            terminalEmitted = true;
                        }
                        await writer.WriteAsync("data: [DONE]\n\n", cancellationToken);
                        return;
                    }
                    case "incomplete":
                    {
                        if (!terminalEmitted)
                        {
                            Status = "incomplete";
                            await CloseAllAsync(writer, cancellationToken);
                            await writer.WriteAsync(Frame("response.incomplete", new JsonObject
                            {
                                ["response"] = new JsonObject
                                {
                                    ["id"] = _responseId,
                                    ["object"] = "response",
                                    ["created_at"] = _createdAt,
                                    ["status"] = "incomplete",
                                    ["model"] = _modelId,
                                    ["output"] = SerializeFinishedItems(),
                                    ["usage"] = ResponsesUsage.Build(null),
                                    ["incomplete_details"] = new JsonObject
                                    {
                                        ["reason"] = "upstream_incomplete"
                                    }
                                }
                            }), cancellationToken);
                            terminalEmitted = true;
                        }
                        await writer.WriteAsync("data: [DONE]\n\n", cancellationToken);
                        return;
                    }
                    case "error":
                    {
                        if (!terminalEmitted)
                        {
                            Status = "error";
                            ErrorMessage = adapterEvent.Text ?? "上游错误";
                            await CloseAllAsync(writer, cancellationToken);
                            var error = ResponsesJson.Error(
                                adapterEvent.ErrorType ?? "proxy_error",
                                adapterEvent.Text ?? "上游错误");
                            await writer.WriteAsync(Frame("response.failed", new JsonObject
                            {
                                ["response"] = new JsonObject
                                {
                                    ["id"] = _responseId,
                                    ["object"] = "response",
                                    ["created_at"] = _createdAt,
                                    ["status"] = "failed",
                                    ["model"] = _modelId,
                                    ["output"] = SerializeFinishedItems(),
                                    ["usage"] = ResponsesUsage.Build(null),
                                    ["error"] = error,
                                    ["last_error"] = error
                                }
                            }), cancellationToken);
                            terminalEmitted = true;
                        }
                        await writer.WriteAsync("data: [DONE]\n\n", cancellationToken);
                        return;
                    }
                }
                lastActivity = DateTime.UtcNow;
            }

            if (!terminalEmitted)
            {
                await CloseAllAsync(writer, cancellationToken);
                await writer.WriteAsync(Frame("response.incomplete", new JsonObject
                {
                    ["response"] = new JsonObject
                    {
                        ["id"] = _responseId,
                        ["object"] = "response",
                        ["created_at"] = _createdAt,
                        ["status"] = "incomplete",
                        ["model"] = _modelId,
                        ["output"] = SerializeFinishedItems(),
                        ["usage"] = ResponsesUsage.Build(null),
                        ["incomplete_details"] = new JsonObject
                        {
                            ["reason"] = "stream_closed"
                        }
                    }
                }), cancellationToken);
                await writer.WriteAsync("data: [DONE]\n\n", cancellationToken);
            }
        }
        finally
        {
            heartbeatCts.Cancel();
            writer.TryComplete();
        }
    }

    private void EnsureMessage()
    {
        if (_msgItemId is not null) return;
        _msgItemId = "msg_" + Guid.NewGuid().ToString("N")[..24];
        _msgText = string.Empty;
    }

    private async Task CloseMessageAsync(ChannelWriter<string> writer, CancellationToken ct)
    {
        if (_msgItemId is null) return;
        var itemId = _msgItemId;
        var text = _msgText;
        await writer.WriteAsync(Frame("response.output_text.done", new JsonObject
        {
            ["item_id"] = itemId,
            ["output_index"] = _outputIndex,
            ["content_index"] = 0,
            ["text"] = text
        }), ct);
        await writer.WriteAsync(Frame("response.content_part.done", new JsonObject
        {
            ["item_id"] = itemId,
            ["output_index"] = _outputIndex,
            ["content_index"] = 0,
            ["part"] = new JsonObject
            {
                ["type"] = "output_text",
                ["text"] = text,
                ["annotations"] = new JsonArray()
            }
        }), ct);
        await writer.WriteAsync(Frame("response.output_item.done", new JsonObject
        {
            ["output_index"] = _outputIndex,
            ["item"] = SerializeMessageItem(itemId, text)
        }), ct);
        _finishedItems.Add(new FinishedItem(_outputIndex, SerializeMessageItem(itemId, text)));
        _outputIndex++;
        _msgItemId = null;
        _msgText = string.Empty;
    }

    private async Task CloseReasoningAsync(ChannelWriter<string> writer, CancellationToken ct)
    {
        if (_reasoningItemId is null) return;
        var itemId = _reasoningItemId;
        var text = _reasoningText;
        await writer.WriteAsync(Frame("response.reasoning_summary_text.done", new JsonObject
        {
            ["item_id"] = itemId,
            ["output_index"] = _outputIndex,
            ["summary_index"] = 0,
            ["text"] = text
        }), ct);
        await writer.WriteAsync(Frame("response.reasoning_summary_part.done", new JsonObject
        {
            ["item_id"] = itemId,
            ["output_index"] = _outputIndex,
            ["summary_index"] = 0,
            ["part"] = new JsonObject
            {
                ["type"] = "summary_text",
                ["text"] = text
            }
        }), ct);
        var item = new JsonObject
        {
            ["type"] = "reasoning",
            ["id"] = itemId,
            ["summary"] = new JsonArray
            {
                new JsonObject { ["type"] = "summary_text", ["text"] = text }
            },
            ["content"] = new JsonArray()
        };
        await writer.WriteAsync(Frame("response.output_item.done", new JsonObject
        {
            ["output_index"] = _outputIndex,
            ["item"] = item
        }), ct);
        _finishedItems.Add(new FinishedItem(_outputIndex, item));
        _outputIndex++;
        _reasoningItemId = null;
        _reasoningText = string.Empty;
    }

    private async Task CloseToolAsync(int toolCallIndex, ChannelWriter<string> writer, CancellationToken ct)
    {
        if (!_toolItems.Remove(toolCallIndex, out var tool)) return;
        var arguments = tool.Arguments.ToString();
        await writer.WriteAsync(Frame("response.function_call_arguments.done", new JsonObject
        {
            ["item_id"] = tool.ItemId,
            ["output_index"] = tool.OutputIndex,
            ["arguments"] = arguments
        }), ct);
        var item = new JsonObject
        {
            ["type"] = "function_call",
            ["id"] = tool.ItemId,
            ["call_id"] = tool.CallId,
            ["name"] = tool.Name,
            ["arguments"] = arguments
        };
        await writer.WriteAsync(Frame("response.output_item.done", new JsonObject
        {
            ["output_index"] = tool.OutputIndex,
            ["item"] = item
        }), ct);
        _finishedItems.Add(new FinishedItem(tool.OutputIndex, item));
    }

    private async Task CloseAllAsync(ChannelWriter<string> writer, CancellationToken ct)
    {
        foreach (var index in _toolItems.Keys.OrderBy(index => index).ToArray())
            await CloseToolAsync(index, writer, ct);
        await CloseReasoningAsync(writer, ct);
        await CloseMessageAsync(writer, ct);
    }

    public JsonObject BuildNonStreamingResponse(string? upstreamText, JsonElement? upstreamUsage)
    {
        var items = new JsonArray();
        if (!string.IsNullOrWhiteSpace(upstreamText))
        {
            items.Add(SerializeMessageItem(
                "msg_" + Guid.NewGuid().ToString("N")[..24],
                upstreamText));
        }
        foreach (var item in _finishedItems.OrderBy(item => item.OutputIndex))
        {
            items.Add(item.Item.DeepClone());
        }
        return new JsonObject
        {
            ["id"] = _responseId,
            ["object"] = "response",
            ["created_at"] = _createdAt,
            ["status"] = "completed",
            ["model"] = _modelId,
            ["output"] = items,
            ["usage"] = ResponsesUsage.Build(upstreamUsage),
            ["end_turn"] = true
        };
    }

    public IReadOnlyList<OcxMessage> GetContinuationMessages()
    {
        var messages = new List<OcxMessage>();
        foreach (var item in _finishedItems.OrderBy(item => item.OutputIndex))
        {
            var mapped = ResponsesParser.MapInputItem(JsonSerializer.SerializeToElement(item.Item));
            if (mapped is not null) messages.Add(mapped);
        }
        return messages;
    }

    private static JsonObject SerializeMessageItem(string itemId, string text)
    {
        var part = new JsonObject
        {
            ["type"] = "output_text",
            ["text"] = text,
            ["annotations"] = new JsonArray()
        };
        return new JsonObject
        {
            ["type"] = "message",
            ["id"] = itemId,
            ["status"] = "completed",
            ["role"] = "assistant",
            ["content"] = new JsonArray { part }
        };
    }

    private JsonArray SerializeFinishedItems()
    {
        var array = new JsonArray();
        foreach (var item in _finishedItems.OrderBy(item => item.OutputIndex))
        {
            array.Add(item.Item.DeepClone());
        }
        return array;
    }

    private JsonObject ResponseSnapshot(string status, JsonArray? output, bool? endTurn = null)
    {
        var snapshot = new JsonObject
        {
            ["id"] = _responseId,
            ["object"] = "response",
            ["created_at"] = _createdAt,
            ["status"] = status,
            ["model"] = _modelId,
            ["output"] = output ?? SerializeFinishedItems(),
            ["usage"] = null
        };
        if (endTurn is not null)
            snapshot["end_turn"] = endTurn;
        return snapshot;
    }

    private string Frame(string name, JsonObject data)
    {
        var payload = new JsonObject
        {
            ["type"] = name,
            ["sequence_number"] = _sequenceNumber++
        };
        foreach (var (key, value) in data)
        {
            payload[key] = value?.DeepClone();
        }
        return $"event: {name}\ndata: {payload.ToJsonString()}\n\n";
    }

    private sealed record FinishedItem(int OutputIndex, JsonObject Item);

    private sealed class ToolItem(
        int toolCallIndex,
        int outputIndex,
        string itemId,
        string callId,
        string name)
    {
        public int ToolCallIndex { get; } = toolCallIndex;
        public int OutputIndex { get; } = outputIndex;
        public string ItemId { get; } = itemId;
        public string CallId { get; } = callId;
        public string Name { get; set; } = name;
        public StringBuilder Arguments { get; } = new();
    }
}
