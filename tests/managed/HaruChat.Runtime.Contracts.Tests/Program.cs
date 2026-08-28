using HaruChat.Runtime.LocalModels;
using System;
using System.Threading;
using System.Threading.Tasks;

var tests = new (string Name, Action Run)[]
{
    ("Opaque handles distinguish valid values", OpaqueHandlesAreValueObjects),
    ("Result propagates stable error code", ResultCarriesError),
    ("Event owns its UTF-8 payload", EventPayloadIsCopied),
    ("Options preserve backend-neutral values", OptionsAreImmutable),
    ("Backend contract is implementable without Unity", BackendCanBeFaked),
};

foreach (var test in tests)
{
    test.Run();
    Console.WriteLine($"PASS {test.Name}");
}

return;

static void OpaqueHandlesAreValueObjects()
{
    Assert(!default(LocalModelHandle).IsValid, "default model handle must be invalid");
    Assert(new LocalContextHandle(7) == new LocalContextHandle(7), "identical handles must compare equal");
    Assert(new LocalGenerationHandle(7) != new LocalGenerationHandle(8), "different handles must not compare equal");
}

static void ResultCarriesError()
{
    var failure = LocalBackendResult<LocalModelHandle>.Failure(LocalBackendErrorCode.Busy, "active generation");
    Assert(!failure.IsSuccess, "failure result must fail");
    Assert(failure.Error.Code == LocalBackendErrorCode.Busy, "error code must be preserved");
}

static void EventPayloadIsCopied()
{
    var source = new byte[] { 0xE3, 0x81, 0x82 };
    var backendEvent = new LocalBackendEvent(LocalBackendEventKind.Token, 1, source, default, default);
    source[0] = 0;
    Assert(backendEvent.Payload.Span[0] == 0xE3, "event payload must outlive source buffer reuse");
}

static void OptionsAreImmutable()
{
    var context = new LocalContextOptions(4096, 256);
    var generation = new LocalGenerationOptions("hello", 128);
    Assert(context.ContextWindowTokens == 4096 && context.BatchSize == 256, "context options changed");
    Assert(generation.Prompt == "hello" && generation.MaximumOutputTokens == 128, "generation options changed");
}

static void BackendCanBeFaked()
{
    ILocalModelBackend backend = new FakeLocalModelBackend();
    var runtime = backend.CreateRuntimeAsync(new LocalRuntimeOptions(), CancellationToken.None).GetAwaiter().GetResult();
    Assert(runtime.IsSuccess && runtime.Value.IsValid, "test backend must satisfy contract");
    backend.DisposeAsync().GetAwaiter().GetResult();
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class FakeLocalModelBackend : ILocalModelBackend
{
    public ValueTask DisposeAsync() => default;
    public Task<LocalBackendResult<LocalRuntimeHandle>> CreateRuntimeAsync(LocalRuntimeOptions options, CancellationToken cancellationToken) => Task.FromResult(LocalBackendResult<LocalRuntimeHandle>.Success(new LocalRuntimeHandle(1)));
    public Task<LocalBackendResult> DestroyRuntimeAsync(LocalRuntimeHandle runtime, CancellationToken cancellationToken) => Task.FromResult(LocalBackendResult.Success());
    public Task<LocalBackendResult<LocalModelHandle>> LoadModelAsync(LocalRuntimeHandle runtime, LocalModelLoadOptions options, CancellationToken cancellationToken) => Task.FromResult(LocalBackendResult<LocalModelHandle>.Failure(LocalBackendErrorCode.Unsupported, "test fake"));
    public Task<LocalBackendResult> UnloadModelAsync(LocalModelHandle model, CancellationToken cancellationToken) => Task.FromResult(LocalBackendResult.Success());
    public Task<LocalBackendResult<LocalContextHandle>> CreateContextAsync(LocalModelHandle model, LocalContextOptions options, CancellationToken cancellationToken) => Task.FromResult(LocalBackendResult<LocalContextHandle>.Failure(LocalBackendErrorCode.Unsupported, "test fake"));
    public Task<LocalBackendResult> ResetContextAsync(LocalContextHandle context, CancellationToken cancellationToken) => Task.FromResult(LocalBackendResult.Success());
    public Task<LocalBackendResult> DestroyContextAsync(LocalContextHandle context, CancellationToken cancellationToken) => Task.FromResult(LocalBackendResult.Success());
    public Task<LocalBackendResult<LocalGenerationHandle>> StartGenerationAsync(LocalContextHandle context, LocalGenerationOptions options, CancellationToken cancellationToken) => Task.FromResult(LocalBackendResult<LocalGenerationHandle>.Failure(LocalBackendErrorCode.Unsupported, "test fake"));
    public Task<LocalBackendResult<LocalEventBatch>> PollEventsAsync(LocalGenerationHandle generation, int maximumEventCount, CancellationToken cancellationToken) => Task.FromResult(LocalBackendResult<LocalEventBatch>.Success(new LocalEventBatch(Array.Empty<LocalBackendEvent>())));
    public Task<LocalBackendResult> CancelGenerationAsync(LocalGenerationHandle generation, CancellationToken cancellationToken) => Task.FromResult(LocalBackendResult.Success());
    public Task<LocalBackendResult> DestroyGenerationAsync(LocalGenerationHandle generation, CancellationToken cancellationToken) => Task.FromResult(LocalBackendResult.Success());
    public Task<LocalBackendResult<LocalModelMetadata>> GetModelMetadataAsync(LocalModelHandle model, CancellationToken cancellationToken) => Task.FromResult(LocalBackendResult<LocalModelMetadata>.Failure(LocalBackendErrorCode.Unsupported, "test fake"));
    public Task<LocalBackendResult<LocalGenerationMetrics>> GetGenerationMetricsAsync(LocalGenerationHandle generation, CancellationToken cancellationToken) => Task.FromResult(LocalBackendResult<LocalGenerationMetrics>.Success(default));
}
