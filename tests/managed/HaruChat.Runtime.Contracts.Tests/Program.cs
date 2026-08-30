using HaruChat.Runtime.LocalModels;
using HaruChat.Runtime.Models;
using HaruChat.Runtime.Characters;
using HaruChat.LlamaCpp;
using System.Runtime.InteropServices;
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
    ("Local adapter streams normalized backend events", LocalAdapterStreamsNormalizedEvents),
    ("Local adapter incrementally decodes split UTF-8 and stops", LocalAdapterDecodesSplitUtf8AndStops),
    ("Local adapter cancellation terminates without hanging", LocalAdapterCancellationTerminates),
    ("LlamaCpp backend consumes the C ABI bootstrap stream when configured", LlamaCppBackendConsumesBootstrapStream),
    ("Model profile validates and applies precedence", ModelProfileValidatesPrecedence),
    ("Prompt compiler retains recent conversation within budget", PromptCompilerPreservesRecentTurns),
    ("Prompt compiler has deterministic section order and overflow", PromptCompilerOrderAndOverflow),
    ("Character chat commits only completed responses", CharacterChatCommitsCompletedResponses),
    ("Character chat rolls back incomplete responses", CharacterChatRollsBackIncompleteResponses),
    ("Character chat rolls back error terminal", CharacterChatRollsBackError),
    ("Character chat supports mock multi-turn streaming", CharacterChatSupportsMultiTurn),
    ("New conversation waits for an active send", NewConversationWaitsForActiveSend),
    ("Character bundle loader validates a minimal bundle", CharacterBundleLoads),
    ("Character catalog rejects normalized duplicate IDs", CharacterCatalogRejectsNormalizedDuplicates),
    ("Character bundle loader rejects symlinks", CharacterBundleRejectsSymlink),
    ("Character bundle rejects invalid content and traversal", CharacterBundleRejectsInvalidContent),
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
    catch (ModelOperationException error) { Assert(error.Code == ModelErrorCode.NotFound, "unknown adapter must use structured not-found error"); }
}

static void LocalAdapterDecodesSplitUtf8AndStops()
{
    var backend = new StreamingBackend(new[] { new LocalBackendEvent(LocalBackendEventKind.Token, 1, new byte[] { 0xEC, 0x95 }, default, default), new LocalBackendEvent(LocalBackendEventKind.Token, 2, new byte[] { 0x88, (byte)'<', (byte)'E', (byte)'N', (byte)'D', (byte)'>' }, default, default), new LocalBackendEvent(LocalBackendEventKind.Completed, 3, Array.Empty<byte>(), default, default) });
    var profile = new ModelProfile("qwen35", 1, "Qwen3.5", 128, new GenerationOptions(), new[] { "<END>" });
    var adapter = new LocalModelAdapter("local", backend, new ModelConfig("/tmp/model.gguf", "qwen35"), profile);
    var session = adapter.CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
    Assert(ConsumeText(session.GenerateAsync(new ModelRequest(new[] { new ModelMessage(ModelRole.User, "x") }), CancellationToken.None)).GetAwaiter().GetResult() == "안", "split UTF-8 and stop sequence must be normalized");
    session.DisposeAsync().GetAwaiter().GetResult();
}

static void LocalAdapterCancellationTerminates()
{
    var backend = new StreamingBackend(new[] { new LocalBackendEvent(LocalBackendEventKind.Cancelled, 1, Array.Empty<byte>(), default, default) });
    var adapter = new LocalModelAdapter("local", backend, new ModelConfig("/tmp/model.gguf", "qwen35"), new ModelProfile("qwen35", 1, "Qwen3.5", 128, new GenerationOptions()));
    var session = adapter.CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
    try { Consume(session.GenerateAsync(new ModelRequest(new[] { new ModelMessage(ModelRole.User, "x") }), CancellationToken.None)).GetAwaiter().GetResult(); throw new InvalidOperationException("cancelled generation must throw"); }
    catch (OperationCanceledException) { }
    session.DisposeAsync().GetAwaiter().GetResult();
}

static void ModelProfileValidatesPrecedence()
{
    var defaults = new GenerationOptions(10, 0.2f, 2, 0.5f); var overrideValue = new GenerationOptions(11, 0.3f, 3, 0.6f);
    var profile = new ModelProfile("p", 1, "Qwen3.5", 64, defaults); var config = new ModelConfig("model", "p", contextWindowOverride: 96, generationOverride: overrideValue);
    Assert(profile.ResolveContextWindow(config) == 96 && profile.ResolveGeneration(config).MaximumOutputTokens == 11, "runtime overrides must win over profile defaults");
    try { _ = new ModelProfile("p", 1, "Qwen3.5", 64, defaults, new[] { "" }); throw new InvalidOperationException("empty stop must fail"); } catch (ArgumentException) { }
}

