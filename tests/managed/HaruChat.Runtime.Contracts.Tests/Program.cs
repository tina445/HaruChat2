using HaruChat.Runtime.LocalModels;
using HaruChat.Runtime.Models;
using HaruChat.Runtime.Characters;
using HaruChat.Runtime.Memory;
using HaruChat.Runtime.Agent;
using HaruChat.Memory.Sqlite;
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
    ("Local adapter resets native context before full prompt replay", LocalAdapterResetsBeforeGeneration),
    ("Local adapter exposes copied runtime diagnostics", LocalAdapterExposesRuntimeDiagnostics),
    ("Local adapter incrementally decodes split UTF-8 and stops", LocalAdapterDecodesSplitUtf8AndStops),
    ("Local adapter normalizes standard reasoning and control tokens", LocalAdapterFiltersReasoningAndControlTokens),
    ("Local adapter cancellation terminates without hanging", LocalAdapterCancellationTerminates),
    ("LlamaCpp backend consumes the C ABI bootstrap stream when configured", LlamaCppBackendConsumesBootstrapStream),
    ("Model profile validates and applies precedence", ModelProfileValidatesPrecedence),
    ("Data-only profiles render Qwen and Gemma chat protocols", DataOnlyProfilesRenderChatProtocols),
    ("Profileless local adapter uses the GGUF embedded chat template", ProfilelessAdapterUsesEmbeddedChatTemplate),
    ("Profileless local adapter reports an unsupported GGUF template", ProfilelessAdapterReportsUnsupportedTemplate),
    ("Runtime settings and context advice preserve model constraints", RuntimeSettingsAndContextAdvice),
    ("Model profile catalog makes constrained metadata fallback explicit", ModelProfileCatalogResolvesSafely),
    ("Prompt compiler retains recent conversation within budget", PromptCompilerPreservesRecentTurns),
    ("Prompt compiler has deterministic section order and overflow", PromptCompilerOrderAndOverflow),
    ("Prompt compiler gives character voice priority", PromptCompilerEnforcesCharacterVoice),
    ("Conversation compaction retains recent raw turns and orders summary", ConversationCompactionRetainsRecentTurns),
    ("Character chat commits only completed responses", CharacterChatCommitsCompletedResponses),
    ("Character chat rolls back incomplete responses", CharacterChatRollsBackIncompleteResponses),
    ("Character chat rolls back error terminal", CharacterChatRollsBackError),
    ("Character chat supports mock multi-turn streaming", CharacterChatSupportsMultiTurn),
    ("Completed history is sent with the next request", CompletedHistoryIsSentWithNextRequest),
    ("Prompt compiler never silently evicts conversation", PromptCompilerNeverSilentlyEvictsConversation),
    ("Session handoff keeps summary and latest turns", SessionHandoffKeepsSummaryAndLatestTurns),
    ("New conversation waits for an active send", NewConversationWaitsForActiveSend),
    ("Character session replacement resets conversation and uses new session", CharacterSessionReplacementResetsConversation),
    ("Character bundle loader validates a minimal bundle", CharacterBundleLoads),
    ("Character catalog rejects normalized duplicate IDs", CharacterCatalogRejectsNormalizedDuplicates),
    ("Character bundle loader rejects symlinks", CharacterBundleRejectsSymlink),
    ("Character bundle rejects invalid content and traversal", CharacterBundleRejectsInvalidContent),
    ("Character bundle loads case-insensitive Markdown lore", CharacterBundleLoadsUppercaseLore),
    ("Prompt compiler places memory before conversation", PromptCompilerPlacesMemoryBeforeConversation),
    ("Memory prompt policy bounds context contribution", MemoryPromptPolicyBoundsContext),
    ("SQLite memory persists, isolates, ranks, exports, and deletes", SqliteMemoryLifecycle),
    ("Character chat retrieves memory before generation", CharacterChatRetrievesMemory),
    ("Character chat records only completed opt-in memory", CharacterChatRecordsOptInMemory),
    ("Automatic memory candidates are character-scoped and conservative", AutomaticMemoryCandidatesAreScopedAndConservative),
    ("SQLite rejects cross-character memory session links", SqliteRejectsCrossCharacterMemorySessionLinks),
    ("Agent tool loop enforces approval and completes a read-only call", AgentToolLoopEnforcesPolicy),
};

static ChatTemplate Template(string message = "<{role}>\n{content}</{role}>\n", string assistant = "<assistant>\n")
{
    return new ChatTemplate(message, assistant);
}

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
    Assert(context.ContextWindowTokens == 4096 && context.BatchSize == 256 && context.MicroBatchSize == 128 &&
           context.KeyCacheType == LocalKvCacheType.Quantized8 && context.ValueCacheType == LocalKvCacheType.Quantized8 &&
           context.FlashAttention == LocalFlashAttentionMode.Enabled && context.OffloadKqv, "long-context defaults changed");
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
    var profile = new ModelProfile("qwen35", 1, Template(), 128, new GenerationOptions(), new[] { "<END>" });
    var adapter = new LocalModelAdapter("local", backend, new ModelConfig("/tmp/model.gguf", "qwen35"), profile);
    var session = adapter.CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
    Assert(ConsumeText(session.GenerateAsync(new ModelRequest(new[] { new ModelMessage(ModelRole.User, "x") }), CancellationToken.None)).GetAwaiter().GetResult() == "안", "split UTF-8 and stop sequence must be normalized");
    session.DisposeAsync().GetAwaiter().GetResult();
}

static void LocalAdapterCancellationTerminates()
{
    var backend = new StreamingBackend(new[] { new LocalBackendEvent(LocalBackendEventKind.Cancelled, 1, Array.Empty<byte>(), default, default) });
    var adapter = new LocalModelAdapter("local", backend, new ModelConfig("/tmp/model.gguf", "qwen35"), new ModelProfile("qwen35", 1, Template(), 128, new GenerationOptions()));
    var session = adapter.CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
    try { Consume(session.GenerateAsync(new ModelRequest(new[] { new ModelMessage(ModelRole.User, "x") }), CancellationToken.None)).GetAwaiter().GetResult(); throw new InvalidOperationException("cancelled generation must throw"); }
    catch (OperationCanceledException) { }
    session.DisposeAsync().GetAwaiter().GetResult();
}

