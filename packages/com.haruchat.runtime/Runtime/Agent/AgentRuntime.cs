#nullable enable

using HaruChat.Runtime.Models;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace HaruChat.Runtime.Agent
{
    public sealed class AgentRuntimeOptions
    {
        public AgentRuntimeOptions(int maximumIterations = 4, int maximumToolResultCharacters = 8192)
        {
            if (maximumIterations < 1 || maximumIterations > 16) throw new ArgumentOutOfRangeException(nameof(maximumIterations));
            if (maximumToolResultCharacters < 128) throw new ArgumentOutOfRangeException(nameof(maximumToolResultCharacters));
            MaximumIterations = maximumIterations; MaximumToolResultCharacters = maximumToolResultCharacters;
        }
        public int MaximumIterations { get; } public int MaximumToolResultCharacters { get; }
    }

    /// <summary>
    /// A provider-neutral, bounded tool loop. It never grants authority itself: write tools require
    /// an approval port and all tools remain subject to an authorization port.
    /// </summary>
    public sealed class AgentRuntime
    {
        private readonly ToolRegistry _tools; private readonly IToolArgumentValidator _validator; private readonly IToolAuthorization _authorization; private readonly IToolApproval _approval; private readonly AgentRuntimeOptions _options;
        public AgentRuntime(ToolRegistry tools, IToolArgumentValidator? validator = null, IToolAuthorization? authorization = null, IToolApproval? approval = null, AgentRuntimeOptions? options = null)
        { _tools = tools ?? throw new ArgumentNullException(nameof(tools)); _validator = validator ?? new BasicJsonObjectToolArgumentValidator(); _authorization = authorization ?? new PermissiveToolAuthorization(); _approval = approval ?? new DenyToolApproval(); _options = options ?? new AgentRuntimeOptions(); }

        public async IAsyncEnumerable<ModelEvent> GenerateAsync(IModelSession session, ModelCapabilities capabilities, ModelRequest initialRequest, ToolExecutionContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (capabilities == null) throw new ArgumentNullException(nameof(capabilities));
            if (initialRequest == null) throw new ArgumentNullException(nameof(initialRequest));
            if (context == null) throw new ArgumentNullException(nameof(context));
            var toolDefinitions = initialRequest.Tools.Count == 0 ? _tools.ToModelDefinitions() : initialRequest.Tools;
            if (toolDefinitions.Count > 0 && !capabilities.Tools)
            {
                yield return ModelEvent.ErrorEvent(new ModelError(ModelErrorCode.Unsupported, "The selected model does not support tools."));
                yield break;
            }

            var messages = new List<ModelMessage>(initialRequest.Messages);
            for (var iteration = 0; iteration < _options.MaximumIterations; iteration++)
            {
                var request = new ModelRequest(messages, initialRequest.Generation, initialRequest.CorrelationId, toolDefinitions);
                var calls = new List<ToolCall>(); var completedForTools = false; var modelFailed = false;
                await foreach (var item in session.GenerateAsync(request, cancellationToken))
                {
                    if (item.Kind == ModelEventKind.ToolCall && item.ToolCall != null) calls.Add(ToolCall.FromModel(item.ToolCall));
                    if (item.Kind == ModelEventKind.Completed && item.StopReason == ModelStopReason.ToolCall) completedForTools = true;
                    if (item.Kind == ModelEventKind.Error) modelFailed = true;
                    yield return item;
                    if (item.IsTerminal) break;
                }
                if (modelFailed) yield break;
                if (calls.Count == 0)
                {
                    if (completedForTools)
                        yield return ModelEvent.ErrorEvent(new ModelError(ModelErrorCode.InvalidRequest, "The model ended for a tool call without providing one."));
                    yield break;
                }
                if (iteration + 1 >= _options.MaximumIterations)
                {
                    foreach (var call in calls) yield return ModelEvent.ToolResultReceived(ToolResult.Failure(call, ToolFailureCode.IterationLimit, "The maximum tool-call iteration limit was reached.").ToModelResult());
                    yield return ModelEvent.ErrorEvent(new ModelError(ModelErrorCode.ContextBudgetExceeded, "The maximum tool-call iteration limit was reached."));
                    yield break;
                }
                var callIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var call in calls)
                {
                    ToolResult result;
                    if (!callIds.Add(call.CallId)) result = ToolResult.Failure(call, ToolFailureCode.DuplicateCall, "Duplicate tool call ID.");
                    else result = await ExecuteAsync(call, context, cancellationToken).ConfigureAwait(false);
                    if (result.Content.Length > _options.MaximumToolResultCharacters)
                        result = ToolResult.Failure(call, ToolFailureCode.ResultTooLarge, "The tool result exceeded the configured size limit.");
                    yield return ModelEvent.ToolResultReceived(result.ToModelResult());
                    messages.Add(new ModelMessage(ModelRole.Tool, SerializeForModel(result)));
                }
            }
        }

        private async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            if (!_tools.TryResolve(call.Name, out var tool)) return ToolResult.Failure(call, ToolFailureCode.UnknownTool, "This tool is not in the allowlist.");
            if (!_validator.TryValidate(tool.Definition, call, out var validationError)) return ToolResult.Failure(call, ToolFailureCode.InvalidArguments, validationError);
            if (!await _authorization.IsAuthorizedAsync(tool.Definition, call, context, cancellationToken).ConfigureAwait(false)) return ToolResult.Failure(call, ToolFailureCode.Unauthorized, "This tool call is not authorized.");
            if (tool.Definition.Access == ToolAccess.Write && !await _approval.RequestApprovalAsync(tool.Definition, call, context, cancellationToken).ConfigureAwait(false)) return ToolResult.Failure(call, ToolFailureCode.ApprovalDenied, "The user did not approve this write operation.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(tool.Definition.Timeout);
            try { return await tool.ExecuteAsync(call, context, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return ToolResult.Failure(call, ToolFailureCode.Cancelled, "The tool call was cancelled."); }
            catch (OperationCanceledException) { return ToolResult.Failure(call, ToolFailureCode.TimedOut, "The tool call timed out."); }
            catch (Exception) { return ToolResult.Failure(call, ToolFailureCode.ExecutionFailed, "The tool could not complete."); }
        }

        private static string SerializeForModel(ToolResult result)
        {
            return "{\"call_id\":\"" + Escape(result.CallId) + "\",\"name\":\"" + Escape(result.Name) + "\",\"ok\":" + (result.Succeeded ? "true" : "false") + ",\"error_code\":" + (result.FailureCode.HasValue ? "\"" + result.FailureCode.Value + "\"" : "null") + ",\"content\":\"" + Escape(result.Content) + "\"}";
        }
        private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    }
}
