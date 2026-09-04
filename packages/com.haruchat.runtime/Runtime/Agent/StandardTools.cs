#nullable enable

using HaruChat.Runtime.Characters;
using HaruChat.Runtime.Memory;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HaruChat.Runtime.Agent
{
    /// <summary>Small, local-only tools intended for the first allowlist. None grant filesystem or network access.</summary>
    public sealed class CurrentTimeTool : ITool
    {
        public ToolDefinition Definition { get; } = new ToolDefinition("time", "Returns the current UTC time.", "{\"type\":\"object\"}");
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ToolResult(call.CallId, call.Name, true, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
        }
    }

    public sealed class RandomNumberTool : ITool
    {
        public ToolDefinition Definition { get; } = new ToolDefinition("random", "Returns an unbiased random integer in an inclusive range.", "{\"type\":\"object\",\"required\":[\"minimum\",\"maximum\"]}");
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!JsonNumbers.TryGetInt(call.ArgumentsJson, "minimum", out var minimum) || !JsonNumbers.TryGetInt(call.ArgumentsJson, "maximum", out var maximum) || minimum > maximum)
                return Task.FromResult(ToolResult.Failure(call, ToolFailureCode.InvalidArguments, "minimum and maximum must be integers with minimum <= maximum."));
            var span = (long)maximum - minimum + 1;
            if (span > int.MaxValue) return Task.FromResult(ToolResult.Failure(call, ToolFailureCode.InvalidArguments, "The requested range is too large."));
            return Task.FromResult(new ToolResult(call.CallId, call.Name, true, (RandomNumberGenerator.GetInt32((int)span) + minimum).ToString(CultureInfo.InvariantCulture)));
        }
    }

    public sealed class LoreSearchTool : ITool
    {
        private readonly CharacterDefinition _character;
        public LoreSearchTool(CharacterDefinition character) { _character = character ?? throw new ArgumentNullException(nameof(character)); }
        public ToolDefinition Definition { get; } = new ToolDefinition("lore.search", "Searches the selected character's local lore.", "{\"type\":\"object\",\"required\":[\"query\"]}");
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!JsonNumbers.TryGetString(call.ArgumentsJson, "query", out var query) || string.IsNullOrWhiteSpace(query)) return Task.FromResult(ToolResult.Failure(call, ToolFailureCode.InvalidArguments, "query is required."));
            var matches = new List<string>(); foreach (var lore in _character.Lore) if (lore.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) matches.Add(lore);
            return Task.FromResult(new ToolResult(call.CallId, call.Name, true, matches.Count == 0 ? "No matching lore was found." : string.Join("\n---\n", matches)));
        }
    }

    public sealed class MemorySearchTool : ITool
    {
        private readonly IMemoryRetriever _memory;
        public MemorySearchTool(IMemoryRetriever memory) { _memory = memory ?? throw new ArgumentNullException(nameof(memory)); }
        public ToolDefinition Definition { get; } = new ToolDefinition("memory.search", "Searches local memories for the current character.", "{\"type\":\"object\",\"required\":[\"query\"]}");
        public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            if (!JsonNumbers.TryGetString(call.ArgumentsJson, "query", out var query) || string.IsNullOrWhiteSpace(query)) return ToolResult.Failure(call, ToolFailureCode.InvalidArguments, "query is required.");
            var items = await _memory.SearchAsync(new MemoryQuery(context.CharacterId, query, 3), cancellationToken).ConfigureAwait(false);
            var content = new StringBuilder(); foreach (var item in items) { if (content.Length > 0) content.Append('\n'); content.Append(item.Content); }
            return new ToolResult(call.CallId, call.Name, true, content.Length == 0 ? "No matching memory was found." : content.ToString());
        }
    }

    /// <summary>Approved write tool for adding, updating, or deleting a single character-scoped memory.</summary>
    public sealed class MemoryWriteTool : ITool
    {
        private readonly IMemoryStore _memory;
        public MemoryWriteTool(IMemoryStore memory) { _memory = memory ?? throw new ArgumentNullException(nameof(memory)); }
        public ToolDefinition Definition { get; } = new ToolDefinition("memory.write", "Adds, updates, or deletes a local memory after user approval.", "{\"type\":\"object\",\"required\":[\"action\",\"memory_id\"]}", ToolAccess.Write);
        public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            if (!JsonNumbers.TryGetString(call.ArgumentsJson, "action", out var action) || !JsonNumbers.TryGetString(call.ArgumentsJson, "memory_id", out var memoryId)) return ToolResult.Failure(call, ToolFailureCode.InvalidArguments, "action and memory_id are required.");
            if (string.Equals(action, "delete", StringComparison.OrdinalIgnoreCase)) { await _memory.DeleteMemoryAsync(context.CharacterId, memoryId, cancellationToken).ConfigureAwait(false); return new ToolResult(call.CallId, call.Name, true, "Memory deleted."); }
            if (!string.Equals(action, "save", StringComparison.OrdinalIgnoreCase) || !JsonNumbers.TryGetString(call.ArgumentsJson, "content", out var content) || string.IsNullOrWhiteSpace(content)) return ToolResult.Failure(call, ToolFailureCode.InvalidArguments, "Use action save with content, or action delete.");
            var importance = JsonNumbers.TryGetInt(call.ArgumentsJson, "importance", out var parsed) ? parsed : 70;
            if (importance < 0 || importance > 100) return ToolResult.Failure(call, ToolFailureCode.InvalidArguments, "importance must be between 0 and 100.");
            var now = DateTimeOffset.UtcNow; await _memory.SaveMemoryAsync(new MemoryItem(memoryId, context.CharacterId, content.Trim(), importance, now, now, context.SessionId), cancellationToken).ConfigureAwait(false);
            return new ToolResult(call.CallId, call.Name, true, "Memory saved.");
        }
    }

    internal static class JsonNumbers
    {
        public static bool TryGetInt(string json, string name, out int value) => int.TryParse(Value(json, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        public static bool TryGetString(string json, string name, out string value) { var raw = Value(json, name); value = raw == null ? string.Empty : raw; return raw != null; }
        private static string? Value(string json, string name)
        {
            var quoted = Regex.Match(json ?? string.Empty, "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"", RegexOptions.CultureInvariant);
            if (quoted.Success) return Regex.Unescape(quoted.Groups[1].Value);
            var number = Regex.Match(json ?? string.Empty, "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*(-?\\d+)", RegexOptions.CultureInvariant);
            return number.Success ? number.Groups[1].Value : null;
        }
    }
}