static void LocalAdapterFiltersReasoningAndControlTokens()
{
    var events = new[]
    {
        new LocalBackendEvent(LocalBackendEventKind.Token, 1, System.Text.Encoding.UTF8.GetBytes("<th"), default, default),
        new LocalBackendEvent(LocalBackendEventKind.Token, 2, System.Text.Encoding.UTF8.GetBytes("ink>internal</th"), default, default),
        new LocalBackendEvent(LocalBackendEventKind.Token, 3, System.Text.Encoding.UTF8.GetBytes("ink>visible<|im"), default, default),
        new LocalBackendEvent(LocalBackendEventKind.Token, 4, System.Text.Encoding.UTF8.GetBytes("_end|>ignored"), default, default),
        new LocalBackendEvent(LocalBackendEventKind.Completed, 5, Array.Empty<byte>(), default, default),
    };
    var profile = new ModelProfile("gemma", 1, Template(), 128, new GenerationOptions());
    var adapter = new LocalModelAdapter("local", new StreamingBackend(events), new ModelConfig("/tmp/gemma.gguf", "gemma"), profile);
    var session = adapter.CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
    var output = CollectEvents(session.GenerateAsync(new ModelRequest(new[] { new ModelMessage(ModelRole.User, "x") }), CancellationToken.None)).GetAwaiter().GetResult();
    Assert(string.Concat(output.Where(x => x.Kind == ModelEventKind.Token).Select(x => x.Text)) == "visible", "standard normalization must hide fragmented reasoning and stop fragmented control tokens without a profile");
    Assert(output.Count(x => x.Kind == ModelEventKind.Completed) == 1, "control-token stop must yield exactly one terminal event");
    session.DisposeAsync().GetAwaiter().GetResult();

    var separateProfile = new ModelProfile("separate", 1, Template(), 128, new GenerationOptions(), reasoningOutput: new ReasoningOutputPolicy("<think>", "</think>", ReasoningOutputMode.Separate));
    var separate = new LocalModelAdapter("separate", new StreamingBackend(new[] { new LocalBackendEvent(LocalBackendEventKind.Token, 1, System.Text.Encoding.UTF8.GetBytes("<think>private</think>answer"), default, default), new LocalBackendEvent(LocalBackendEventKind.Completed, 2, Array.Empty<byte>(), default, default) }), new ModelConfig("/tmp/model.gguf", "separate"), separateProfile);
    var separateSession = separate.CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
    var separateOutput = CollectEvents(separateSession.GenerateAsync(new ModelRequest(new[] { new ModelMessage(ModelRole.User, "x") }), CancellationToken.None)).GetAwaiter().GetResult();
    Assert(string.Concat(separateOutput.Where(x => x.Kind == ModelEventKind.Reasoning).Select(x => x.Text)) == "private" && string.Concat(separateOutput.Where(x => x.Kind == ModelEventKind.Token).Select(x => x.Text)) == "answer", "separate policy must preserve channel distinction without a model-family branch");
    separateSession.DisposeAsync().GetAwaiter().GetResult();

    var showProfile = new ModelProfile("show", 1, Template(), 128, new GenerationOptions(), reasoningOutput: new ReasoningOutputPolicy("<think>", "</think>", ReasoningOutputMode.Show));
    var show = new LocalModelAdapter("show", new StreamingBackend(new[] { new LocalBackendEvent(LocalBackendEventKind.Token, 1, System.Text.Encoding.UTF8.GetBytes("<think>visible thought</think>answer"), default, default), new LocalBackendEvent(LocalBackendEventKind.Completed, 2, Array.Empty<byte>(), default, default) }), new ModelConfig("/tmp/model.gguf", "show"), showProfile);
    var showSession = show.CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
    Assert(ConsumeText(showSession.GenerateAsync(new ModelRequest(new[] { new ModelMessage(ModelRole.User, "x") }), CancellationToken.None)).GetAwaiter().GetResult() == "visible thoughtanswer", "show policy must suppress delimiters without suppressing content");
    showSession.DisposeAsync().GetAwaiter().GetResult();
}

static void ModelProfileValidatesPrecedence()
{
    var defaults = new GenerationOptions(10, 0.2f, 2, 0.5f); var overrideValue = new GenerationOptions(11, 0.3f, 3, 0.6f);
    var profile = new ModelProfile("p", 1, Template(), 64, defaults); var config = new ModelConfig("model", "p", contextWindowOverride: 96, generationOverride: overrideValue);
    Assert(profile.ResolveContextWindow(config) == 96 && profile.ResolveGeneration(config).MaximumOutputTokens == 11, "runtime overrides must win over profile defaults");
    try { _ = new ModelProfile("p", 1, Template(), 64, defaults, new[] { "" }); throw new InvalidOperationException("empty stop must fail"); } catch (ArgumentException) { }
}

