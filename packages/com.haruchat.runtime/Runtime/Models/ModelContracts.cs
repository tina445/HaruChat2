#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HaruChat.Runtime.Models
{
    public enum ModelRole { System, User, Assistant, Tool }
    public enum ModelEventKind { Token, Reasoning, ToolCall, ToolResult, Usage, Completed, Error }
    public enum ModelStopReason { Stop, Length, ToolCall }
    public enum ModelErrorCode { InvalidConfiguration, InvalidRequest, NotFound, Busy, Cancelled, Unsupported, BackendFailure, ContextBudgetExceeded }

    public sealed class ModelMessage
    {
        public ModelMessage(ModelRole role, string text)
        {
            Role = role;
            Text = text ?? throw new ArgumentNullException(nameof(text));
        }
        public ModelRole Role { get; }
        public string Text { get; }
    }

    public sealed class GenerationOptions
    {
        public GenerationOptions(int maximumOutputTokens = 8192, float temperature = 0.7f, int topK = 40, float topP = 0.9f, int? seed = null)
        {
            if (maximumOutputTokens <= 0 || temperature < 0 || topK < 0 || topP <= 0 || topP > 1) throw new ArgumentOutOfRangeException(nameof(maximumOutputTokens));
            MaximumOutputTokens = maximumOutputTokens; Temperature = temperature; TopK = topK; TopP = topP; Seed = seed;
        }
        public int MaximumOutputTokens { get; }
        public float Temperature { get; }
        public int TopK { get; }
        public float TopP { get; }
        public int? Seed { get; }
    }

    public sealed class ModelToolDefinition
    {
        public ModelToolDefinition(string name, string description, string argumentSchemaJson)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A tool name is required.", nameof(name));
            Name = name; Description = description ?? string.Empty; ArgumentSchemaJson = argumentSchemaJson ?? "{}";
        }
        public string Name { get; } public string Description { get; } public string ArgumentSchemaJson { get; }
    }

    public sealed class ModelToolCall
    {
        public ModelToolCall(string callId, string name, string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(callId) || string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A tool call ID and name are required.");
            CallId = callId; Name = name; ArgumentsJson = argumentsJson ?? "{}";
        }
        public string CallId { get; } public string Name { get; } public string ArgumentsJson { get; }
    }

    public sealed class ModelToolResult
    {
        public ModelToolResult(string callId, string name, bool succeeded, string content, string? errorCode = null)
        {
            if (string.IsNullOrWhiteSpace(callId) || string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A tool call ID and name are required.");
            CallId = callId; Name = name; Succeeded = succeeded; Content = content ?? string.Empty; ErrorCode = errorCode;
        }
        public string CallId { get; } public string Name { get; } public bool Succeeded { get; } public string Content { get; } public string? ErrorCode { get; }
    }

    public sealed class ModelRequest
    {
        public ModelRequest(IReadOnlyList<ModelMessage> messages, GenerationOptions? generation = null, string? correlationId = null, IReadOnlyList<ModelToolDefinition>? tools = null)
        {
            if (messages == null || messages.Count == 0) throw new ArgumentException("At least one message is required.", nameof(messages));
            Messages = new List<ModelMessage>(messages).AsReadOnly();
            Generation = generation;
            CorrelationId = correlationId ?? Guid.NewGuid().ToString("N");
            Tools = new List<ModelToolDefinition>(tools ?? Array.Empty<ModelToolDefinition>()).AsReadOnly();
        }
        public IReadOnlyList<ModelMessage> Messages { get; }
        /// <summary>Null delegates generation policy to the selected model profile/runtime configuration.</summary>
        public GenerationOptions? Generation { get; }
        public string CorrelationId { get; }
        /// <summary>Provider-neutral tool schemas. An empty list means tool calling is disabled.</summary>
        public IReadOnlyList<ModelToolDefinition> Tools { get; }
    }

    public sealed class ModelUsage
    {
        public ModelUsage(long? promptTokens, long? generatedTokens, TimeSpan? elapsed) { PromptTokens = promptTokens; GeneratedTokens = generatedTokens; Elapsed = elapsed; }
        public long? PromptTokens { get; }
        public long? GeneratedTokens { get; }
        public TimeSpan? Elapsed { get; }
    }

    public sealed class ModelDiagnostics
    {
        public ModelDiagnostics(string backend, bool? accelerationEnabled, int? contextWindowTokens, TimeSpan? loadDuration, TimeSpan? timeToFirstToken, double? promptTokensPerSecond, double? generationTokensPerSecond)
        { Backend = backend ?? string.Empty; AccelerationEnabled = accelerationEnabled; ContextWindowTokens = contextWindowTokens; LoadDuration = loadDuration; TimeToFirstToken = timeToFirstToken; PromptTokensPerSecond = promptTokensPerSecond; GenerationTokensPerSecond = generationTokensPerSecond; }
        public string Backend { get; } public bool? AccelerationEnabled { get; } public int? ContextWindowTokens { get; }
        public TimeSpan? LoadDuration { get; } public TimeSpan? TimeToFirstToken { get; }
        public double? PromptTokensPerSecond { get; } public double? GenerationTokensPerSecond { get; }
    }

    public sealed class ModelError
    {
        public ModelError(ModelErrorCode code, string message, bool recoverable = true) { Code = code; Message = message ?? string.Empty; Recoverable = recoverable; }
        public ModelErrorCode Code { get; }
        public string Message { get; }
        public bool Recoverable { get; }
    }

    public sealed class ModelEvent
    {
        private ModelEvent(ModelEventKind kind, string? text, ModelUsage? usage, ModelStopReason? stopReason, ModelError? error, ModelToolCall? toolCall, ModelToolResult? toolResult)
        { Kind = kind; Text = text; Usage = usage; StopReason = stopReason; Error = error; ToolCall = toolCall; ToolResult = toolResult; }
        public ModelEventKind Kind { get; }
        public string? Text { get; }
        public ModelUsage? Usage { get; }
        public ModelStopReason? StopReason { get; }
        public ModelError? Error { get; }
        public ModelToolCall? ToolCall { get; }
        public ModelToolResult? ToolResult { get; }
        public bool IsTerminal { get { return Kind == ModelEventKind.Completed || Kind == ModelEventKind.Error; } }
        public static ModelEvent Token(string text) { return new ModelEvent(ModelEventKind.Token, text ?? string.Empty, null, null, null, null, null); }
        public static ModelEvent Reasoning(string text) { return new ModelEvent(ModelEventKind.Reasoning, text ?? string.Empty, null, null, null, null, null); }
        public static ModelEvent ToolCallRequested(ModelToolCall call) { if (call == null) throw new ArgumentNullException(nameof(call)); return new ModelEvent(ModelEventKind.ToolCall, null, null, null, null, call, null); }
        public static ModelEvent ToolResultReceived(ModelToolResult result) { if (result == null) throw new ArgumentNullException(nameof(result)); return new ModelEvent(ModelEventKind.ToolResult, null, null, null, null, null, result); }
        public static ModelEvent UsageSnapshot(ModelUsage usage) { return new ModelEvent(ModelEventKind.Usage, null, usage, null, null, null, null); }
        public static ModelEvent Completed(ModelStopReason reason = ModelStopReason.Stop) { return new ModelEvent(ModelEventKind.Completed, null, null, reason, null, null, null); }
        public static ModelEvent ErrorEvent(ModelError error) { return new ModelEvent(ModelEventKind.Error, null, null, null, error, null, null); }
    }

    public sealed class ModelCapabilities
    {
        public ModelCapabilities(bool streaming = true, bool cancellation = true, bool tools = false, bool reasoning = false) { Streaming = streaming; Cancellation = cancellation; Tools = tools; Reasoning = reasoning; }
        public bool Streaming { get; } public bool Cancellation { get; } public bool Tools { get; } public bool Reasoning { get; }
    }

    public sealed class ModelSessionOptions
    {
        public ModelSessionOptions(int contextWindowTokens, int batchSize = 256)
        { if (contextWindowTokens <= 0 || batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(contextWindowTokens)); ContextWindowTokens = contextWindowTokens; BatchSize = batchSize; }
        public int ContextWindowTokens { get; } public int BatchSize { get; }
    }

    public interface IModelAdapter
    {
        string Id { get; }
        ModelCapabilities Capabilities { get; }
        Task<IModelSession> CreateSessionAsync(ModelSessionOptions options, CancellationToken cancellationToken);
    }
    public interface IModelSession : IAsyncDisposable
    {
        IAsyncEnumerable<ModelEvent> GenerateAsync(ModelRequest request, CancellationToken cancellationToken);
        Task ResetAsync(CancellationToken cancellationToken);
        Task<ModelUsage> GetUsageAsync(CancellationToken cancellationToken);
        Task<ModelDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken);
    }

    /// <summary>Optional token-count capability. Local adapters return exact tokenizer counts;
    /// providers that cannot expose their tokenizer intentionally do not implement this port.</summary>
    public interface ITokenCountingModelSession
    {
        Task<ModelTokenCount> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken);
    }

    public sealed class ModelTokenCount
    {
        public ModelTokenCount(int tokens, bool isExact)
        {
            if (tokens < 0) throw new ArgumentOutOfRangeException(nameof(tokens));
            Tokens = tokens; IsExact = isExact;
        }
        public int Tokens { get; }
        public bool IsExact { get; }
    }

    public sealed class ModelRouter
    {
        private readonly Dictionary<string, IModelAdapter> _adapters;
        public ModelRouter(IEnumerable<IModelAdapter> adapters)
        {
            _adapters = new Dictionary<string, IModelAdapter>(StringComparer.Ordinal);
            foreach (var adapter in adapters ?? throw new ArgumentNullException(nameof(adapters)))
            {
                if (adapter == null || string.IsNullOrWhiteSpace(adapter.Id) || !_adapters.TryAdd(adapter.Id, adapter)) throw new ArgumentException("Adapter IDs must be non-empty and unique.", nameof(adapters));
            }
        }
        public IModelAdapter Resolve(string id)
        {
            if (id == null || !_adapters.TryGetValue(id, out var adapter)) throw new ModelOperationException(ModelErrorCode.NotFound, "No model adapter is registered for '" + id + "'.");
            return adapter;
        }
    }
}