static void LlamaCppBackendConsumesBootstrapStream()
{
    var libraryDirectory = Environment.GetEnvironmentVariable("HARUCHAT_LLMCORE_LIBRARY_DIR");
    if (string.IsNullOrWhiteSpace(libraryDirectory)) return; // Explicit opt-in: native build artifact is not committed.
    var library = Path.Combine(libraryDirectory, OperatingSystem.IsWindows() ? "llmcore.dll" : OperatingSystem.IsMacOS() ? "libllmcore.dylib" : "libllmcore.so");
    NativeLibrary.SetDllImportResolver(typeof(LlamaCppBackend).Assembly, (name, _, _) => name == "llmcore" ? NativeLibrary.Load(library) : IntPtr.Zero);
    var fixture = Path.Combine(Path.GetTempPath(), "haruchat-managed-bootstrap-" + Guid.NewGuid().ToString("N") + ".gguf"); File.WriteAllBytes(fixture, System.Text.Encoding.ASCII.GetBytes("GGUFfixture"));
    try
    {
        var profile = new ModelProfile("qwen35", 1, "Qwen3.5", 128, new GenerationOptions(16));
        var backend = new LlamaCppBackend(); var adapter = new LocalModelAdapter("local", backend, new ModelConfig(fixture, "qwen35"), profile);
        var session = adapter.CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
        var text = ConsumeText(session.GenerateAsync(new ModelRequest(new[] { new ModelMessage(ModelRole.User, "hello") }), CancellationToken.None)).GetAwaiter().GetResult();
        Assert(text == "mock response", "managed adapter must consume copied bootstrap C ABI stream");
        session.DisposeAsync().GetAwaiter().GetResult(); backend.DisposeAsync().GetAwaiter().GetResult();
    }
    finally { File.Delete(fixture); }
}

