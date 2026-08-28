#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HaruChat.Runtime.LocalModels
{
    /// <summary>
    /// Provider-neutral managed boundary for a locally hosted model backend.
    /// Implementations are introduced in Phase 4; this contract deliberately contains no Unity or P/Invoke types.
    /// </summary>
    public interface ILocalModelBackend : IAsyncDisposable
    {
        Task<LocalBackendResult<LocalRuntimeHandle>> CreateRuntimeAsync(
            LocalRuntimeOptions options,
            CancellationToken cancellationToken);

        Task<LocalBackendResult> DestroyRuntimeAsync(
            LocalRuntimeHandle runtime,
            CancellationToken cancellationToken);

        Task<LocalBackendResult<LocalModelHandle>> LoadModelAsync(
            LocalRuntimeHandle runtime,
            LocalModelLoadOptions options,
            CancellationToken cancellationToken);

        Task<LocalBackendResult> UnloadModelAsync(
            LocalModelHandle model,
            CancellationToken cancellationToken);

        Task<LocalBackendResult<LocalContextHandle>> CreateContextAsync(
            LocalModelHandle model,
            LocalContextOptions options,
            CancellationToken cancellationToken);

        Task<LocalBackendResult> ResetContextAsync(
            LocalContextHandle context,
            CancellationToken cancellationToken);

        Task<LocalBackendResult> DestroyContextAsync(
            LocalContextHandle context,
            CancellationToken cancellationToken);

        Task<LocalBackendResult<LocalGenerationHandle>> StartGenerationAsync(
            LocalContextHandle context,
            LocalGenerationOptions options,
            CancellationToken cancellationToken);

        Task<LocalBackendResult<LocalEventBatch>> PollEventsAsync(
            LocalGenerationHandle generation,
            int maximumEventCount,
            CancellationToken cancellationToken);

        Task<LocalBackendResult> CancelGenerationAsync(
            LocalGenerationHandle generation,
            CancellationToken cancellationToken);

        Task<LocalBackendResult> DestroyGenerationAsync(
            LocalGenerationHandle generation,
            CancellationToken cancellationToken);

        Task<LocalBackendResult<LocalModelMetadata>> GetModelMetadataAsync(
            LocalModelHandle model,
            CancellationToken cancellationToken);

        Task<LocalBackendResult<LocalGenerationMetrics>> GetGenerationMetricsAsync(
            LocalGenerationHandle generation,
            CancellationToken cancellationToken);

    }

    public readonly struct LocalRuntimeHandle : IEquatable<LocalRuntimeHandle>
    {
        public LocalRuntimeHandle(ulong value) { Value = value; }
        public ulong Value { get; }
        public bool IsValid { get { return Value != 0; } }
        public bool Equals(LocalRuntimeHandle other) { return Value == other.Value; }
        public override bool Equals(object? obj) { return obj is LocalRuntimeHandle && Equals((LocalRuntimeHandle)obj); }
        public override int GetHashCode() { return Value.GetHashCode(); }
        public static bool operator ==(LocalRuntimeHandle left, LocalRuntimeHandle right) { return left.Equals(right); }
        public static bool operator !=(LocalRuntimeHandle left, LocalRuntimeHandle right) { return !left.Equals(right); }
    }

    public readonly struct LocalModelHandle : IEquatable<LocalModelHandle>
    {
        public LocalModelHandle(ulong value) { Value = value; }
        public ulong Value { get; }
        public bool IsValid { get { return Value != 0; } }
        public bool Equals(LocalModelHandle other) { return Value == other.Value; }
        public override bool Equals(object? obj) { return obj is LocalModelHandle && Equals((LocalModelHandle)obj); }
        public override int GetHashCode() { return Value.GetHashCode(); }
        public static bool operator ==(LocalModelHandle left, LocalModelHandle right) { return left.Equals(right); }
        public static bool operator !=(LocalModelHandle left, LocalModelHandle right) { return !left.Equals(right); }
    }

    public readonly struct LocalContextHandle : IEquatable<LocalContextHandle>
    {
        public LocalContextHandle(ulong value) { Value = value; }
        public ulong Value { get; }
        public bool IsValid { get { return Value != 0; } }
        public bool Equals(LocalContextHandle other) { return Value == other.Value; }
        public override bool Equals(object? obj) { return obj is LocalContextHandle && Equals((LocalContextHandle)obj); }
        public override int GetHashCode() { return Value.GetHashCode(); }
        public static bool operator ==(LocalContextHandle left, LocalContextHandle right) { return left.Equals(right); }
        public static bool operator !=(LocalContextHandle left, LocalContextHandle right) { return !left.Equals(right); }
    }

    public readonly struct LocalGenerationHandle : IEquatable<LocalGenerationHandle>
    {
        public LocalGenerationHandle(ulong value) { Value = value; }
        public ulong Value { get; }
        public bool IsValid { get { return Value != 0; } }
        public bool Equals(LocalGenerationHandle other) { return Value == other.Value; }
        public override bool Equals(object? obj) { return obj is LocalGenerationHandle && Equals((LocalGenerationHandle)obj); }
        public override int GetHashCode() { return Value.GetHashCode(); }
        public static bool operator ==(LocalGenerationHandle left, LocalGenerationHandle right) { return left.Equals(right); }
        public static bool operator !=(LocalGenerationHandle left, LocalGenerationHandle right) { return !left.Equals(right); }
    }

    public sealed class LocalRuntimeOptions
    {
        public LocalRuntimeOptions(int maximumQueuedEventCount = 128)
        {
            MaximumQueuedEventCount = maximumQueuedEventCount;
        }

        public int MaximumQueuedEventCount { get; }
    }

    public sealed class LocalModelLoadOptions
    {
        public LocalModelLoadOptions(string modelPath)
        {
            ModelPath = modelPath ?? throw new ArgumentNullException(nameof(modelPath));
        }

        public string ModelPath { get; }
    }

    public sealed class LocalContextOptions
    {
        public LocalContextOptions(int contextWindowTokens, int batchSize)
        {
            ContextWindowTokens = contextWindowTokens;
            BatchSize = batchSize;
        }

        public int ContextWindowTokens { get; }
        public int BatchSize { get; }
    }

    public sealed class LocalGenerationOptions
    {
        public LocalGenerationOptions(string prompt, int maximumOutputTokens)
        {
            Prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
            MaximumOutputTokens = maximumOutputTokens;
        }

        public string Prompt { get; }
        public int MaximumOutputTokens { get; }
    }

    public enum LocalBackendErrorCode
    {
        None = 0,
        InvalidArgument = 1,
        InvalidHandle = 2,
        NotFound = 3,
        Busy = 4,
        QueueFull = 5,
        Cancelled = 6,
        Unsupported = 7,
        BackendFailure = 8,
    }

    public readonly struct LocalBackendError
    {
        public LocalBackendError(LocalBackendErrorCode code, string message)
        {
            Code = code;
            Message = message ?? string.Empty;
        }

        public LocalBackendErrorCode Code { get; }
        public string Message { get; }
        public bool IsNone { get { return Code == LocalBackendErrorCode.None; } }
    }

    public readonly struct LocalBackendResult
    {
        private LocalBackendResult(LocalBackendError error) { Error = error; }
        public LocalBackendError Error { get; }
        public bool IsSuccess { get { return Error.IsNone; } }
        public static LocalBackendResult Success() { return new LocalBackendResult(new LocalBackendError(LocalBackendErrorCode.None, string.Empty)); }
        public static LocalBackendResult Failure(LocalBackendErrorCode code, string message) { return new LocalBackendResult(new LocalBackendError(code, message)); }
    }

    public readonly struct LocalBackendResult<T>
    {
        private LocalBackendResult(T value, LocalBackendError error) { Value = value; Error = error; }
        public T Value { get; }
        public LocalBackendError Error { get; }
        public bool IsSuccess { get { return Error.IsNone; } }
        public static LocalBackendResult<T> Success(T value) { return new LocalBackendResult<T>(value, new LocalBackendError(LocalBackendErrorCode.None, string.Empty)); }
        public static LocalBackendResult<T> Failure(LocalBackendErrorCode code, string message) { return new LocalBackendResult<T>(default!, new LocalBackendError(code, message)); }
    }

    public enum LocalBackendEventKind
    {
        Token = 0,
        Metrics = 1,
        Completed = 2,
        Cancelled = 3,
        Error = 4,
    }

    /// <summary>
    /// A backend event snapshot. Payload is copied at construction so it remains valid after the next poll.
    /// Token payloads are UTF-8 byte fragments and may not align with Unicode character boundaries.
    /// </summary>
    public readonly struct LocalBackendEvent
    {
        public LocalBackendEvent(LocalBackendEventKind kind, long sequence, ReadOnlyMemory<byte> payload, LocalBackendError error, LocalGenerationMetrics metrics)
        {
            Kind = kind;
            Sequence = sequence;
            Payload = payload.ToArray();
            Error = error;
            Metrics = metrics;
        }

        public LocalBackendEventKind Kind { get; }
        public long Sequence { get; }
        public ReadOnlyMemory<byte> Payload { get; }
        public LocalBackendError Error { get; }
        public LocalGenerationMetrics Metrics { get; }
    }

    public sealed class LocalEventBatch
    {
        public LocalEventBatch(IReadOnlyList<LocalBackendEvent> events)
        {
            Events = events ?? throw new ArgumentNullException(nameof(events));
        }

        public IReadOnlyList<LocalBackendEvent> Events { get; }
    }

    public sealed class LocalModelMetadata
    {
        public LocalModelMetadata(string architecture, int contextWindowTokens)
        {
            Architecture = architecture ?? throw new ArgumentNullException(nameof(architecture));
            ContextWindowTokens = contextWindowTokens;
        }

        public string Architecture { get; }
        public int ContextWindowTokens { get; }
    }

    public readonly struct LocalGenerationMetrics
    {
        public LocalGenerationMetrics(long promptTokenCount, long generatedTokenCount, TimeSpan elapsed)
        {
            PromptTokenCount = promptTokenCount;
            GeneratedTokenCount = generatedTokenCount;
            Elapsed = elapsed;
        }

        public long PromptTokenCount { get; }
        public long GeneratedTokenCount { get; }
        public TimeSpan Elapsed { get; }
    }
}
