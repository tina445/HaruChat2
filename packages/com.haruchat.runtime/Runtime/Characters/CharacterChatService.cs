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
        private readonly CharacterDefinition _character; private readonly Conversation _conversation; private readonly IModelSession _session; private readonly PromptCompiler _compiler; private readonly int _contextBudget; private bool _sending;
        public CharacterChatService(CharacterDefinition character, Conversation conversation, IModelSession session, PromptCompiler compiler, int contextBudget)
        { _character = character; _conversation = conversation; _session = session; _compiler = compiler; _contextBudget = contextBudget; }
        public async IAsyncEnumerable<ModelEvent> SendAsync(string input, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (_sending) throw new InvalidOperationException("SessionBusy"); _sending = true; var response = new System.Text.StringBuilder(); var completed = false;
            try
            {
                _conversation.BeginUserTurn(input); var request = _compiler.Compile(_character, _conversation, input, _contextBudget);
                await foreach (var item in _session.GenerateAsync(request, cancellationToken)) { if (item.Kind == ModelEventKind.Token) response.Append(item.Text); if (item.Kind == ModelEventKind.Completed) completed = true; yield return item; }
                if (completed) _conversation.CommitAssistant(response.ToString()); else _conversation.RollbackPending();
            }
            finally { if (!completed) _conversation.RollbackPending(); _sending = false; }
        }
        public async Task NewConversationAsync(CancellationToken cancellationToken) { _conversation.Reset(); await _session.ResetAsync(cancellationToken); }
        public ValueTask DisposeAsync() { return _session.DisposeAsync(); }
    }
}