static void DataOnlyProfilesRenderChatProtocols()
{
    var qwen = new ModelProfile("qwen", 1, new ChatTemplate("<|im_start|>{role}\n{content}<|im_end|>\n", "<|im_start|>assistant\n"), 128, new GenerationOptions());
    var gemmaRoles = new Dictionary<ModelRole, string> { { ModelRole.Assistant, "model" } };
    var gemma = new ModelProfile("gemma", 1, new ChatTemplate("<|turn>{role}\n{content}<turn|>\n", "<|turn>model\n", gemmaRoles), 128, new GenerationOptions());
    var messages = new[] { new ModelMessage(ModelRole.System, "Be helpful."), new ModelMessage(ModelRole.User, "Hello") };
    Assert(qwen.ChatTemplate.Render(messages) == "<|im_start|>system\nBe helpful.<|im_end|>\n<|im_start|>user\nHello<|im_end|>\n<|im_start|>assistant\n", "Qwen protocol must be profile data, not adapter code");
    Assert(gemma.ChatTemplate.Render(messages) == "<|turn>system\nBe helpful.<turn|>\n<|turn>user\nHello<turn|>\n<|turn>model\n", "Gemma protocol must be selectable without a new adapter");

    var backend = new StreamingBackend();
    var adapter = new LocalModelAdapter("local", backend, new ModelConfig("/tmp/gemma.gguf", "gemma"), gemma);
    var session = adapter.CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
    Consume(session.GenerateAsync(new ModelRequest(messages), CancellationToken.None)).GetAwaiter().GetResult();
    Assert(backend.LastGeneration!.Prompt == gemma.ChatTemplate.Render(messages), "LocalModelAdapter must use the selected profile without a family branch");
    session.DisposeAsync().GetAwaiter().GetResult();

    var catalog = new ModelProfileCatalog(new[] { new ModelProfile("auto-gemma", 1, gemma.ChatTemplate, 128, new GenerationOptions(), architectureContains: new[] { "test" }) });
    var automaticBackend = new StreamingBackend();
    var automaticAdapter = new LocalModelAdapter("local-auto", automaticBackend, new ModelConfig("/tmp/selected-only.gguf"), catalog);
    var automaticSession = automaticAdapter.CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
    Consume(automaticSession.GenerateAsync(new ModelRequest(messages), CancellationToken.None)).GetAwaiter().GetResult();
    Assert(automaticAdapter.SelectedProfile!.Id == "auto-gemma" && automaticBackend.LastGeneration!.Prompt == "embedded:Be helpful.|Hello", "GGUF embedded template must take priority over catalog prompt text");
    automaticSession.DisposeAsync().GetAwaiter().GetResult();

    var fallbackBackend = new StreamingBackend(embeddedTemplateSupported: false);
    var fallbackAdapter = new LocalModelAdapter("local-fallback", fallbackBackend, new ModelConfig("/tmp/legacy-gemma.gguf"), catalog);
    var fallbackSession = fallbackAdapter.CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
    Consume(fallbackSession.GenerateAsync(new ModelRequest(messages), CancellationToken.None)).GetAwaiter().GetResult();
    Assert(fallbackBackend.LastGeneration!.Prompt == gemma.ChatTemplate.Render(messages), "catalog profile must render only when a legacy GGUF lacks an embedded template");
    fallbackSession.DisposeAsync().GetAwaiter().GetResult();

    var path = Path.Combine(Path.GetTempPath(), "haruchat-profile-" + Guid.NewGuid().ToString("N") + ".json");
    try
    {
        File.WriteAllText(path, "{\"id\":\"gemma\",\"schemaVersion\":1,\"chatTemplate\":{\"messageTemplate\":\"<|turn>{role}\\n{content}<turn|>\\n\",\"assistantTemplate\":\"<|turn>model\\n\",\"roles\":[{\"role\":\"assistant\",\"name\":\"model\"}]},\"contextWindowTokens\":128,\"maximumOutputTokens\":8,\"temperature\":0.7,\"topK\":40,\"topP\":0.95}");
        var loaded = ModelProfileLoader.Load(path);
        Assert(loaded.ChatTemplate.Render(new[] { new ModelMessage(ModelRole.Assistant, "Done") }).Contains("<|turn>model\nDone", StringComparison.Ordinal), "profile JSON role mapping must be applied without a model-family branch");
    }
    finally { File.Delete(path); }
}

static void RuntimeSettingsAndContextAdvice()
{
    var profile = new ModelProfile("p", 1, Template(), 4096, new GenerationOptions(256, 0.7f));
    var config = new ModelConfig("model", "p"); var settings = new ModelRuntimeSettings(2048, 0.35f); var applied = settings.Apply(config, profile.Defaults);
    Assert(profile.ResolveContextWindow(applied) == 2048 && applied.GenerationOverride!.Temperature == 0.35f, "settings must map to model-owned context and temperature overrides");
    var advice = ContextWindowAdvisor.Recommend(4096, 600, 256, 256, 3072);
    Assert(advice.MaximumTokens == 3072 && advice.ReservedTokens == 1112 && advice.HardwareMeasured, "context advice must expose measured cap and prompt reservation");
    Assert(profile.ResolveContextWindow(new ModelConfig("model", "p", contextWindowOverride: 8192)) == 8192, "model profile preserves the explicit runtime context override");
}

static void ModelProfileCatalogResolvesSafely()
{
    var qwen = new ModelProfile("qwen", 1, Template(), 64, new GenerationOptions(), architectureContains: new[] { "qwen" });
    var llama = new ModelProfile("llama", 1, Template(), 64, new GenerationOptions(), architectureContains: new[] { "llama" });
    var catalog = new ModelProfileCatalog(new[] { qwen, llama }); var metadata = new LocalModelMetadata("Qwen3ForCausalLM", 1024);
    Assert(catalog.Resolve(null, metadata).Id == "qwen", "metadata fallback must select the unique compatible profile");
    Assert(catalog.Resolve("llama", metadata).Id == "llama", "explicit profile binding must win over metadata");
    try { _ = catalog.Resolve(null, new LocalModelMetadata("unknown", 1)); throw new InvalidOperationException("unknown metadata must not guess a profile"); } catch (ModelOperationException error) { Assert(error.Code == ModelErrorCode.InvalidConfiguration, "unsafe metadata fallback must be actionable"); }
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
        var profile = new ModelProfile("qwen35", 1, Template(), 128, new GenerationOptions(16));
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
    var profile = new ModelProfile("qwen35", 1, Template(), 128, new GenerationOptions(16, 0.7f, 40, 0.9f));
    var adapter = new LocalModelAdapter("local", backend, new ModelConfig("/tmp/model.gguf", "qwen35"), profile);
    var session = adapter.CreateSessionAsync(new ModelSessionOptions(128, 8), CancellationToken.None).GetAwaiter().GetResult();
    var output = ConsumeText(session.GenerateAsync(new ModelRequest(new[] { new ModelMessage(ModelRole.User, "안녕") }, new GenerationOptions(7, 0.4f, 12, 0.8f)), CancellationToken.None)).GetAwaiter().GetResult();
    Assert(output == "응답" && backend.LastGeneration!.MaximumOutputTokens == 7 && backend.LastGeneration.Temperature == 0.4f, "adapter must map request options and ordered tokens");
    session.DisposeAsync().GetAwaiter().GetResult();
}