static void LocalAdapterStreamsNormalizedEvents()
{
    var backend = new StreamingBackend();
    var profile = new ModelProfile("qwen35", 1, "Qwen3.5", 128, new GenerationOptions(16, 0.7f, 40, 0.9f));
    var adapter = new LocalModelAdapter("local", backend, new ModelConfig("/tmp/model.gguf", "qwen35"), profile);
    var session = adapter.CreateSessionAsync(new ModelSessionOptions(128, 8), CancellationToken.None).GetAwaiter().GetResult();
    var output = ConsumeText(session.GenerateAsync(new ModelRequest(new[] { new ModelMessage(ModelRole.User, "안녕") }, new GenerationOptions(7, 0.4f, 12, 0.8f)), CancellationToken.None)).GetAwaiter().GetResult();
    Assert(output == "응답" && backend.LastGeneration!.MaximumOutputTokens == 7 && backend.LastGeneration.Temperature == 0.4f, "adapter must map request options and ordered tokens");
    session.DisposeAsync().GetAwaiter().GetResult();
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

static void PromptCompilerOrderAndOverflow()
{
    var character = new CharacterDefinition("a", "A", "system", "personality", "style", "scenario", new[] { "lore" }, new[] { new ModelMessage(ModelRole.User, "example-user"), new ModelMessage(ModelRole.Assistant, "example-assistant") }, "hash");
    var request = new PromptCompiler().Compile(character, new Conversation(), "latest", 128);
    Assert(string.Join("|", request.Messages.Select(x => x.Text)) == "system|personality|style|scenario|lore|example-user|example-assistant|latest", "section order must be stable");
    try { _ = new PromptCompiler().Compile(character, new Conversation(), "latest", 1); throw new InvalidOperationException("required prompt over budget must fail"); } catch (ContextBudgetExceededException) { }
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

static void CharacterChatRollsBackIncompleteResponses()
{
    var character = new CharacterDefinition("a", "A", "system", null, null, null, Array.Empty<string>(), Array.Empty<ModelMessage>(), "hash");
    var conversation = new Conversation();
    var adapter = new MockModelAdapter("mock", _ => new[] { ModelEvent.Token("partial") });
    var session = adapter.CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
    var service = new CharacterChatService(character, conversation, session, new PromptCompiler(), 128);
    Consume(service.SendAsync("hi", CancellationToken.None)).GetAwaiter().GetResult();
    Assert(conversation.Committed.Count == 0, "incomplete responses must roll back the pending user turn");
}

static void CharacterChatRollsBackError()
{
    var character = new CharacterDefinition("a", "A", "system", null, null, null, Array.Empty<string>(), Array.Empty<ModelMessage>(), "hash"); var conversation = new Conversation();
    var session = new MockModelAdapter("mock", _ => new[] { ModelEvent.Token("partial"), ModelEvent.ErrorEvent(new ModelError(ModelErrorCode.BackendFailure, "boom")), ModelEvent.Completed() }).CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
    Consume(new CharacterChatService(character, conversation, session, new PromptCompiler(), 128).SendAsync("hi", CancellationToken.None)).GetAwaiter().GetResult();
    Assert(conversation.Committed.Count == 0, "error terminal must roll back even if a later completed event exists");
}

static void CharacterChatSupportsMultiTurn()
{
    var character = new CharacterDefinition("a", "A", "system", null, null, null, Array.Empty<string>(), Array.Empty<ModelMessage>(), "hash"); var conversation = new Conversation();
    var session = new MockModelAdapter("mock", request => new[] { ModelEvent.Token(request.Messages[request.Messages.Count - 1].Text + "!"), ModelEvent.Completed() }).CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
    var service = new CharacterChatService(character, conversation, session, new PromptCompiler(), 128);
    Consume(service.SendAsync("one", CancellationToken.None)).GetAwaiter().GetResult(); Consume(service.SendAsync("two", CancellationToken.None)).GetAwaiter().GetResult();
    Assert(conversation.Committed.Count == 4 && conversation.Committed[3].Text == "two!", "mock adapter must complete multiple committed turns");
}

static void NewConversationWaitsForActiveSend()
{
    var character = new CharacterDefinition("a", "A", "system", null, null, null, Array.Empty<string>(), Array.Empty<ModelMessage>(), "hash");
    var conversation = new Conversation();
    var adapter = new MockModelAdapter("mock", _ => new[] { ModelEvent.Token("partial") });
    var session = adapter.CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
    var service = new CharacterChatService(character, conversation, session, new PromptCompiler(), 128);
    var enumerator = service.SendAsync("hi", CancellationToken.None).GetAsyncEnumerator();
    try
    {
        Assert(enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult(), "send must emit the first token");
        var reset = service.NewConversationAsync(CancellationToken.None);
        Assert(!reset.IsCompleted, "new conversation must wait until the active send releases the session");
        enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        reset.GetAwaiter().GetResult();
        Assert(conversation.Committed.Count == 0, "new conversation must clear the rolled-back conversation after reset");
    }
    finally { enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
}

static async Task Consume(IAsyncEnumerable<ModelEvent> events)
{
    await foreach (var ignored in events) { }
}

static async Task<string> ConsumeText(IAsyncEnumerable<ModelEvent> events)
{
    var output = new System.Text.StringBuilder();
    await foreach (var item in events) if (item.Kind == ModelEventKind.Token) output.Append(item.Text);
    return output.ToString();
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

static void CharacterCatalogRejectsNormalizedDuplicates()
{
    var first = new CharacterDefinition("café", "A", "system", null, null, null, Array.Empty<string>(), Array.Empty<ModelMessage>(), "a");
    var second = new CharacterDefinition("CAFE\u0301", "B", "system", null, null, null, Array.Empty<string>(), Array.Empty<ModelMessage>(), "b");
    try { _ = new CharacterCatalog(new[] { first, second }); throw new InvalidOperationException("normalized duplicate must fail"); }
    catch (CharacterValidationException) { }
}

static void CharacterBundleRejectsSymlink()
{
    if (OperatingSystem.IsWindows()) return;
    var parent = Path.Combine(Path.GetTempPath(), "haruchat-contract-" + Guid.NewGuid().ToString("N"));
    var root = Path.Combine(parent, "sample");
    Directory.CreateDirectory(root);
    try
    {
        File.WriteAllText(Path.Combine(root, "manifest.json"), "{\"schemaVersion\":1,\"id\":\"sample\",\"displayName\":\"sample\"}");
        File.CreateSymbolicLink(Path.Combine(root, "system.md"), "/dev/null");
        try { _ = new CharacterBundleLoader().Load(root); throw new InvalidOperationException("symlink must fail"); }
        catch (CharacterValidationException) { }
    }
    finally { Directory.Delete(parent, true); }
}

static void CharacterBundleRejectsInvalidContent()
{
    var parent = Path.Combine(Path.GetTempPath(), "haruchat-contract-" + Guid.NewGuid().ToString("N")); var root = Path.Combine(parent, "sample"); Directory.CreateDirectory(root);
    try
    {
        File.WriteAllText(Path.Combine(root, "manifest.json"), "{\"schemaVersion\":2,\"id\":\"sample\",\"displayName\":\"sample\"}"); File.WriteAllText(Path.Combine(root, "system.md"), "ok");
        try { _ = new CharacterBundleLoader().Load(root); throw new InvalidOperationException("schema mismatch must fail"); } catch (CharacterValidationException) { }
        File.WriteAllText(Path.Combine(root, "manifest.json"), "{\"schemaVersion\":1,\"id\":\"sample\",\"displayName\":\"sample\"}"); File.WriteAllBytes(Path.Combine(root, "system.md"), new byte[] { 0xFF });
        try { _ = new CharacterBundleLoader().Load(root); throw new InvalidOperationException("invalid UTF-8 must fail"); } catch (CharacterValidationException) { }
    }
    finally { Directory.Delete(parent, true); }
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

sealed class StreamingBackend : ILocalModelBackend
{
    private int _pollCount; private readonly IReadOnlyList<LocalBackendEvent>? _events;
    public StreamingBackend(IReadOnlyList<LocalBackendEvent>? events = null) { _events = events; }
    public LocalGenerationOptions? LastGeneration { get; private set; }
    public ValueTask DisposeAsync() => default;
    public Task<LocalBackendResult<LocalRuntimeHandle>> CreateRuntimeAsync(LocalRuntimeOptions o, CancellationToken ct) => Task.FromResult(LocalBackendResult<LocalRuntimeHandle>.Success(new LocalRuntimeHandle(1)));
    public Task<LocalBackendResult> DestroyRuntimeAsync(LocalRuntimeHandle h, CancellationToken ct) => Task.FromResult(LocalBackendResult.Success());
    public Task<LocalBackendResult<LocalModelHandle>> LoadModelAsync(LocalRuntimeHandle h, LocalModelLoadOptions o, CancellationToken ct) => Task.FromResult(LocalBackendResult<LocalModelHandle>.Success(new LocalModelHandle(2)));
    public Task<LocalBackendResult> UnloadModelAsync(LocalModelHandle h, CancellationToken ct) => Task.FromResult(LocalBackendResult.Success());
    public Task<LocalBackendResult<LocalContextHandle>> CreateContextAsync(LocalModelHandle h, LocalContextOptions o, CancellationToken ct) => Task.FromResult(LocalBackendResult<LocalContextHandle>.Success(new LocalContextHandle(3)));
    public Task<LocalBackendResult> ResetContextAsync(LocalContextHandle h, CancellationToken ct) => Task.FromResult(LocalBackendResult.Success());
    public Task<LocalBackendResult> DestroyContextAsync(LocalContextHandle h, CancellationToken ct) => Task.FromResult(LocalBackendResult.Success());
    public Task<LocalBackendResult<LocalGenerationHandle>> StartGenerationAsync(LocalContextHandle h, LocalGenerationOptions o, CancellationToken ct) { LastGeneration = o; return Task.FromResult(LocalBackendResult<LocalGenerationHandle>.Success(new LocalGenerationHandle(4))); }
    public Task<LocalBackendResult<LocalEventBatch>> PollEventsAsync(LocalGenerationHandle h, int maximum, CancellationToken ct) { if (_pollCount++ == 0) return Task.FromResult(LocalBackendResult<LocalEventBatch>.Success(new LocalEventBatch(_events ?? new[] { new LocalBackendEvent(LocalBackendEventKind.Token, 1, System.Text.Encoding.UTF8.GetBytes("응답"), default, default), new LocalBackendEvent(LocalBackendEventKind.Completed, 2, Array.Empty<byte>(), default, default) }))); return Task.FromResult(LocalBackendResult<LocalEventBatch>.Success(new LocalEventBatch(Array.Empty<LocalBackendEvent>()))); }
    public Task<LocalBackendResult> CancelGenerationAsync(LocalGenerationHandle h, CancellationToken ct) => Task.FromResult(LocalBackendResult.Success());
    public Task<LocalBackendResult> DestroyGenerationAsync(LocalGenerationHandle h, CancellationToken ct) => Task.FromResult(LocalBackendResult.Success());
    public Task<LocalBackendResult<LocalModelMetadata>> GetModelMetadataAsync(LocalModelHandle h, CancellationToken ct) => Task.FromResult(LocalBackendResult<LocalModelMetadata>.Success(new LocalModelMetadata("test", 128)));
    public Task<LocalBackendResult<LocalGenerationMetrics>> GetGenerationMetricsAsync(LocalGenerationHandle h, CancellationToken ct) => Task.FromResult(LocalBackendResult<LocalGenerationMetrics>.Success(default));
}
