using HaruChat.Runtime.LocalModels;
using HaruChat.Runtime.Models;
using HaruChat.Runtime.Characters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

var tests = new (string Name, Action Run)[]
{
    ("Opaque handles distinguish valid values", OpaqueHandlesAreValueObjects),
    ("Result propagates stable error code", ResultCarriesError),
    ("Event owns its UTF-8 payload", EventPayloadIsCopied),
    ("Options preserve backend-neutral values", OptionsAreImmutable),
    ("Backend contract is implementable without Unity", BackendCanBeFaked),
    ("Model router requires explicit unique selection", ModelRouterIsExplicit),
    ("Prompt compiler retains recent conversation within budget", PromptCompilerPreservesRecentTurns),
    ("Character chat commits only completed responses", CharacterChatCommitsCompletedResponses),
    ("Character bundle loader validates a minimal bundle", CharacterBundleLoads),
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

static void ModelRouterIsExplicit()
{
    var router = new ModelRouter(new[] { new MockModelAdapter("mock") });
    Assert(router.Resolve("mock").Id == "mock", "known adapter must resolve");
    try { router.Resolve("unknown"); throw new InvalidOperationException("unknown adapter must fail"); }
    catch (KeyNotFoundException) { }
}

static void PromptCompilerPreservesRecentTurns()
{
    var character = new CharacterDefinition("a", "A", "system", null, null, null, Array.Empty<string>(), Array.Empty<ModelMessage>(), "hash");
    var conversation = new Conversation();
    conversation.BeginUserTurn("old question"); conversation.CommitAssistant("old answer");
    conversation.BeginUserTurn("recent question"); conversation.CommitAssistant("recent answer");
    var request = new PromptCompiler().Compile(character, conversation, "new input", 15);
    Assert(request.Messages[request.Messages.Count - 1].Text == "new input", "latest user turn must remain");
    Assert(request.Messages.Any(x => x.Text == "recent answer"), "recent complete turn must remain before older turns");
}

static void CharacterChatCommitsCompletedResponses()
{
    var character = new CharacterDefinition("a", "A", "system", null, null, null, Array.Empty<string>(), Array.Empty<ModelMessage>(), "hash");
    var conversation = new Conversation();
    var adapter = new MockModelAdapter("mock", _ => new[] { ModelEvent.Token("hello"), ModelEvent.Completed() });
    var session = adapter.CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
    var service = new CharacterChatService(character, conversation, session, new PromptCompiler(), 128);
    Consume(service.SendAsync("hi", CancellationToken.None)).GetAwaiter().GetResult();
    Assert(conversation.Committed.Count == 2 && conversation.Committed[1].Text == "hello", "only completed assistant responses must commit");
}

static async Task Consume(IAsyncEnumerable<ModelEvent> events)
{
    await foreach (var ignored in events) { }
}

static void CharacterBundleLoads()
{
    var root = Path.Combine(Path.GetTempPath(), "haruchat-contract-" + Guid.NewGuid().ToString("N"), "sample");
    Directory.CreateDirectory(root);
    try
    {
        File.WriteAllText(Path.Combine(root, "manifest.json"), "{\"schemaVersion\":1,\"id\":\"sample\",\"displayName\":\"샘플\"}");
        File.WriteAllText(Path.Combine(root, "system.md"), "안녕하세요");
        var value = new CharacterBundleLoader().Load(root);
        Assert(value.Id == "sample" && value.System == "안녕하세요", "minimal strict UTF-8 bundle must load");
    }
    finally { Directory.Delete(Path.GetDirectoryName(root)!, true); }
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
