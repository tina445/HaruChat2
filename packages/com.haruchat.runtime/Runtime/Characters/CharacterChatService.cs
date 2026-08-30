#nullable enable

using HaruChat.Runtime.Models;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace HaruChat.Runtime.Characters
{
    public sealed class CharacterChatService : IAsyncDisposable
    {
        private readonly CharacterDefinition _character; private readonly Conversation _conversation; private readonly IModelSession _session; private readonly PromptCompiler _compiler; private readonly int _contextBudget; private readonly SemaphoreSlim _operationLock = new SemaphoreSlim(1, 1); private readonly object _stateGate = new object(); private CancellationTokenSource? _activeSend;
        public CharacterChatService(CharacterDefinition character, Conversation conversation, IModelSession session, PromptCompiler compiler, int contextBudget)
        { _character = character; _conversation = conversation; _session = session; _compiler = compiler; _contextBudget = contextBudget; }
        public async IAsyncEnumerable<ModelEvent> SendAsync(string input, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await _operationLock.WaitAsync(cancellationToken); var response = new System.Text.StringBuilder(); var completed = false; using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_stateGate) _activeSend = linked;
            try
            {
                _conversation.BeginUserTurn(input); var request = _compiler.Compile(_character, _conversation, input, _contextBudget);
                await foreach (var item in _session.GenerateAsync(request, linked.Token))
                {
                    if (item.Kind == ModelEventKind.Token) response.Append(item.Text);
                    if (item.Kind == ModelEventKind.Error) { yield return item; break; }
                    if (item.Kind == ModelEventKind.Completed) completed = true;
                    yield return item;
                    if (item.IsTerminal) break;
                }
                if (completed) _conversation.CommitAssistant(response.ToString()); else _conversation.RollbackPending();
            }
            finally { if (!completed) _conversation.RollbackPending(); lock (_stateGate) { if (ReferenceEquals(_activeSend, linked)) _activeSend = null; } _operationLock.Release(); }
        }
        public async Task NewConversationAsync(CancellationToken cancellationToken)
        {
            CancellationTokenSource? active; lock (_stateGate) active = _activeSend; active?.Cancel();
            await _operationLock.WaitAsync(cancellationToken);
            try { await _session.ResetAsync(cancellationToken); _conversation.Reset(); }
            finally { _operationLock.Release(); }
        }
        public async ValueTask DisposeAsync()
        {
            CancellationTokenSource? active; lock (_stateGate) active = _activeSend; active?.Cancel();
            await _operationLock.WaitAsync();
            try { await _session.DisposeAsync(); }
            finally { _operationLock.Release(); _operationLock.Dispose(); }
        }
    }
}