static void LocalAdapterResetsBeforeGeneration()
{
    var backend = new StreamingBackend();
    var adapter = new LocalModelAdapter("local", backend, new ModelConfig("/tmp/model.gguf", "qwen35"), new ModelProfile("qwen35", 1, Template(), 128, new GenerationOptions(16)));
    var session = adapter.CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
    Consume(session.GenerateAsync(new ModelRequest(new[] { new ModelMessage(ModelRole.User, "one") }), CancellationToken.None)).GetAwaiter().GetResult();
    Consume(session.GenerateAsync(new ModelRequest(new[] { new ModelMessage(ModelRole.User, "one"), new ModelMessage(ModelRole.Assistant, "answer"), new ModelMessage(ModelRole.User, "two") }), CancellationToken.None)).GetAwaiter().GetResult();
    Assert(backend.ResetCount == 2, "each complete prompt snapshot must replace, not append to, native context");
    session.DisposeAsync().GetAwaiter().GetResult();
}

static void ProfilelessAdapterUsesEmbeddedChatTemplate()
{
    var backend = new StreamingBackend();
    var adapter = new LocalModelAdapter("local", backend, new ModelConfig("/tmp/new-model.gguf"));
    var session = adapter.CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
    var messages = new[]
    {
        new ModelMessage(ModelRole.System, "system instruction"),
        new ModelMessage(ModelRole.User, "hello"),
    };

    Consume(session.GenerateAsync(new ModelRequest(messages), CancellationToken.None)).GetAwaiter().GetResult();

    Assert(backend.TemplateMessages != null && backend.TemplateMessages.Count == 2, "embedded template must receive the full conversation");
    Assert(backend.TemplateMessages![0].Role == "system" && backend.TemplateMessages[1].Role == "user", "roles must use GGUF chat-template names");
    Assert(backend.LastGeneration!.Prompt == "embedded:system instruction|hello", "profileless adapter must use the backend-rendered GGUF prompt");
    session.DisposeAsync().GetAwaiter().GetResult();
}

static void ProfilelessAdapterReportsUnsupportedTemplate()
{
    var backend = new StreamingBackend(embeddedTemplateSupported: false);
    var adapter = new LocalModelAdapter("local", backend, new ModelConfig("/tmp/no-template.gguf"));
    var session = adapter.CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
    try
    {
        Consume(session.GenerateAsync(new ModelRequest(new[] { new ModelMessage(ModelRole.User, "hello") }), CancellationToken.None)).GetAwaiter().GetResult();
        throw new InvalidOperationException("A missing embedded template must not silently use a guessed prompt format.");
    }
    catch (ModelOperationException error) { Assert(error.Code == ModelErrorCode.Unsupported, "unsupported embedded template must remain recoverable and structured"); }
    finally { session.DisposeAsync().GetAwaiter().GetResult(); }
}

static void LocalAdapterExposesRuntimeDiagnostics()
{
    var backend = new StreamingBackend();
    var profile = new ModelProfile("qwen35", 1, Template(assistant: "<assistant>\n<think>\n\n</think>\n\n"), 128, new GenerationOptions(16));
    var adapter = new LocalModelAdapter("local", backend, new ModelConfig("/tmp/model.gguf", "qwen35"), profile);
    var session = adapter.CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
    Consume(session.GenerateAsync(new ModelRequest(new[] { new ModelMessage(ModelRole.User, "hello") }), CancellationToken.None)).GetAwaiter().GetResult();
    var diagnostics = session.GetDiagnosticsAsync(CancellationToken.None).GetAwaiter().GetResult();
    Assert(diagnostics.Backend == "test-backend-metal" && diagnostics.AccelerationEnabled == true, "runtime metadata must flow through the managed adapter");
    Assert(backend.LastGeneration!.Prompt.EndsWith("<think>\n\n</think>\n\n", StringComparison.Ordinal), "non-thinking is a profile-owned template policy");
    session.DisposeAsync().GetAwaiter().GetResult();
}

static void PromptCompilerPreservesRecentTurns()
{
    var character = new CharacterDefinition("a", "A", "system", null, null, null, Array.Empty<string>(), Array.Empty<ModelMessage>(), "hash");
    var conversation = new Conversation();
    conversation.BeginUserTurn("old question"); conversation.CommitAssistant("old answer");
    conversation.BeginUserTurn("recent question"); conversation.CommitAssistant("recent answer");
    var request = new PromptCompiler(new CharacterPromptPolicy(false, false)).Compile(character, conversation, "new input", 15);
    Assert(request.Messages[request.Messages.Count - 1].Text == "new input", "latest user turn must remain");
    Assert(request.Messages.Any(x => x.Text == "recent answer"), "recent complete turn must remain before older turns");
}

static void PromptCompilerOrderAndOverflow()
{
    var character = new CharacterDefinition("a", "A", "system", "personality", "style", "scenario", new[] { "lore" }, new[] { new ModelMessage(ModelRole.User, "example-user"), new ModelMessage(ModelRole.Assistant, "example-assistant") }, "hash");
    var request = new PromptCompiler(new CharacterPromptPolicy(false, false)).Compile(character, new Conversation(), "latest", 128);
    Assert(string.Join("|", request.Messages.Select(x => x.Text)) == "system|personality|style|scenario|lore|example-user|example-assistant|latest", "section order must be stable");
    try { _ = new PromptCompiler(new CharacterPromptPolicy(false, false)).Compile(character, new Conversation(), "latest", 1); throw new InvalidOperationException("required prompt over budget must fail"); } catch (ContextBudgetExceededException) { }
}

