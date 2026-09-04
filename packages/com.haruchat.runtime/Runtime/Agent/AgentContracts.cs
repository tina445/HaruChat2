#nullable enable

using HaruChat.Runtime.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HaruChat.Runtime.Agent
{
    public enum ToolAccess { ReadOnly, Write }
    public enum ToolFailureCode { UnknownTool, DuplicateCall, InvalidArguments, Unauthorized, ApprovalDenied, TimedOut, Cancelled, ResultTooLarge, ExecutionFailed, IterationLimit }

    public sealed class ToolDefinition
    {
        public ToolDefinition(string name, string description, string argumentSchemaJson, ToolAccess access = ToolAccess.ReadOnly, TimeSpan? timeout = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A tool name is required.", nameof(name));
            if (timeout.HasValue && timeout.Value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            Name = name; Description = description ?? string.Empty; ArgumentSchemaJson = argumentSchemaJson ?? "{}"; Access = access; Timeout = timeout ?? TimeSpan.FromSeconds(10);
        }
        public string Name { get; } public string Description { get; } public string ArgumentSchemaJson { get; } public ToolAccess Access { get; } public TimeSpan Timeout { get; }
        public ModelToolDefinition ToModelDefinition() => new ModelToolDefinition(Name, Description, ArgumentSchemaJson);
    }

    public sealed class ToolCall
    {
        public ToolCall(string callId, string name, string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(callId) || string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A tool call ID and name are required.");
            CallId = callId; Name = name; ArgumentsJson = argumentsJson ?? "{}";
        }
        public string CallId { get; } public string Name { get; } public string ArgumentsJson { get; }
        public static ToolCall FromModel(ModelToolCall call) { if (call == null) throw new ArgumentNullException(nameof(call)); return new ToolCall(call.CallId, call.Name, call.ArgumentsJson); }
    }

    public sealed class ToolResult
    {
        public ToolResult(string callId, string name, bool succeeded, string content, ToolFailureCode? failureCode = null)
        { CallId = callId ?? throw new ArgumentNullException(nameof(callId)); Name = name ?? throw new ArgumentNullException(nameof(name)); Succeeded = succeeded; Content = content ?? string.Empty; FailureCode = failureCode; }
        public string CallId { get; } public string Name { get; } public bool Succeeded { get; } public string Content { get; } public ToolFailureCode? FailureCode { get; }
        public ModelToolResult ToModelResult() => new ModelToolResult(CallId, Name, Succeeded, Content, FailureCode.HasValue ? FailureCode.Value.ToString() : null);
        public static ToolResult Failure(ToolCall call, ToolFailureCode code, string message) => new ToolResult(call.CallId, call.Name, false, message, code);
    }

    public sealed class ToolExecutionContext
    {
        public ToolExecutionContext(string characterId, string? sessionId = null, object? state = null)
        { if (string.IsNullOrWhiteSpace(characterId)) throw new ArgumentException("A character ID is required.", nameof(characterId)); CharacterId = characterId; SessionId = sessionId; State = state; }
        public string CharacterId { get; } public string? SessionId { get; } public object? State { get; }
    }

    public interface ITool
    {
        ToolDefinition Definition { get; }
        Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken);
    }

    public interface IToolArgumentValidator { bool TryValidate(ToolDefinition definition, ToolCall call, out string error); }
    public interface IToolAuthorization { Task<bool> IsAuthorizedAsync(ToolDefinition definition, ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken); }
    public interface IToolApproval { Task<bool> RequestApprovalAsync(ToolDefinition definition, ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken); }

    public sealed class PermissiveToolAuthorization : IToolAuthorization
    { public Task<bool> IsAuthorizedAsync(ToolDefinition definition, ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken) => Task.FromResult(true); }

    /// <summary>Write tools are denied unless an application supplies an approval UI port.</summary>
    public sealed class DenyToolApproval : IToolApproval
    { public Task<bool> RequestApprovalAsync(ToolDefinition definition, ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken) => Task.FromResult(false); }

    public sealed class BasicJsonObjectToolArgumentValidator : IToolArgumentValidator
    {
        public bool TryValidate(ToolDefinition definition, ToolCall call, out string error)
        {
            var json = call.ArgumentsJson.Trim();
            if (!IsJsonObject(json)) { error = "Tool arguments must be a JSON object."; return false; }
            // This intentionally remains a conservative boundary check. Each tool parses its own typed fields.
            var schema = definition.ArgumentSchemaJson;
            var requiredAt = schema.IndexOf("\"required\"", StringComparison.Ordinal);
            if (requiredAt >= 0)
            {
                var start = schema.IndexOf('[', requiredAt); var end = start < 0 ? -1 : schema.IndexOf(']', start + 1);
                if (start < 0 || end < 0) { error = "The tool schema is invalid."; return false; }
                var names = schema.Substring(start + 1, end - start - 1).Split(',');
                foreach (var raw in names)
                {
                    var name = raw.Trim().Trim('\"');
                    if (name.Length > 0 && json.IndexOf("\"" + name + "\"", StringComparison.Ordinal) < 0) { error = "Missing required argument '" + name + "'."; return false; }
                }
            }
            error = string.Empty; return true;
        }

        private static bool IsJsonObject(string json)
        {
            if (json.Length < 2 || json[0] != '{' || json[json.Length - 1] != '}') return false;
            var index = 1; SkipWhitespace(json, ref index);
            if (index == json.Length - 1) return true;
            while (index < json.Length - 1)
            {
                if (!ReadString(json, ref index)) return false;
                SkipWhitespace(json, ref index);
                if (index >= json.Length - 1 || json[index++] != ':') return false;
                SkipWhitespace(json, ref index);
                var valueStart = index; var depth = 0; var inString = false;
                while (index < json.Length - 1)
                {
                    var current = json[index++];
                    if (inString)
                    {
                        if (current == '\\') { if (index >= json.Length - 1) return false; index++; }
                        else if (current == '\"') inString = false;
                    }
                    else if (current == '\"') inString = true;
                    else if (current == '{' || current == '[') depth++;
                    else if (current == '}' || current == ']') { if (depth == 0) return false; depth--; }
                    else if (current == ',' && depth == 0) { index--; break; }
                }
                if (inString || depth != 0 || index == valueStart) return false;
                SkipWhitespace(json, ref index);
                if (index == json.Length - 1) return true;
                if (json[index++] != ',') return false;
                SkipWhitespace(json, ref index);
            }
            return false;
        }
        private static bool ReadString(string value, ref int index)
        {
            if (index >= value.Length || value[index++] != '\"') return false;
            while (index < value.Length)
            {
                var current = value[index++];
                if (current == '\\') { if (index >= value.Length) return false; index++; }
                else if (current == '\"') return true;
            }
            return false;
        }
        private static void SkipWhitespace(string value, ref int index)
        { while (index < value.Length && char.IsWhiteSpace(value[index])) index++; }
    }

    public sealed class ToolRegistry
    {
        private readonly Dictionary<string, ITool> _tools;
        private readonly IReadOnlyList<ToolDefinition> _definitions;
        public ToolRegistry(IEnumerable<ITool> tools)
        {
            _tools = new Dictionary<string, ITool>(StringComparer.Ordinal);
            var definitions = new List<ToolDefinition>();
            foreach (var tool in tools ?? throw new ArgumentNullException(nameof(tools)))
            {
                if (tool == null || !_tools.TryAdd(tool.Definition.Name, tool)) throw new ArgumentException("Tool names must be unique.", nameof(tools));
                definitions.Add(tool.Definition);
            }
            _definitions = definitions.AsReadOnly();
        }
        public IReadOnlyList<ToolDefinition> Definitions => _definitions;
        public bool TryResolve(string name, out ITool tool) => _tools.TryGetValue(name, out tool!);
        public IReadOnlyList<ModelToolDefinition> ToModelDefinitions()
        { var result = new List<ModelToolDefinition>(_definitions.Count); foreach (var definition in _definitions) result.Add(definition.ToModelDefinition()); return result.AsReadOnly(); }
    }
}
