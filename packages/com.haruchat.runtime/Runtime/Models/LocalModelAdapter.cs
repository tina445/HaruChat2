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
        private readonly ILocalModelBackend _backend; private readonly ModelConfig _config; private readonly ModelProfile _profile;
        public LocalModelAdapter(string id, ILocalModelBackend backend, ModelConfig config, ModelProfile profile)
        { if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Adapter ID is required.", nameof(id)); Id = id; _backend = backend ?? throw new ArgumentNullException(nameof(backend)); _config = config ?? throw new ArgumentNullException(nameof(config)); _profile = profile ?? throw new ArgumentNullException(nameof(profile)); if (!string.Equals(config.ProfileId, profile.Id, StringComparison.Ordinal)) throw new ArgumentException("Configuration profile does not match profile."); }
        public string Id { get; } public ModelCapabilities Capabilities { get { return _profile.Capabilities; } }
        public async Task<IModelSession> CreateSessionAsync(ModelSessionOptions options, CancellationToken ct)
        {
            ModelProfile.ValidateModelChecksum(_config); var watch = Stopwatch.StartNew(); var runtime = await _backend.CreateRuntimeAsync(new LocalRuntimeOptions(), ct).ConfigureAwait(false); Ensure(runtime);
            try { var model = await _backend.LoadModelAsync(runtime.Value, new LocalModelLoadOptions(_config.ModelPath), ct).ConfigureAwait(false); Ensure(model); try { var context = await _backend.CreateContextAsync(model.Value, new LocalContextOptions(_profile.ResolveContextWindow(_config), options.BatchSize), ct).ConfigureAwait(false); Ensure(context); var metadata = await _backend.GetModelMetadataAsync(model.Value, ct).ConfigureAwait(false); return new Session(_backend, runtime.Value, model.Value, context.Value, _profile, _config, watch.Elapsed, metadata.IsSuccess ? metadata.Value : null); } catch { await _backend.UnloadModelAsync(model.Value, CancellationToken.None).ConfigureAwait(false); throw; } }
            catch { await _backend.DestroyRuntimeAsync(runtime.Value, CancellationToken.None).ConfigureAwait(false); throw; }
        }
        private static void Ensure(LocalBackendResult value) { if (!value.IsSuccess) throw Failure(value.Error); }
        private static void Ensure<T>(LocalBackendResult<T> value) { if (!value.IsSuccess) throw Failure(value.Error); }
        private static ModelOperationException Failure(LocalBackendError error) { return new ModelOperationException(Map(error.Code), error.Message); }
        private static ModelErrorCode Map(LocalBackendErrorCode value) { switch (value) { case LocalBackendErrorCode.InvalidArgument: return ModelErrorCode.InvalidRequest; case LocalBackendErrorCode.NotFound: return ModelErrorCode.NotFound; case LocalBackendErrorCode.Busy: return ModelErrorCode.Busy; case LocalBackendErrorCode.Cancelled: return ModelErrorCode.Cancelled; case LocalBackendErrorCode.Unsupported: return ModelErrorCode.Unsupported; default: return ModelErrorCode.BackendFailure; } }

        private sealed class Session : IModelSession
        {
            private readonly ILocalModelBackend _backend; private readonly LocalRuntimeHandle _runtime; private readonly LocalModelHandle _model; private readonly LocalContextHandle _context; private readonly ModelProfile _profile; private readonly ModelConfig _config; private readonly TimeSpan _loadDuration; private readonly LocalModelMetadata? _metadata; private readonly SemaphoreSlim _lifetime = new SemaphoreSlim(1, 1); private bool _disposed; private ModelUsage _usage = new ModelUsage(null, null, null); private ModelDiagnostics _diagnostics;
            public Session(ILocalModelBackend backend, LocalRuntimeHandle runtime, LocalModelHandle model, LocalContextHandle context, ModelProfile profile, ModelConfig config, TimeSpan loadDuration, LocalModelMetadata? metadata)
            { _backend = backend; _runtime = runtime; _model = model; _context = context; _profile = profile; _config = config; _loadDuration = loadDuration; _metadata = metadata; _diagnostics = new ModelDiagnostics("llama.cpp", null, metadata?.ContextWindowTokens ?? profile.ContextWindowTokens, loadDuration, null, null, null); }
            public async IAsyncEnumerable<ModelEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
            {
                if (request == null) throw new ArgumentNullException(nameof(request)); await _lifetime.WaitAsync(ct).ConfigureAwait(false); var queue = new EventQueue(128); var job = default(LocalGenerationHandle); var started = false;
                try
                {
                    ThrowIfDisposed(); var options = request.Generation ?? _profile.ResolveGeneration(_config); var start = await _backend.StartGenerationAsync(_context, new LocalGenerationOptions(Serialize(request.Messages), options.MaximumOutputTokens, options.Temperature, options.TopK, options.TopP, (uint)(options.Seed ?? 0)), ct).ConfigureAwait(false); Ensure(start); job = start.Value; started = true; var pump = PumpAsync(job, queue);
                    while (true) { ModelEvent item; try { item = await queue.ReadAsync(ct).ConfigureAwait(false); } catch (OperationCanceledException) { await _backend.CancelGenerationAsync(job, CancellationToken.None).ConfigureAwait(false); throw; } yield return item; if (item.IsTerminal) break; }
                    await pump.ConfigureAwait(false);
                }
                finally { if (started) { await _backend.CancelGenerationAsync(job, CancellationToken.None).ConfigureAwait(false); await _backend.DestroyGenerationAsync(job, CancellationToken.None).ConfigureAwait(false); } _lifetime.Release(); }
            }
            private async Task PumpAsync(LocalGenerationHandle job, EventQueue queue)
            {
                var decoder = new UTF8Encoding(false, true).GetDecoder(); var stops = new StopFilter(_profile.StopSequences); var watch = Stopwatch.StartNew(); TimeSpan? firstToken = null;
                try { while (true) { var batch = await _backend.PollEventsAsync(job, 32, CancellationToken.None).ConfigureAwait(false); Ensure(batch); if (batch.Value.Events.Count == 0) { await Task.Delay(8).ConfigureAwait(false); continue; } foreach (var raw in batch.Value.Events) { switch (raw.Kind) { case LocalBackendEventKind.Token: var text = Decode(decoder, raw.Payload, false); if (text.Length > 0 && firstToken == null) firstToken = watch.Elapsed; if (!await WriteTextAsync(queue, stops.Push(text), stops.Stopped).ConfigureAwait(false)) return; break; case LocalBackendEventKind.Metrics: _usage = new ModelUsage(raw.Metrics.PromptTokenCount, raw.Metrics.GeneratedTokenCount, raw.Metrics.Elapsed); _diagnostics = new ModelDiagnostics("llama.cpp", null, _metadata?.ContextWindowTokens ?? _profile.ContextWindowTokens, _loadDuration, firstToken, Rate(raw.Metrics.PromptTokenCount, raw.Metrics.Elapsed), Rate(raw.Metrics.GeneratedTokenCount, raw.Metrics.Elapsed)); await queue.WriteAsync(ModelEvent.UsageSnapshot(_usage), CancellationToken.None).ConfigureAwait(false); break; case LocalBackendEventKind.Completed: if (!await WriteTextAsync(queue, stops.Push(Decode(decoder, ReadOnlyMemory<byte>.Empty, true)) + stops.Flush(), stops.Stopped).ConfigureAwait(false)) return; await queue.WriteAsync(ModelEvent.Completed(), CancellationToken.None).ConfigureAwait(false); queue.Complete(); return; case LocalBackendEventKind.Cancelled: queue.Complete(new OperationCanceledException()); return; case LocalBackendEventKind.Error: await queue.WriteAsync(ModelEvent.ErrorEvent(new ModelError(Map(raw.Error.Code), raw.Error.Message)), CancellationToken.None).ConfigureAwait(false); queue.Complete(); return; default: queue.Complete(new ModelOperationException(ModelErrorCode.BackendFailure, "Unknown local event.")); return; } } } }
                catch (Exception error) { queue.Complete(error is ModelOperationException || error is OperationCanceledException ? error : new ModelOperationException(ModelErrorCode.BackendFailure, error.Message)); }
            }
            private static async Task<bool> WriteTextAsync(EventQueue queue, string text, bool stopped) { if (text.Length > 0) await queue.WriteAsync(ModelEvent.Token(text), CancellationToken.None).ConfigureAwait(false); if (!stopped) return true; await queue.WriteAsync(ModelEvent.Completed(), CancellationToken.None).ConfigureAwait(false); queue.Complete(); return false; }
            private static string Decode(Decoder decoder, ReadOnlyMemory<byte> value, bool flush) { var bytes = value.ToArray(); var chars = new char[Math.Max(1, Encoding.UTF8.GetMaxCharCount(bytes.Length))]; decoder.Convert(bytes, 0, bytes.Length, chars, 0, chars.Length, flush, out _, out var used, out _); return new string(chars, 0, used); }
            private static double? Rate(long tokens, TimeSpan elapsed) { return elapsed.TotalSeconds <= 0 ? (double?)null : tokens / elapsed.TotalSeconds; }
            public async Task ResetAsync(CancellationToken ct) { await _lifetime.WaitAsync(ct).ConfigureAwait(false); try { ThrowIfDisposed(); var result = await _backend.ResetContextAsync(_context, ct).ConfigureAwait(false); Ensure(result); } finally { _lifetime.Release(); } }
            public Task<ModelUsage> GetUsageAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(_usage); }
            public Task<ModelDiagnostics> GetDiagnosticsAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(_diagnostics); }
            public async ValueTask DisposeAsync() { await _lifetime.WaitAsync().ConfigureAwait(false); try { if (_disposed) return; _disposed = true; await _backend.DestroyContextAsync(_context, CancellationToken.None).ConfigureAwait(false); await _backend.UnloadModelAsync(_model, CancellationToken.None).ConfigureAwait(false); await _backend.DestroyRuntimeAsync(_runtime, CancellationToken.None).ConfigureAwait(false); } finally { _lifetime.Release(); } }
            private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(Session)); }
            private string Serialize(IReadOnlyList<ModelMessage> messages) { if (!string.Equals(_profile.NamedTemplate, "Qwen3.5", StringComparison.Ordinal)) throw new ModelOperationException(ModelErrorCode.Unsupported, "Unsupported named template: " + _profile.NamedTemplate); var value = new StringBuilder(); foreach (var message in messages) value.Append("<|im_start|>").Append(message.Role.ToString().ToLowerInvariant()).Append('\n').Append(message.Text).Append("<|im_end|>\n"); return value.Append("<|im_start|>assistant\n").ToString(); }
        }
        private sealed class StopFilter { private readonly IReadOnlyList<string> _stops; private readonly int _hold; private string _buffer = string.Empty; public bool Stopped { get; private set; } public StopFilter(IReadOnlyList<string> stops) { _stops = stops; foreach (var value in stops) _hold = Math.Max(_hold, value.Length - 1); } public string Push(string value) { if (Stopped) return string.Empty; _buffer += value; var index = -1; foreach (var stop in _stops) { var current = _buffer.IndexOf(stop, StringComparison.Ordinal); if (current >= 0 && (index < 0 || current < index)) index = current; } if (index >= 0) { var output = _buffer.Substring(0, index); _buffer = string.Empty; Stopped = true; return output; } if (_buffer.Length <= _hold) return string.Empty; var count = _buffer.Length - _hold; var result = _buffer.Substring(0, count); _buffer = _buffer.Substring(count); return result; } public string Flush() { if (Stopped) return string.Empty; var result = _buffer; _buffer = string.Empty; return result; } }
        private sealed class EventQueue { private readonly ConcurrentQueue<ModelEvent> _items = new ConcurrentQueue<ModelEvent>(); private readonly SemaphoreSlim _available = new SemaphoreSlim(0); private readonly SemaphoreSlim _capacity; private Exception? _completion; private int _completed; public EventQueue(int capacity) { _capacity = new SemaphoreSlim(capacity); } public async Task WriteAsync(ModelEvent item, CancellationToken ct) { await _capacity.WaitAsync(ct).ConfigureAwait(false); if (Volatile.Read(ref _completed) != 0) { _capacity.Release(); return; } _items.Enqueue(item); _available.Release(); } public async Task<ModelEvent> ReadAsync(CancellationToken ct) { while (true) { if (_items.TryDequeue(out var item)) { _capacity.Release(); return item; } if (Volatile.Read(ref _completed) != 0) { if (_completion != null) throw _completion; throw new InvalidOperationException("Event stream completed without terminal event."); } await _available.WaitAsync(ct).ConfigureAwait(false); } } public void Complete(Exception? error = null) { _completion = error; Interlocked.Exchange(ref _completed, 1); _available.Release(); } }
    }
    public sealed class ModelOperationException : Exception { public ModelOperationException(ModelErrorCode code, string message) : base(message) { Code = code; } public ModelErrorCode Code { get; } }
}
