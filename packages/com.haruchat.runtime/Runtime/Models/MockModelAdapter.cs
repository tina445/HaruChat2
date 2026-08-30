#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace HaruChat.Runtime.Models
{
    public sealed class MockModelAdapter : IModelAdapter
    {
        private readonly Func<ModelRequest, IEnumerable<ModelEvent>> _events;
        public MockModelAdapter(string id, Func<ModelRequest, IEnumerable<ModelEvent>>? events = null) { Id = id; _events = events ?? (_ => new[] { ModelEvent.Token("mock response"), ModelEvent.Completed() }); }
        public string Id { get; } public ModelCapabilities Capabilities { get { return new ModelCapabilities(); } }
        public Task<IModelSession> CreateSessionAsync(ModelSessionOptions options, CancellationToken cancellationToken) { return Task.FromResult<IModelSession>(new Session(_events)); }
        private sealed class Session : IModelSession
        {
            private readonly Func<ModelRequest, IEnumerable<ModelEvent>> _events; private bool _busy; private ModelUsage _usage = new ModelUsage(null, null, null);
            public Session(Func<ModelRequest, IEnumerable<ModelEvent>> events) { _events = events; }
            public async IAsyncEnumerable<ModelEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                if (_busy) throw new InvalidOperationException("SessionBusy"); _busy = true;
                try { foreach (var item in _events(request)) { cancellationToken.ThrowIfCancellationRequested(); await Task.Yield(); yield return item; if (item.IsTerminal) break; } }
                finally { _busy = false; }
            }
            public Task ResetAsync(CancellationToken cancellationToken) { return Task.CompletedTask; }
            public Task<ModelUsage> GetUsageAsync(CancellationToken cancellationToken) { return Task.FromResult(_usage); }
            public ValueTask DisposeAsync() { return default; }
        }
    }
}
