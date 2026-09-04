#nullable enable

using HaruChat.Runtime.Models;
using HaruChat.Runtime.Memory;
using HaruChat.Runtime.Agent;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace HaruChat.Runtime.Characters
{
    public sealed class CharacterChatService : IAsyncDisposable
    {
        private CharacterDefinition _character; private readonly Conversation _conversation; private IModelSession _session; private readonly PromptCompiler _compiler; private int _contextBudget; private readonly SemaphoreSlim _operationLock = new SemaphoreSlim(1, 1); private readonly object _stateGate = new object(); private CancellationTokenSource? _activeSend;
        private readonly IMemoryStore? _memoryStore; private readonly IMemoryRetriever? _memoryRetriever; private readonly MemoryPersistenceOptions _memoryOptions; private readonly IMemoryCandidateFactory _memoryCandidates; private readonly bool _usesAutomaticMemoryCandidates; private readonly AgentRuntime? _agent; private readonly ModelCapabilities? _modelCapabilities; private readonly int _maximumOutputTokens; private readonly ConversationCompactionPolicy _compactionPolicy; private IConversationCompressor? _compressor; private string _memorySessionId; private DateTimeOffset _memorySessionCreated; private int _lastEstimatedPromptTokens; private int _lastActualPromptTokens; private int _lastExcludedTurns; private int _lastSummaryTokens; private int _lastCompactedTurns;
        public CharacterChatService(CharacterDefinition character, Conversation conversation, IModelSession session, PromptCompiler compiler, int contextBudget, IMemoryStore? memoryStore = null, IMemoryRetriever? memoryRetriever = null, MemoryPersistenceOptions? memoryOptions = null, IMemoryCandidateFactory? memoryCandidates = null, AgentRuntime? agent = null, ModelCapabilities? modelCapabilities = null, int maximumOutputTokens = 0, IConversationCompressor? compressor = null, ConversationCompactionPolicy? compactionPolicy = null)
        { if (maximumOutputTokens < 0 || maximumOutputTokens >= contextBudget) throw new ArgumentOutOfRangeException(nameof(maximumOutputTokens)); _character = character; _conversation = conversation; _session = session; _compiler = compiler; _contextBudget = contextBudget; _memoryStore = memoryStore; _memoryRetriever = memoryRetriever; _memoryOptions = memoryOptions ?? new MemoryPersistenceOptions(); _usesAutomaticMemoryCandidates = memoryCandidates == null; _memoryCandidates = memoryCandidates ?? (_memoryOptions.AutomaticallySaveImportantMemories ? (IMemoryCandidateFactory)new RuleBasedMemoryCandidateFactory() : new NoMemoryCandidateFactory()); _agent = agent; _modelCapabilities = modelCapabilities; _maximumOutputTokens = maximumOutputTokens; _compactionPolicy = compactionPolicy ?? new ConversationCompactionPolicy(); _compressor = compressor ?? (session is ITokenCountingModelSession ? new ModelConversationCompressor(session) : null); _memorySessionId = Guid.NewGuid().ToString("N"); _memorySessionCreated = DateTimeOffset.UtcNow; }
        public async IAsyncEnumerable<ModelEvent> SendAsync(string input, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await _operationLock.WaitAsync(cancellationToken); var response = new System.Text.StringBuilder(); var completed = false; using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_stateGate) _activeSend = linked;
            try
            {
                IReadOnlyList<MemoryItem> memories = Array.Empty<MemoryItem>();
                if (_memoryOptions.Enabled && _memoryRetriever != null)
                {
                    try
                    {
                        var recalled = new List<MemoryItem>();
                        if (_compiler.MemoryPolicy.MaximumSessionSummaries > 0)
                        {
                            var sessions = await _memoryRetriever.GetRecentSessionsAsync(_character.Id, _compiler.MemoryPolicy.MaximumSessionSummaries + 1, DateTimeOffset.UtcNow, linked.Token).ConfigureAwait(false);
                            foreach (var session in sessions)
                            {
                                if (session.SessionId == _memorySessionId || string.IsNullOrWhiteSpace(session.SummaryText)) continue;
                                recalled.Add(new MemoryItem("session-summary-" + session.SessionId, _character.Id, session.SummaryText, 0, session.CreatedAt, session.UpdatedAt, session.SessionId, session.ExpiresAt));
                            }
                        }
                        var remainingItems = _compiler.MemoryPolicy.MaximumItems - recalled.Count;
                        if (remainingItems > 0) recalled.AddRange(await _memoryRetriever.SearchAsync(new MemoryQuery(_character.Id, input, remainingItems), linked.Token).ConfigureAwait(false));
                        memories = recalled;
                    }
                    catch (MemoryOperationException) { /* Memory is an optional recovery boundary. */ }
                }
                _conversation.BeginUserTurn(input);
                var promptBudget = _contextBudget - _maximumOutputTokens;
                var plan = _compiler.CompilePlan(_character, _conversation, input, promptBudget, memories: memories);
                var tokenCount = await CountPromptTokensAsync(plan, linked.Token).ConfigureAwait(false);
                if (_compactionPolicy.ShouldCompact(tokenCount.Tokens, promptBudget))
                {
                    var eligibleTurns = _conversation.Archived.Count / 2 - _compactionPolicy.RetainedCompletedTurns;
                    if (eligibleTurns > _conversation.CompactedCompletedTurns && _compressor != null)
                    {
                        try
                        {
                            var result = await _compressor.CompressAsync(_conversation.GetArchivePrefix(eligibleTurns), eligibleTurns, linked.Token).ConfigureAwait(false);
                            _conversation.ApplyCompaction(result.StructuredSummary, result.ArchivedCompletedTurns);
                            plan = _compiler.CompilePlan(_character, _conversation, input, promptBudget, memories: memories);
                            tokenCount = await CountPromptTokensAsync(plan, linked.Token).ConfigureAwait(false);
                        }
                        catch (ConversationCompressionException) { /* Compiler's deterministic eviction remains the safe fallback. */ }
                    }
                }
                _lastEstimatedPromptTokens = plan.EstimatedPromptTokens; _lastActualPromptTokens = tokenCount.Tokens; _lastExcludedTurns = plan.ExcludedCompletedTurns; _lastSummaryTokens = plan.SummaryEstimatedTokens; _lastCompactedTurns = plan.CompactedCompletedTurns;
                if (tokenCount.Tokens > promptBudget) { yield return ModelEvent.ErrorEvent(new ModelError(ModelErrorCode.ContextBudgetExceeded, "The required prompt exceeds the available context budget.")); yield break; }
                var stream = _agent == null ? _session.GenerateAsync(plan.Request, linked.Token) : _agent.GenerateAsync(_session, _modelCapabilities ?? new ModelCapabilities(), plan.Request, new ToolExecutionContext(_character.Id, _memorySessionId), linked.Token);
                await foreach (var item in stream)
                {
                    if (item.Kind == ModelEventKind.Token) response.Append(item.Text);
                    if (item.Kind == ModelEventKind.Error) { yield return item; break; }
                    if (item.Kind == ModelEventKind.Completed) completed = true;
                    yield return item;
                    if (item.IsTerminal) break;
                }
                if (completed) { _conversation.CommitAssistant(response.ToString()); await PersistCompletedTurnAsync(input, response.ToString(), linked.Token).ConfigureAwait(false); } else _conversation.RollbackPending();
            }
            finally { if (!completed) _conversation.RollbackPending(); lock (_stateGate) { if (ReferenceEquals(_activeSend, linked)) _activeSend = null; } _operationLock.Release(); }
        }
        public async Task NewConversationAsync(CancellationToken cancellationToken)
        {
            CancellationTokenSource? active; lock (_stateGate) active = _activeSend; active?.Cancel();
            await _operationLock.WaitAsync(cancellationToken);
            try { await _session.ResetAsync(cancellationToken); _conversation.Reset(); BeginMemorySession(); }
            finally { _operationLock.Release(); }
        }
        public async Task<ContextWindowStatus> GetContextWindowStatusAsync(CancellationToken cancellationToken)
        {
            var usage = await _session.GetUsageAsync(cancellationToken).ConfigureAwait(false);
            return new ContextWindowStatus(_contextBudget, _maximumOutputTokens, _lastEstimatedPromptTokens, usage.PromptTokens, usage.GeneratedTokens, _lastExcludedTurns, _lastSummaryTokens, _lastCompactedTurns, _lastActualPromptTokens);
        }
        /// <summary>
        /// Replaces the foreground model/profile or character session. The caller creates the new
        /// provider-neutral session; this service only serializes the hand-off and owns its disposal.
        /// A switch always discards pending/history context so no response can mix character or model state.
        /// </summary>
        public async Task ReplaceSessionAsync(CharacterDefinition character, IModelSession session, int contextBudget, CancellationToken cancellationToken)
        {
            if (character == null) throw new ArgumentNullException(nameof(character));
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (contextBudget <= 0) throw new ArgumentOutOfRangeException(nameof(contextBudget));
            CancellationTokenSource? active; lock (_stateGate) active = _activeSend; active?.Cancel();
            await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var previous = _session;
                try { await previous.DisposeAsync().ConfigureAwait(false); }
                catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
                _character = character; _session = session; _contextBudget = contextBudget; _compressor = session is ITokenCountingModelSession ? new ModelConversationCompressor(session) : null; _conversation.Reset(); BeginMemorySession();
            }
            finally { _operationLock.Release(); }
        }
        public async ValueTask DisposeAsync()
        {
            CancellationTokenSource? active; lock (_stateGate) active = _activeSend; active?.Cancel();
            await _operationLock.WaitAsync();
            try { await _session.DisposeAsync(); }
            finally { _operationLock.Release(); _operationLock.Dispose(); }
        }
        private async Task PersistCompletedTurnAsync(string input, string response, CancellationToken cancellationToken)
        {
            if (!_memoryOptions.Enabled || _memoryStore == null) return;
            var now = DateTimeOffset.UtcNow; var expires = _memoryOptions.Retention.HasValue ? now + _memoryOptions.Retention.Value : (DateTimeOffset?)null;
            try
            {
                var summary = _conversation.CompressedSummary;
                if (!string.IsNullOrWhiteSpace(summary) && !MemoryPrivacy.IsSensitive(summary)) await _memoryStore.UpsertSessionAsync(new MemorySession(_memorySessionId, _character.Id, Trim(summary, 8192), _memorySessionCreated, now, expires), cancellationToken).ConfigureAwait(false);
                var candidate = _memoryCandidates.Create(_character.Id, _memorySessionId, input, response, now, expires);
                if (candidate != null && (!_usesAutomaticMemoryCandidates || candidate.Importance >= _memoryOptions.AutomaticMemoryImportanceThreshold)) await _memoryStore.SaveMemoryAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (MemoryOperationException) { /* Completion of the canonical conversation must survive memory failure. */ }
        }
        private void BeginMemorySession() { _memorySessionId = Guid.NewGuid().ToString("N"); _memorySessionCreated = DateTimeOffset.UtcNow; }
        private async Task<ModelTokenCount> CountPromptTokensAsync(PromptPlan plan, CancellationToken cancellationToken)
        {
            if (_session is ITokenCountingModelSession tokenizer) return await tokenizer.CountTokensAsync(plan.Request, cancellationToken).ConfigureAwait(false);
            return new ModelTokenCount(plan.EstimatedPromptTokens, false);
        }
        private static string Trim(string value, int maximumLength) { return value.Length <= maximumLength ? value : value.Substring(0, maximumLength); }
    }
}