static void PromptCompilerEnforcesCharacterVoice()
{
    var character = new CharacterDefinition("a", "A", "system", "warm", "informal short sentences", null, Array.Empty<string>(), Array.Empty<ModelMessage>(), "hash");
    var plan = new PromptCompiler().CompilePlan(character, new Conversation(), "hello", 128);
    Assert(plan.CompilerVersion == PromptCompiler.CompilerVersion && plan.CharacterId == "a", "prompt plan must preserve its character snapshot");
    Assert(plan.Request.Messages.Any(x => x.Text.Contains("generic helpful-assistant voice", StringComparison.Ordinal)), "default policy must prioritize the declared character voice");
}

static void ConversationCompactionRetainsRecentTurns()
{
    var character = new CharacterDefinition("a", "A", "system", null, null, null, Array.Empty<string>(), Array.Empty<ModelMessage>(), "hash");
    var conversation = new Conversation();
    for (var index = 0; index < 10; index++) { conversation.BeginUserTurn("u" + index); conversation.CommitAssistant("a" + index); }
    conversation.ApplyCompaction("facts\n- durable fact\ndecisions\n- decision\nopen_loops\n- none\nrelationships\n- none\ncommitments\n- none\nnarrative\n- brief", 2);
    Assert(conversation.Archived.Count == 20 && conversation.Committed.Count == 16, "archive must preserve source while live prompt loses compacted pairs");
    var request = new PromptCompiler(new CharacterPromptPolicy(false, false), summaryPolicy: new ConversationSummaryPromptPolicy(128)).Compile(character, conversation, "latest", 512);
    var summaryIndex = request.Messages.ToList().FindIndex(x => x.Text.StartsWith("Compressed conversation context:", StringComparison.Ordinal));
    var turnIndex = request.Messages.ToList().FindIndex(x => x.Text == "u2");
    Assert(summaryIndex >= 0 && turnIndex > summaryIndex, "summary must precede retained raw history");
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

static void CompletedHistoryIsSentWithNextRequest()
{
    var character = new CharacterDefinition("a", "A", "system", null, null, null, Array.Empty<string>(), Array.Empty<ModelMessage>(), "hash");
    var requests = new List<ModelRequest>();
    var session = new MockModelAdapter("mock", request => { requests.Add(request); return new[] { ModelEvent.Token("answer"), ModelEvent.Completed() }; }).CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
    var service = new CharacterChatService(character, new Conversation(), session, new PromptCompiler(), 128);
    Consume(service.SendAsync("first user", CancellationToken.None)).GetAwaiter().GetResult();
    Consume(service.SendAsync("second user", CancellationToken.None)).GetAwaiter().GetResult();
    Assert(requests.Count == 2 && requests[1].Messages.Any(x => x.Role == ModelRole.User && x.Text == "first user") && requests[1].Messages.Any(x => x.Role == ModelRole.Assistant && x.Text == "answer") && requests[1].Messages.Last().Text == "second user", "the next request must contain the completed user/assistant pair before its new input");
}

static void PromptCompilerNeverSilentlyEvictsConversation()
{
    var character = new CharacterDefinition("a", "A", "system", null, null, null, Array.Empty<string>(), Array.Empty<ModelMessage>(), "hash");
    var conversation = new Conversation();
    conversation.BeginUserTurn("old-user"); conversation.CommitAssistant("old-assistant");
    conversation.BeginUserTurn("recent-user"); conversation.CommitAssistant("recent-assistant");
    var request = new PromptCompiler(new CharacterPromptPolicy(false, false)).Compile(character, conversation, "latest", 8);
    Assert(request.Messages.Any(x => x.Text == "old-user") && request.Messages.Any(x => x.Text == "recent-assistant"), "the compiler must leave overflow resolution to exact-token orchestration instead of dropping old turns");
}

static void SessionHandoffKeepsSummaryAndLatestTurns()
{
    var conversation = new Conversation();
    for (var i = 0; i < 3; i++) { conversation.BeginUserTurn("u" + i); conversation.CommitAssistant("a" + i); }
    conversation.ApplyCompaction("facts:\n- durable\ndecisions:\n- none\nopen_loops:\n- none\nrelationships:\n- none\ncommitments:\n- none\nnarrative:\n- brief", 1);
    var handoff = conversation.CreateSessionHandoff(300);
    Assert(handoff != null && handoff.Contains("durable", StringComparison.Ordinal) && handoff.Contains("a2", StringComparison.Ordinal), "opt-in session persistence must retain both compressed context and recent raw tail");
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

static void CharacterSessionReplacementResetsConversation()
{
    var first = new CharacterDefinition("first", "First", "system", null, null, null, Array.Empty<string>(), Array.Empty<ModelMessage>(), "first-hash");
    var second = new CharacterDefinition("second", "Second", "system", null, null, null, Array.Empty<string>(), Array.Empty<ModelMessage>(), "second-hash");
    var conversation = new Conversation();
    var firstSession = new MockModelAdapter("first", _ => new[] { ModelEvent.Token("first"), ModelEvent.Completed() }).CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
    var secondSession = new MockModelAdapter("second", _ => new[] { ModelEvent.Token("second"), ModelEvent.Completed() }).CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
    var service = new CharacterChatService(first, conversation, firstSession, new PromptCompiler(), 128);
    Consume(service.SendAsync("one", CancellationToken.None)).GetAwaiter().GetResult();
    service.ReplaceSessionAsync(second, secondSession, 128, CancellationToken.None).GetAwaiter().GetResult();
    Assert(conversation.Committed.Count == 0, "character/model switch must discard incompatible history");
    var output = ConsumeText(service.SendAsync("two", CancellationToken.None)).GetAwaiter().GetResult();
    Assert(output == "second" && conversation.Committed.Count == 2, "replacement must use the new session");
    service.DisposeAsync().GetAwaiter().GetResult();
}

static void PromptCompilerPlacesMemoryBeforeConversation()
{
    var character = new CharacterDefinition("a", "A", "system", null, null, null, Array.Empty<string>(), Array.Empty<ModelMessage>(), "hash");
    var memory = new MemoryItem("m", "a", "remembered fact", 50, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
    var request = new PromptCompiler(new CharacterPromptPolicy(false, false)).Compile(character, new Conversation(), "latest", 128, memories: new[] { memory });
    Assert(string.Join("|", request.Messages.Select(x => x.Text)) == "system|Relevant memory:\nremembered fact|latest", "memory must be compiled before conversation history");
}

static void MemoryPromptPolicyBoundsContext()
{
    var character = new CharacterDefinition("a", "A", "system", null, null, null, Array.Empty<string>(), Array.Empty<ModelMessage>(), "hash");
    var first = new MemoryItem("first", "a", "small fact", 50, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
    var oversized = new MemoryItem("second", "a", new string('x', 100), 50, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
    var request = new PromptCompiler(new CharacterPromptPolicy(false, false), new MemoryPromptPolicy(2, 8, 0)).Compile(character, new Conversation(), "latest", 128, memories: new[] { first, oversized });
    Assert(request.Messages.Any(x => x.Text.Contains("small fact", StringComparison.Ordinal)) && !request.Messages.Any(x => x.Text.Contains(new string('x', 100), StringComparison.Ordinal)), "memory over the configured token budget must not displace instructions or conversation");
}

static void SqliteMemoryLifecycle()
{
    var path = Path.Combine(Path.GetTempPath(), "haruchat-memory-" + Guid.NewGuid().ToString("N") + ".db");
    try
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_000_000); var expires = now.AddDays(7);
        var store = new SqliteMemoryStore(path); store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        store.SaveSettingsAsync(new MemorySettings("haru", true, TimeSpan.FromDays(7), 2, 96, false), CancellationToken.None).GetAwaiter().GetResult();
        var settings = store.GetSettingsAsync("haru", CancellationToken.None).GetAwaiter().GetResult();
        Assert(settings.Enabled && settings.MaximumRetrievedItems == 2 && settings.MaximumPromptTokens == 96 && !settings.IncludeRecentSessionSummary, "character settings must persist independently of memories");
        store.UpsertSessionAsync(new MemorySession("s1", "haru", "summary", now, now, expires), CancellationToken.None).GetAwaiter().GetResult();
        store.SaveMemoryAsync(new MemoryItem("first", "haru", "서울의 비 오는 날을 좋아한다", 20, now, now, "s1", expires), CancellationToken.None).GetAwaiter().GetResult();
        store.SaveMemoryAsync(new MemoryItem("second", "haru", "서울의 비 오는 날을 좋아한다", 80, now, now, "s1", expires), CancellationToken.None).GetAwaiter().GetResult();
        store.SaveMemoryAsync(new MemoryItem("other", "other", "서울의 비", 100, now, now, null, expires), CancellationToken.None).GetAwaiter().GetResult();
        var found = store.SearchAsync(new MemoryQuery("haru", "서울의 비", 8, now), CancellationToken.None).GetAwaiter().GetResult();
        Assert(found.Count == 2 && found[0].MemoryId == "second", "FTS retrieval must isolate character and rank importance deterministically");
        store.SaveMemoryAsync(new MemoryItem("first", "haru", "부산의 맑은 날을 좋아한다", 20, now, now.AddMinutes(1), "s1", expires), CancellationToken.None).GetAwaiter().GetResult();
        Assert(store.SearchAsync(new MemoryQuery("haru", "서울의", 8, now), CancellationToken.None).GetAwaiter().GetResult().Count == 1, "FTS triggers must remove stale content on update");
        Assert(store.ExportSessionsAsync("haru", CancellationToken.None).GetAwaiter().GetResult().Count == 1 && store.ExportMemoriesAsync("haru", CancellationToken.None).GetAwaiter().GetResult().Count == 2, "exports must contain only the requested namespace");
        store.DeleteExpiredAsync(expires, CancellationToken.None).GetAwaiter().GetResult();
        Assert(store.ExportMemoriesAsync("haru", CancellationToken.None).GetAwaiter().GetResult().Count == 0, "retention must delete expired memories");
        store.ClearCharacterAsync("other", CancellationToken.None).GetAwaiter().GetResult();
        Assert(store.ExportMemoriesAsync("other", CancellationToken.None).GetAwaiter().GetResult().Count == 0, "clear must delete the full character namespace");
        store.DisposeAsync().GetAwaiter().GetResult();
    }
    finally { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
}

static void CharacterChatRecordsOptInMemory()
{
    var path = Path.Combine(Path.GetTempPath(), "haruchat-memory-chat-" + Guid.NewGuid().ToString("N") + ".db");
    try
    {
        var store = new SqliteMemoryStore(path); var character = new CharacterDefinition("a", "A", "system", null, null, null, Array.Empty<string>(), Array.Empty<ModelMessage>(), "hash");
        var session = new MockModelAdapter("mock", _ => new[] { ModelEvent.Token("answer"), ModelEvent.Completed() }).CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
        var chat = new CharacterChatService(character, new Conversation(), session, new PromptCompiler(), 128, store, store, new MemoryPersistenceOptions(true, TimeSpan.FromDays(1)), new TestCandidateFactory());
        Consume(chat.SendAsync("question", CancellationToken.None)).GetAwaiter().GetResult();
        Assert(store.ExportSessionsAsync("a", CancellationToken.None).GetAwaiter().GetResult().Count == 1 && store.ExportMemoriesAsync("a", CancellationToken.None).GetAwaiter().GetResult().Count == 1, "completed opt-in turns must persist summary and explicit candidate");
        chat.DisposeAsync().GetAwaiter().GetResult(); store.DisposeAsync().GetAwaiter().GetResult();
    }
    finally { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
}

static void CharacterChatRetrievesMemory()
{
    var path = Path.Combine(Path.GetTempPath(), "haruchat-memory-retrieve-" + Guid.NewGuid().ToString("N") + ".db");
    try
    {
        var store = new SqliteMemoryStore(path); var now = DateTimeOffset.UtcNow; store.SaveMemoryAsync(new MemoryItem("memory", "a", "favorite color is blue", 80, now, now, expiresAt: now.AddDays(1)), CancellationToken.None).GetAwaiter().GetResult();
        var character = new CharacterDefinition("a", "A", "system", null, null, null, Array.Empty<string>(), Array.Empty<ModelMessage>(), "hash"); var sawMemory = false;
        var session = new MockModelAdapter("mock", request => { sawMemory = request.Messages.Any(x => x.Text.Contains("Relevant memory:\nfavorite color is blue", StringComparison.Ordinal)); return new[] { ModelEvent.Token("answer"), ModelEvent.Completed() }; }).CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
        var chat = new CharacterChatService(character, new Conversation(), session, new PromptCompiler(), 128, memoryRetriever: store, memoryOptions: new MemoryPersistenceOptions(true, TimeSpan.FromDays(1)));
        Consume(chat.SendAsync("favorite color", CancellationToken.None)).GetAwaiter().GetResult(); Assert(sawMemory, "retrieved memory must be in the provider-neutral request before generation");
        chat.DisposeAsync().GetAwaiter().GetResult(); store.DisposeAsync().GetAwaiter().GetResult();
    }
    finally { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
}

static void AutomaticMemoryCandidatesAreScopedAndConservative()
{
    var factory = new RuleBasedMemoryCandidateFactory(); var now = DateTimeOffset.UtcNow;
    var first = factory.Create("haru", "session", "기억해줘, 나는 민트 초콜릿을 좋아해.", "알겠어.", now, now.AddDays(1));
    var repeat = factory.Create("haru", "other", "기억해줘, 나는 민트 초콜릿을 좋아해.", "알겠어.", now, now.AddDays(1));
    Assert(first != null && repeat != null && first.MemoryId == repeat.MemoryId && first.CharacterId == "haru", "automatic facts must deduplicate inside a character namespace");
    Assert(factory.Create("haru", "session", "내 비밀번호는 1234야. 기억해줘.", "", now, null) == null, "sensitive values must never become automatic memories");
    Assert(factory.Create("haru", "session", "안녕!", "", now, null) == null, "ephemeral chat must not become automatic memory");
}

static void SqliteRejectsCrossCharacterMemorySessionLinks()
{
    var path = Path.Combine(Path.GetTempPath(), "haruchat-memory-isolation-" + Guid.NewGuid().ToString("N") + ".db");
    try
    {
        var store = new SqliteMemoryStore(path); var now = DateTimeOffset.UtcNow;
        store.UpsertSessionAsync(new MemorySession("only-a", "a", "", now, now), CancellationToken.None).GetAwaiter().GetResult();
        try { store.SaveMemoryAsync(new MemoryItem("cross", "b", "fact", 70, now, now, "only-a"), CancellationToken.None).GetAwaiter().GetResult(); throw new InvalidOperationException("cross-owner source session must fail"); }
        catch (MemoryOperationException error) { Assert(error.Code == MemoryErrorCode.InvalidData, "cross-character source must be invalid data"); }
        store.DisposeAsync().GetAwaiter().GetResult();
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}

static void AgentToolLoopEnforcesPolicy()
{
    var character = new CharacterDefinition("a", "A", "system", null, null, null, Array.Empty<string>(), Array.Empty<ModelMessage>(), "hash");
    var session = new MockModelAdapter("tool-mock", _ => new[] { ModelEvent.ToolCallRequested(new ModelToolCall("1", "time", "{}")), ModelEvent.Completed(ModelStopReason.ToolCall), ModelEvent.Completed() }).CreateSessionAsync(new ModelSessionOptions(128), CancellationToken.None).GetAwaiter().GetResult();
    var agent = new AgentRuntime(new ToolRegistry(new ITool[] { new CurrentTimeTool() }));
    var events = CollectEvents(agent.GenerateAsync(session, new ModelCapabilities(tools: true), new ModelRequest(new[] { new ModelMessage(ModelRole.User, "time?") }), new ToolExecutionContext(character.Id), CancellationToken.None)).GetAwaiter().GetResult();
    Assert(events.Any(x => x.Kind == ModelEventKind.ToolResult && x.ToolResult != null && x.ToolResult.Succeeded), "read-only allowlisted tools must return structured results");
    session.DisposeAsync().GetAwaiter().GetResult();
}

static async Task Consume(IAsyncEnumerable<ModelEvent> events)
{
    await foreach (var ignored in events) { }
}

static async Task<IReadOnlyList<ModelEvent>> CollectEvents(IAsyncEnumerable<ModelEvent> events)
{
    var result = new List<ModelEvent>();
    await foreach (var item in events) result.Add(item);
    return result;
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

static void CharacterBundleLoadsUppercaseLore()
{
    var parent = Path.Combine(Path.GetTempPath(), "haruchat-lore-" + Guid.NewGuid().ToString("N")); var root = Path.Combine(parent, "sample");
    Directory.CreateDirectory(Path.Combine(root, "lore"));
    try
    {
        File.WriteAllText(Path.Combine(root, "manifest.json"), "{\"schemaVersion\":1,\"id\":\"sample\",\"displayName\":\"Sample\"}");
        File.WriteAllText(Path.Combine(root, "system.md"), "system"); File.WriteAllText(Path.Combine(root, "lore", "FACT.MD"), "lore");
        var definition = new CharacterBundleLoader().Load(root);
        Assert(definition.Lore.Count == 1 && definition.Lore[0] == "lore", "accepted Markdown extensions must be loaded on every host filesystem");
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
    public Task<LocalBackendResult<LocalRuntimeMetadata>> GetRuntimeMetadataAsync(LocalRuntimeHandle runtime, CancellationToken cancellationToken) => Task.FromResult(LocalBackendResult<LocalRuntimeMetadata>.Success(new LocalRuntimeMetadata("fake", "test", true, true, true)));
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
    public Task<LocalBackendResult<int>> CountTokensAsync(LocalModelHandle model, string utf8Text, CancellationToken cancellationToken) => Task.FromResult(LocalBackendResult<int>.Success(utf8Text?.Length ?? 0));
}

sealed class StreamingBackend : ILocalModelBackend, ILocalModelChatTemplateBackend
{
    private int _pollCount; private readonly IReadOnlyList<LocalBackendEvent>? _events; private readonly bool _embeddedTemplateSupported;
    public StreamingBackend(IReadOnlyList<LocalBackendEvent>? events = null, bool embeddedTemplateSupported = true) { _events = events; _embeddedTemplateSupported = embeddedTemplateSupported; }
    public LocalGenerationOptions? LastGeneration { get; private set; }
    public IReadOnlyList<LocalChatMessage>? TemplateMessages { get; private set; }
    public int ResetCount { get; private set; }
    public ValueTask DisposeAsync() => default;
    public Task<LocalBackendResult<LocalRuntimeHandle>> CreateRuntimeAsync(LocalRuntimeOptions o, CancellationToken ct) => Task.FromResult(LocalBackendResult<LocalRuntimeHandle>.Success(new LocalRuntimeHandle(1)));
    public Task<LocalBackendResult> DestroyRuntimeAsync(LocalRuntimeHandle h, CancellationToken ct) => Task.FromResult(LocalBackendResult.Success());
    public Task<LocalBackendResult<LocalRuntimeMetadata>> GetRuntimeMetadataAsync(LocalRuntimeHandle h, CancellationToken ct) => Task.FromResult(LocalBackendResult<LocalRuntimeMetadata>.Success(new LocalRuntimeMetadata("test-backend-metal", "test", true, true, false)));
    public Task<LocalBackendResult<LocalModelHandle>> LoadModelAsync(LocalRuntimeHandle h, LocalModelLoadOptions o, CancellationToken ct) => Task.FromResult(LocalBackendResult<LocalModelHandle>.Success(new LocalModelHandle(2)));
    public Task<LocalBackendResult> UnloadModelAsync(LocalModelHandle h, CancellationToken ct) => Task.FromResult(LocalBackendResult.Success());
    public Task<LocalBackendResult<LocalContextHandle>> CreateContextAsync(LocalModelHandle h, LocalContextOptions o, CancellationToken ct) => Task.FromResult(LocalBackendResult<LocalContextHandle>.Success(new LocalContextHandle(3)));
    public Task<LocalBackendResult> ResetContextAsync(LocalContextHandle h, CancellationToken ct) { ResetCount++; return Task.FromResult(LocalBackendResult.Success()); }
    public Task<LocalBackendResult> DestroyContextAsync(LocalContextHandle h, CancellationToken ct) => Task.FromResult(LocalBackendResult.Success());
    public Task<LocalBackendResult<LocalGenerationHandle>> StartGenerationAsync(LocalContextHandle h, LocalGenerationOptions o, CancellationToken ct) { _pollCount = 0; LastGeneration = o; return Task.FromResult(LocalBackendResult<LocalGenerationHandle>.Success(new LocalGenerationHandle(4))); }
    public Task<LocalBackendResult<LocalEventBatch>> PollEventsAsync(LocalGenerationHandle h, int maximum, CancellationToken ct) { if (_pollCount++ == 0) return Task.FromResult(LocalBackendResult<LocalEventBatch>.Success(new LocalEventBatch(_events ?? new[] { new LocalBackendEvent(LocalBackendEventKind.Token, 1, System.Text.Encoding.UTF8.GetBytes("응답"), default, default), new LocalBackendEvent(LocalBackendEventKind.Completed, 2, Array.Empty<byte>(), default, default) }))); return Task.FromResult(LocalBackendResult<LocalEventBatch>.Success(new LocalEventBatch(Array.Empty<LocalBackendEvent>()))); }
    public Task<LocalBackendResult> CancelGenerationAsync(LocalGenerationHandle h, CancellationToken ct) => Task.FromResult(LocalBackendResult.Success());
    public Task<LocalBackendResult> DestroyGenerationAsync(LocalGenerationHandle h, CancellationToken ct) => Task.FromResult(LocalBackendResult.Success());
    public Task<LocalBackendResult<LocalModelMetadata>> GetModelMetadataAsync(LocalModelHandle h, CancellationToken ct) => Task.FromResult(LocalBackendResult<LocalModelMetadata>.Success(new LocalModelMetadata("test", 128)));
    public Task<LocalBackendResult<LocalGenerationMetrics>> GetGenerationMetricsAsync(LocalGenerationHandle h, CancellationToken ct) => Task.FromResult(LocalBackendResult<LocalGenerationMetrics>.Success(default));
    public Task<LocalBackendResult<int>> CountTokensAsync(LocalModelHandle h, string utf8Text, CancellationToken ct) => Task.FromResult(LocalBackendResult<int>.Success(utf8Text?.Length ?? 0));
    public Task<LocalBackendResult<string>> ApplyChatTemplateAsync(LocalModelHandle h, IReadOnlyList<LocalChatMessage> messages, CancellationToken ct)
    {
        if (!_embeddedTemplateSupported) return Task.FromResult(LocalBackendResult<string>.Failure(LocalBackendErrorCode.Unsupported, "No embedded template."));
        TemplateMessages = new List<LocalChatMessage>(messages);
        return Task.FromResult(LocalBackendResult<string>.Success("embedded:" + string.Join("|", messages.Select(x => x.Content))));
    }
}
