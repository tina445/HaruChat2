#nullable enable
using HaruChat.Runtime.LocalModels;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HaruChat.Runtime.Models
{
    /// <summary>Applies profile policy and converts copied backend events to normalized events.</summary>
    public sealed class LocalModelAdapter : IModelAdapter
    {
        private readonly ILocalModelBackend _backend; private readonly ModelConfig _config; private readonly ModelProfile? _profile; private readonly ModelProfileCatalog? _profiles;
        public LocalModelAdapter(string id, ILocalModelBackend backend, ModelConfig config, ModelProfile profile)
        { if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Adapter ID is required.", nameof(id)); Id = id; _backend = backend ?? throw new ArgumentNullException(nameof(backend)); _config = config ?? throw new ArgumentNullException(nameof(config)); _profile = profile ?? throw new ArgumentNullException(nameof(profile)); if (!string.IsNullOrWhiteSpace(config.ProfileId) && !string.Equals(config.ProfileId, profile.Id, StringComparison.Ordinal)) throw new ArgumentException("Configuration profile does not match profile."); }
        public LocalModelAdapter(string id, ILocalModelBackend backend, ModelConfig config, ModelProfileCatalog profiles)
        { if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Adapter ID is required.", nameof(id)); Id = id; _backend = backend ?? throw new ArgumentNullException(nameof(backend)); _config = config ?? throw new ArgumentNullException(nameof(config)); _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles)); }
        public LocalModelAdapter(string id, ILocalModelBackend backend, ModelConfig config)
        { if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Adapter ID is required.", nameof(id)); if (!(backend is ILocalModelChatTemplateBackend)) throw new ArgumentException("Backend does not support embedded GGUF chat templates.", nameof(backend)); Id = id; _backend = backend; _config = config ?? throw new ArgumentNullException(nameof(config)); }
        public string Id { get; } public ModelProfile? SelectedProfile { get; private set; } public ModelCapabilities Capabilities { get { return (SelectedProfile ?? _profile)?.Capabilities ?? new ModelCapabilities(); } }
        public async Task<IModelSession> CreateSessionAsync(ModelSessionOptions options, CancellationToken ct)
        {
            ModelProfile.ValidateModelChecksum(_config); var watch = Stopwatch.StartNew(); var runtime = await _backend.CreateRuntimeAsync(new LocalRuntimeOptions(), ct).ConfigureAwait(false); Ensure(runtime);
            try { var runtimeMetadata = await _backend.GetRuntimeMetadataAsync(runtime.Value, ct).ConfigureAwait(false); Ensure(runtimeMetadata); var model = await _backend.LoadModelAsync(runtime.Value, new LocalModelLoadOptions(_config.ModelPath), ct).ConfigureAwait(false); Ensure(model); try { var metadata = await _backend.GetModelMetadataAsync(model.Value, ct).ConfigureAwait(false); var catalogProfile = ResolveCatalogProfile(metadata); var embedded = _profile == null && string.IsNullOrWhiteSpace(_config.ProfileId) && _backend is ILocalModelChatTemplateBackend; var profile = _profile ?? catalogProfile ?? new ModelProfile("gguf-embedded", 1, new ChatTemplate("{role}:{content}", "assistant:"), options.ContextWindowTokens, new GenerationOptions()); SelectedProfile = profile; var context = await _backend.CreateContextAsync(model.Value, new LocalContextOptions(profile.ResolveContextWindow(_config), options.BatchSize), ct).ConfigureAwait(false); Ensure(context); return new Session(_backend, runtime.Value, model.Value, context.Value, profile, catalogProfile, _config, watch.Elapsed, runtimeMetadata.Value, metadata.IsSuccess ? metadata.Value : null, embedded); } catch { await _backend.UnloadModelAsync(model.Value, CancellationToken.None).ConfigureAwait(false); throw; } }
            catch { await _backend.DestroyRuntimeAsync(runtime.Value, CancellationToken.None).ConfigureAwait(false); throw; }
        }
        private static void Ensure(LocalBackendResult value) { if (!value.IsSuccess) throw Failure(value.Error); }
        private static void Ensure<T>(LocalBackendResult<T> value) { if (!value.IsSuccess) throw Failure(value.Error); }
        private ModelProfile? ResolveCatalogProfile(LocalBackendResult<LocalModelMetadata> metadata)
        {
            if (_profiles == null) return null;
            if (!metadata.IsSuccess)
            {
                if (!string.IsNullOrWhiteSpace(_config.ProfileId)) Ensure(metadata);
                return null;
            }
            if (!string.IsNullOrWhiteSpace(_config.ProfileId)) return _profiles.Resolve(_config.ProfileId, metadata.Value);
            try { return _profiles.Resolve(_config.ProfileId, metadata.Value); }
            catch (ModelOperationException) { return null; }
        }
        private static ModelOperationException Failure(LocalBackendError error) { return new ModelOperationException(Map(error.Code, error.Message), error.Message); }
        private static ModelErrorCode Map(LocalBackendErrorCode value, string? message) { return string.Equals(message, "context limit reached", StringComparison.Ordinal) ? ModelErrorCode.ContextBudgetExceeded : Map(value); }
        private static ModelErrorCode Map(LocalBackendErrorCode value) { switch (value) { case LocalBackendErrorCode.InvalidArgument: return ModelErrorCode.InvalidRequest; case LocalBackendErrorCode.NotFound: return ModelErrorCode.NotFound; case LocalBackendErrorCode.Busy: return ModelErrorCode.Busy; case LocalBackendErrorCode.Cancelled: return ModelErrorCode.Cancelled; case LocalBackendErrorCode.Unsupported: return ModelErrorCode.Unsupported; default: return ModelErrorCode.BackendFailure; } }

        private sealed class Session : IModelSession, ITokenCountingModelSession
        {
            private readonly ILocalModelBackend _backend; private readonly LocalRuntimeHandle _runtime; private readonly LocalModelHandle _model; private readonly LocalContextHandle _context; private readonly ModelProfile _profile; private readonly ModelProfile? _templateFallbackProfile; private readonly bool _embeddedTemplate; private readonly ModelConfig _config; private readonly TimeSpan _loadDuration; private readonly LocalRuntimeMetadata _runtimeMetadata; private readonly LocalModelMetadata? _metadata; private readonly SemaphoreSlim _lifetime = new SemaphoreSlim(1, 1); private bool _disposed; private ModelUsage _usage = new ModelUsage(null, null, null); private ModelDiagnostics _diagnostics;
            public Session(ILocalModelBackend backend, LocalRuntimeHandle runtime, LocalModelHandle model, LocalContextHandle context, ModelProfile profile, ModelProfile? templateFallbackProfile, ModelConfig config, TimeSpan loadDuration, LocalRuntimeMetadata runtimeMetadata, LocalModelMetadata? metadata, bool embeddedTemplate = false)
            { _backend = backend; _runtime = runtime; _model = model; _context = context; _profile = profile; _templateFallbackProfile = templateFallbackProfile; _embeddedTemplate = embeddedTemplate; _config = config; _loadDuration = loadDuration; _runtimeMetadata = runtimeMetadata; _metadata = metadata; _diagnostics = new ModelDiagnostics(runtimeMetadata.BackendName, runtimeMetadata.BackendName.IndexOf("metal", StringComparison.OrdinalIgnoreCase) >= 0 ? true : (bool?)null, metadata?.ContextWindowTokens ?? profile.ContextWindowTokens, loadDuration, null, null, null); }
            public async IAsyncEnumerable<ModelEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
            {
                if (request == null) throw new ArgumentNullException(nameof(request)); await _lifetime.WaitAsync(ct).ConfigureAwait(false); var queue = new EventQueue(128); var job = default(LocalGenerationHandle); var started = false; Task? pump = null; using var pumpCancellation = new CancellationTokenSource();
                try
                {
                    ThrowIfDisposed();
                    // ModelRequest is a complete, provider-neutral conversation snapshot. Clear the
                    // native KV cache before evaluating it so historical turns are represented once,
                    // by Conversation/PromptCompiler, rather than accumulating on every request.
                    var reset = await _backend.ResetContextAsync(_context, ct).ConfigureAwait(false); Ensure(reset);
                    var options = request.Generation ?? _profile.ResolveGeneration(_config); var prompt = await SerializeAsync(request.Messages, ct).ConfigureAwait(false); var start = await _backend.StartGenerationAsync(_context, new LocalGenerationOptions(prompt, options.MaximumOutputTokens, options.Temperature, options.TopK, options.TopP, options.Seed.HasValue ? unchecked((uint)options.Seed.Value) : unchecked((uint)Guid.NewGuid().GetHashCode())), ct).ConfigureAwait(false); Ensure(start); job = start.Value; started = true; pump = PumpAsync(job, queue, pumpCancellation.Token);
                    while (true) { ModelEvent item; try { item = await queue.ReadAsync(ct).ConfigureAwait(false); } catch (OperationCanceledException) { await _backend.CancelGenerationAsync(job, CancellationToken.None).ConfigureAwait(false); throw; } yield return item; if (item.IsTerminal) break; }
                    await pump.ConfigureAwait(false);
                }
                finally
                {
                    if (started)
                    {
                        pumpCancellation.Cancel(); queue.Complete();
                        await _backend.CancelGenerationAsync(job, CancellationToken.None).ConfigureAwait(false);
                        if (pump != null) { try { await pump.ConfigureAwait(false); } catch (OperationCanceledException) { } }
                        await _backend.DestroyGenerationAsync(job, CancellationToken.None).ConfigureAwait(false);
                    }
                    _lifetime.Release();
                }
            }
            private async Task PumpAsync(LocalGenerationHandle job, EventQueue queue, CancellationToken cancellationToken)
            {
                var decoder = new UTF8Encoding(false, true).GetDecoder(); var stops = new StopFilter(WithReservedOutputStops(_profile.StopSequences)); var reasoning = new ReasoningFilter(_profile.ReasoningOutput); var watch = Stopwatch.StartNew(); TimeSpan? firstToken = null;
                try { while (true) { cancellationToken.ThrowIfCancellationRequested(); var batch = await _backend.PollEventsAsync(job, 32, cancellationToken).ConfigureAwait(false); Ensure(batch); if (batch.Value.Events.Count == 0) { await Task.Delay(8, cancellationToken).ConfigureAwait(false); continue; } foreach (var raw in batch.Value.Events) { switch (raw.Kind) { case LocalBackendEventKind.Token: var text = Decode(decoder, raw.Payload, false); if (text.Length > 0 && firstToken == null) firstToken = watch.Elapsed; if (!await WriteFragmentsAsync(queue, reasoning.Push(text, false), stops, cancellationToken).ConfigureAwait(false)) return; break; case LocalBackendEventKind.Metrics: _usage = new ModelUsage(raw.Metrics.PromptTokenCount, raw.Metrics.GeneratedTokenCount, raw.Metrics.Elapsed); _diagnostics = new ModelDiagnostics(_runtimeMetadata.BackendName, _runtimeMetadata.BackendName.IndexOf("metal", StringComparison.OrdinalIgnoreCase) >= 0 ? true : (bool?)null, _metadata?.ContextWindowTokens ?? _profile.ContextWindowTokens, _loadDuration, firstToken, Rate(raw.Metrics.PromptTokenCount, raw.Metrics.Elapsed), Rate(raw.Metrics.GeneratedTokenCount, raw.Metrics.Elapsed)); await queue.WriteAsync(ModelEvent.UsageSnapshot(_usage), cancellationToken).ConfigureAwait(false); break; case LocalBackendEventKind.Completed: if (!await WriteFragmentsAsync(queue, reasoning.Push(Decode(decoder, ReadOnlyMemory<byte>.Empty, true), true), stops, cancellationToken).ConfigureAwait(false)) return; if (!await WriteTextAsync(queue, stops.Flush(), stops.Stopped, cancellationToken).ConfigureAwait(false)) return; await queue.WriteAsync(ModelEvent.Completed(), cancellationToken).ConfigureAwait(false); queue.Complete(); return; case LocalBackendEventKind.Cancelled: queue.Complete(new OperationCanceledException()); return; case LocalBackendEventKind.Error: await queue.WriteAsync(ModelEvent.ErrorEvent(new ModelError(Map(raw.Error.Code, raw.Error.Message), raw.Error.Message)), cancellationToken).ConfigureAwait(false); queue.Complete(); return; default: queue.Complete(new ModelOperationException(ModelErrorCode.BackendFailure, "Unknown local event.")); return; } } } }
                catch (Exception error) { queue.Complete(error is ModelOperationException || error is OperationCanceledException ? error : new ModelOperationException(ModelErrorCode.BackendFailure, error.Message)); }
            }
            private static async Task<bool> WriteFragmentsAsync(EventQueue queue, IReadOnlyList<ReasoningFragment> fragments, StopFilter stops, CancellationToken cancellationToken) { foreach (var fragment in fragments) { if (fragment.IsReasoning) { if (fragment.Text.Length > 0) await queue.WriteAsync(ModelEvent.Reasoning(fragment.Text), cancellationToken).ConfigureAwait(false); continue; } if (!await WriteTextAsync(queue, stops.Push(fragment.Text), stops.Stopped, cancellationToken).ConfigureAwait(false)) return false; } return true; }
            private static async Task<bool> WriteTextAsync(EventQueue queue, string text, bool stopped, CancellationToken cancellationToken) { if (text.Length > 0) await queue.WriteAsync(ModelEvent.Token(text), cancellationToken).ConfigureAwait(false); if (!stopped) return true; await queue.WriteAsync(ModelEvent.Completed(), cancellationToken).ConfigureAwait(false); queue.Complete(); return false; }
            private static string Decode(Decoder decoder, ReadOnlyMemory<byte> value, bool flush) { var bytes = value.ToArray(); var chars = new char[Math.Max(1, Encoding.UTF8.GetMaxCharCount(bytes.Length))]; decoder.Convert(bytes, 0, bytes.Length, chars, 0, chars.Length, flush, out _, out var used, out _); return new string(chars, 0, used); }
            private static IReadOnlyList<string> WithReservedOutputStops(IReadOnlyList<string> profileStops) { var stops = new List<string>(profileStops); if (!stops.Contains("<|im_end|>")) stops.Add("<|im_end|>"); return stops; }
            private static double? Rate(long tokens, TimeSpan elapsed) { return elapsed.TotalSeconds <= 0 ? (double?)null : tokens / elapsed.TotalSeconds; }
            public async Task ResetAsync(CancellationToken ct) { await _lifetime.WaitAsync(ct).ConfigureAwait(false); try { ThrowIfDisposed(); var result = await _backend.ResetContextAsync(_context, ct).ConfigureAwait(false); Ensure(result); } finally { _lifetime.Release(); } }
            public Task<ModelUsage> GetUsageAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(_usage); }
            public Task<ModelDiagnostics> GetDiagnosticsAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(_diagnostics); }
            public async Task<ModelTokenCount> CountTokensAsync(ModelRequest request, CancellationToken ct)
            {
                if (request == null) throw new ArgumentNullException(nameof(request));
                await _lifetime.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ThrowIfDisposed();
                    var result = await _backend.CountTokensAsync(_model, await SerializeAsync(request.Messages, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                    Ensure(result);
                    return new ModelTokenCount(result.Value, true);
                }
                finally { _lifetime.Release(); }
            }
            public async ValueTask DisposeAsync() { await _lifetime.WaitAsync().ConfigureAwait(false); try { if (_disposed) return; _disposed = true; await _backend.DestroyContextAsync(_context, CancellationToken.None).ConfigureAwait(false); await _backend.UnloadModelAsync(_model, CancellationToken.None).ConfigureAwait(false); await _backend.DestroyRuntimeAsync(_runtime, CancellationToken.None).ConfigureAwait(false); } finally { _lifetime.Release(); } }
            private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(Session)); }
            private async Task<string> SerializeAsync(IReadOnlyList<ModelMessage> messages, CancellationToken ct) { if (!_embeddedTemplate) return _profile.ChatTemplate.Render(messages); var converted = new List<LocalChatMessage>(); foreach (var message in messages) converted.Add(new LocalChatMessage(message.Role.ToString().ToLowerInvariant(), message.Text)); var result = await ((ILocalModelChatTemplateBackend)_backend).ApplyChatTemplateAsync(_model, converted, ct).ConfigureAwait(false); if (result.IsSuccess) return result.Value; if (result.Error.Code == LocalBackendErrorCode.Unsupported && _templateFallbackProfile != null) return _templateFallbackProfile.ChatTemplate.Render(messages); Ensure(result); return string.Empty; }
        }
        private readonly struct ReasoningFragment { public ReasoningFragment(string text, bool isReasoning) { Text = text; IsReasoning = isReasoning; } public string Text { get; } public bool IsReasoning { get; } }
        private sealed class ReasoningFilter
        {
            private readonly ReasoningOutputPolicy _policy; private string _buffer = string.Empty; private bool _inside;
            public ReasoningFilter(ReasoningOutputPolicy? policy) { _policy = policy ?? new ReasoningOutputPolicy("<think>", "</think>", ReasoningOutputMode.Hide); }
            public IReadOnlyList<ReasoningFragment> Push(string value, bool flush)
            {
                var output = new List<ReasoningFragment>();
                _buffer += value;
                while (_buffer.Length > 0)
                {
                    var marker = _inside ? _policy.CloseMarker : _policy.OpenMarker; var index = _buffer.IndexOf(marker, StringComparison.Ordinal);
                    if (index >= 0)
                    {
                        Add(output, _buffer.Substring(0, index), _inside);
                        _buffer = _buffer.Substring(index + marker.Length); _inside = !_inside; continue;
                    }
                    var retained = flush ? 0 : Math.Min(_buffer.Length, marker.Length - 1); var length = _buffer.Length - retained;
                    if (length > 0) { Add(output, _buffer.Substring(0, length), _inside); _buffer = _buffer.Substring(length); }
                    break;
                }
                return output;
            }
            private void Add(List<ReasoningFragment> output, string value, bool reasoning) { if (value.Length == 0 || (reasoning && _policy.Mode == ReasoningOutputMode.Hide)) return; output.Add(new ReasoningFragment(value, reasoning && _policy.Mode == ReasoningOutputMode.Separate)); }
        }
        private sealed class StopFilter { private readonly IReadOnlyList<string> _stops; private readonly int _hold; private string _buffer = string.Empty; public bool Stopped { get; private set; } public StopFilter(IReadOnlyList<string> stops) { _stops = stops; foreach (var value in stops) _hold = Math.Max(_hold, value.Length - 1); } public string Push(string value) { if (Stopped) return string.Empty; _buffer += value; var index = -1; foreach (var stop in _stops) { var current = _buffer.IndexOf(stop, StringComparison.Ordinal); if (current >= 0 && (index < 0 || current < index)) index = current; } if (index >= 0) { var output = _buffer.Substring(0, index); _buffer = string.Empty; Stopped = true; return output; } if (_buffer.Length <= _hold) return string.Empty; var count = _buffer.Length - _hold; var result = _buffer.Substring(0, count); _buffer = _buffer.Substring(count); return result; } public string Flush() { if (Stopped) return string.Empty; var result = _buffer; _buffer = string.Empty; return result; } }
        private sealed class EventQueue { private readonly ConcurrentQueue<ModelEvent> _items = new ConcurrentQueue<ModelEvent>(); private readonly SemaphoreSlim _available = new SemaphoreSlim(0); private readonly SemaphoreSlim _capacity; private Exception? _completion; private int _completed; public EventQueue(int capacity) { _capacity = new SemaphoreSlim(capacity); } public async Task WriteAsync(ModelEvent item, CancellationToken ct) { await _capacity.WaitAsync(ct).ConfigureAwait(false); if (Volatile.Read(ref _completed) != 0) { _capacity.Release(); return; } _items.Enqueue(item); _available.Release(); } public async Task<ModelEvent> ReadAsync(CancellationToken ct) { while (true) { if (_items.TryDequeue(out var item)) { _capacity.Release(); return item; } if (Volatile.Read(ref _completed) != 0) { if (_completion != null) throw _completion; throw new InvalidOperationException("Event stream completed without terminal event."); } await _available.WaitAsync(ct).ConfigureAwait(false); } } public void Complete(Exception? error = null) { _completion = error; Interlocked.Exchange(ref _completed, 1); _available.Release(); } }
    }
    public sealed class ModelOperationException : Exception { public ModelOperationException(ModelErrorCode code, string message) : base(message) { Code = code; } public ModelErrorCode Code { get; } }
}
