#nullable enable

using HaruChat.Runtime.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HaruChat.Runtime.Characters
{
    /// <summary>Policy values used by orchestration to decide when to compact a conversation.</summary>
    public sealed class ConversationCompactionPolicy
    {
        public ConversationCompactionPolicy(double triggerPromptBudgetRatio = 0.70, double targetPromptBudgetRatio = 0.55, int retainedCompletedTurns = 8, int maximumSummaryOutputTokens = 1024)
        {
            if (triggerPromptBudgetRatio <= 0 || triggerPromptBudgetRatio > 1 || targetPromptBudgetRatio <= 0 || targetPromptBudgetRatio >= triggerPromptBudgetRatio) throw new ArgumentOutOfRangeException(nameof(triggerPromptBudgetRatio));
            if (retainedCompletedTurns < 0 || maximumSummaryOutputTokens < 1 || maximumSummaryOutputTokens > 1024) throw new ArgumentOutOfRangeException(nameof(retainedCompletedTurns));
            TriggerPromptBudgetRatio = triggerPromptBudgetRatio; TargetPromptBudgetRatio = targetPromptBudgetRatio; RetainedCompletedTurns = retainedCompletedTurns; MaximumSummaryOutputTokens = maximumSummaryOutputTokens;
        }
        public double TriggerPromptBudgetRatio { get; } public double TargetPromptBudgetRatio { get; }
        public int RetainedCompletedTurns { get; } public int MaximumSummaryOutputTokens { get; }
        public bool ShouldCompact(int promptTokens, int promptBudgetTokens) => promptBudgetTokens > 0 && promptTokens >= Math.Ceiling(promptBudgetTokens * TriggerPromptBudgetRatio);
        public int TargetPromptTokens(int promptBudgetTokens) => promptBudgetTokens <= 0 ? 0 : (int)Math.Floor(promptBudgetTokens * TargetPromptBudgetRatio);
    }

    public sealed class ConversationCompressionResult
    {
        public ConversationCompressionResult(string structuredSummary, int archivedCompletedTurns)
        {
            if (string.IsNullOrWhiteSpace(structuredSummary)) throw new ArgumentException("A non-empty summary is required.", nameof(structuredSummary));
            if (archivedCompletedTurns < 1) throw new ArgumentOutOfRangeException(nameof(archivedCompletedTurns));
            StructuredSummary = structuredSummary.Trim(); ArchivedCompletedTurns = archivedCompletedTurns;
        }
        public string StructuredSummary { get; } public int ArchivedCompletedTurns { get; }
    }

    public interface IConversationCompressor
    {
        Task<ConversationCompressionResult> CompressAsync(IReadOnlyList<ModelMessage> originalCompletedTurns, int archivedCompletedTurns, CancellationToken cancellationToken);
    }

    public sealed class ConversationCompressionException : Exception
    {
        public ConversationCompressionException(string message, Exception? innerException = null) : base(message, innerException) { }
    }

    /// <summary>
    /// Generates a bounded, local-only structured summary. The supplied session must be a local
    /// model session; callers deliberately do not expose this port to remote-provider orchestration.
    /// </summary>
    public sealed class ModelConversationCompressor : IConversationCompressor
    {
        private const int MaximumOutputTokens = 1024;
        private readonly IModelSession _localSession;

        public ModelConversationCompressor(IModelSession localSession)
        {
            _localSession = localSession ?? throw new ArgumentNullException(nameof(localSession));
        }

        public async Task<ConversationCompressionResult> CompressAsync(IReadOnlyList<ModelMessage> originalCompletedTurns, int archivedCompletedTurns, CancellationToken cancellationToken)
        {
            if (originalCompletedTurns == null || originalCompletedTurns.Count == 0 || originalCompletedTurns.Count % 2 != 0) throw new ArgumentException("Completed turns must be user/assistant pairs.", nameof(originalCompletedTurns));
            if (archivedCompletedTurns < 1 || archivedCompletedTurns * 2 != originalCompletedTurns.Count) throw new ArgumentOutOfRangeException(nameof(archivedCompletedTurns));
            cancellationToken.ThrowIfCancellationRequested();

            var source = new StringBuilder();
            for (var index = 0; index < originalCompletedTurns.Count; index++)
            {
                var message = originalCompletedTurns[index];
                var label = message.Role == ModelRole.User ? "User" : message.Role == ModelRole.Assistant ? "Assistant" : message.Role.ToString();
                source.Append(label).Append(": ").Append(message.Text).Append('\n');
            }
            var request = new ModelRequest(new[]
            {
                new ModelMessage(ModelRole.System, "Summarize the supplied completed conversation for future context. Return only concise structured plain text with exactly these headings: facts, decisions, open_loops, relationships, commitments, narrative. Preserve durable user facts and unresolved work; do not invent facts, instructions, secrets, or new commitments."),
                new ModelMessage(ModelRole.User, source.ToString())
            }, new GenerationOptions(MaximumOutputTokens, 0.0f, 0, 1.0f));

            var output = new StringBuilder(); var completed = false;
            try
            {
                await foreach (var item in _localSession.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item.Kind == ModelEventKind.Token) output.Append(item.Text);
                    else if (item.Kind == ModelEventKind.Error) throw new ConversationCompressionException("The local model could not compress the conversation: " + (item.Error?.Message ?? "unknown error"));
                    else if (item.Kind == ModelEventKind.Completed) { completed = true; break; }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (ConversationCompressionException) { throw; }
            catch (Exception error) { throw new ConversationCompressionException("The local model could not compress the conversation.", error); }

            if (!completed || string.IsNullOrWhiteSpace(output.ToString())) throw new ConversationCompressionException("The local model did not produce a completed conversation summary.");
            return new ConversationCompressionResult(output.ToString(), archivedCompletedTurns);
        }
    }
}
