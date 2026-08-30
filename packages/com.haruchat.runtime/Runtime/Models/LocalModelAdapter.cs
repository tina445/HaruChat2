#nullable enable

using HaruChat.Runtime.LocalModels;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HaruChat.Runtime.Models
{
    public sealed class LocalModelAdapter : IModelAdapter
    {
        private readonly ILocalModelBackend _backend; private readonly ModelConfig _config; private readonly ModelProfile _profile;
        public LocalModelAdapter(string id, ILocalModelBackend backend, ModelConfig config, ModelProfile profile) { Id = id; _backend = backend ?? throw new ArgumentNullException(nameof(backend)); _config = config; _profile = profile; if (config.ProfileId != profile.Id) throw new ArgumentException("Configuration profile does not match profile."); }
        public string Id { get; } public ModelCapabilities Capabilities { get { return new ModelCapabilities(tools: false); } }
        public async Task<IModelSession> CreateSessionAsync(ModelSessionOptions options, CancellationToken cancellationToken)
        {
            var runtime = await _backend.CreateRuntimeAsync(new LocalRuntimeOptions(), cancellationToken).ConfigureAwait(false); Ensure(runtime);
            var model = await _backend.LoadModelAsync(runtime.Value, new LocalModelLoadOptions(_config.ModelPath), cancellationToken).ConfigureAwait(false); if (!model.IsSuccess) { await _backend.DestroyRuntimeAsync(runtime.Value, CancellationToken.None).ConfigureAwait(false); Ensure(model); }
            var context = await _backend.CreateContextAsync(model.Value, new LocalContextOptions(_profile.ResolveContextWindow(_config), options.BatchSize), cancellationToken).ConfigureAwait(false); if (!context.IsSuccess) { await _backend.UnloadModelAsync(model.Value, CancellationToken.None).ConfigureAwait(false); await _backend.DestroyRuntimeAsync(runtime.Value, CancellationToken.None).ConfigureAwait(false); Ensure(context); }
            return new Session(_backend, runtime.Value, model.Value, context.Value, _profile, _config);
        }
        private static void Ensure(LocalBackendResult result) { if (!result.IsSuccess) throw new InvalidOperationException(result.Error.Code + ": " + result.Error.Message); }
        private static void Ensure<T>(LocalBackendResult<T> result) { if (!result.IsSuccess) throw new InvalidOperationException(result.Error.Code + ": " + result.Error.Message); }
        private sealed class Session : IModelSession
        {
            private readonly ILocalModelBackend _backend; private readonly LocalRuntimeHandle _runtime; private readonly LocalModelHandle _model; private readonly LocalContextHandle _context; private readonly ModelProfile _profile; private readonly ModelConfig _config; private bool _busy; private bool _disposed; private ModelUsage _usage = new ModelUsage(null, null, null);
            public Session(ILocalModelBackend backend, LocalRuntimeHandle runtime, LocalModelHandle model, LocalContextHandle context, ModelProfile profile, ModelConfig config) { _backend = backend; _runtime = runtime; _model = model; _context = context; _profile = profile; _config = config; }
            public async IAsyncEnumerable<ModelEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(Session)); if (_busy) throw new InvalidOperationException("SessionBusy"); _busy = true; LocalGenerationHandle job = default; var terminal = false;
                try
                {
                    var prompt = Serialize(request.Messages); var generation = _profile.ResolveGeneration(_config); var started = await _backend.StartGenerationAsync(_context, new LocalGenerationOptions(prompt, generation.MaximumOutputTokens, generation.Temperature, generation.TopK, generation.TopP, (uint)(generation.Seed ?? 0)), cancellationToken).ConfigureAwait(false); Ensure(started); job = started.Value;
                    while (!terminal)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); var batch = await _backend.PollEventsAsync(job, 32, cancellationToken).ConfigureAwait(false); Ensure(batch);
                        if (batch.Value.Events.Count == 0) { await Task.Delay(8, cancellationToken).ConfigureAwait(false); continue; }
                        foreach (var item in batch.Value.Events)
                        {
                            if (item.Kind == LocalBackendEventKind.Token) yield return ModelEvent.Token(Decode(item.Payload));
                            else if (item.Kind == LocalBackendEventKind.Metrics) { _usage = new ModelUsage(item.Metrics.PromptTokenCount, item.Metrics.GeneratedTokenCount, item.Metrics.Elapsed); yield return ModelEvent.UsageSnapshot(_usage); }
                            else if (item.Kind == LocalBackendEventKind.Completed) { terminal = true; yield return ModelEvent.Completed(); }
                            else if (item.Kind == LocalBackendEventKind.Error) { terminal = true; yield return ModelEvent.ErrorEvent(new ModelError(ModelErrorCode.BackendFailure, Decode(item.Payload))); }
                            else if (item.Kind == LocalBackendEventKind.Cancelled) { terminal = true; throw new OperationCanceledException(cancellationToken); }
                        }
                    }
                }
                finally { if (job.IsValid) { if (!terminal) await _backend.CancelGenerationAsync(job, CancellationToken.None).ConfigureAwait(false); await _backend.DestroyGenerationAsync(job, CancellationToken.None).ConfigureAwait(false); } _busy = false; }
            }
            public Task ResetAsync(CancellationToken cancellationToken) { if (_busy) throw new InvalidOperationException("SessionBusy"); return ResetCore(cancellationToken); }
            private async Task ResetCore(CancellationToken ct) { var result = await _backend.ResetContextAsync(_context, ct).ConfigureAwait(false); Ensure(result); }
            public Task<ModelUsage> GetUsageAsync(CancellationToken cancellationToken) { return Task.FromResult(_usage); }
            public async ValueTask DisposeAsync() { if (_disposed) return; _disposed = true; await _backend.DestroyContextAsync(_context, CancellationToken.None).ConfigureAwait(false); await _backend.UnloadModelAsync(_model, CancellationToken.None).ConfigureAwait(false); await _backend.DestroyRuntimeAsync(_runtime, CancellationToken.None).ConfigureAwait(false); }
            private string Serialize(IReadOnlyList<ModelMessage> messages)
            {
                var builder = new StringBuilder();
                if (_profile.NamedTemplate != "Qwen3.5") throw new InvalidOperationException("Unsupported named template: " + _profile.NamedTemplate);
                foreach (var message in messages) builder.Append("<|im_start|>").Append(message.Role.ToString().ToLowerInvariant()).Append('\n').Append(message.Text).Append("<|im_end|>\n");
                return builder.Append("<|im_start|>assistant\n").ToString();
            }
            private static string Decode(ReadOnlyMemory<byte> bytes) { return new UTF8Encoding(false, true).GetString(bytes.ToArray()); }
            private static void Ensure(LocalBackendResult result) { if (!result.IsSuccess) throw new InvalidOperationException(result.Error.Code + ": " + result.Error.Message); }
            private static void Ensure<T>(LocalBackendResult<T> result) { if (!result.IsSuccess) throw new InvalidOperationException(result.Error.Code + ": " + result.Error.Message); }
        }
    }
}
