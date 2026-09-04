#nullable enable

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HaruChat.Runtime.Memory
{
    public static class MemoryPrivacy
    {
        private static readonly Regex Sensitive = new Regex("\\b(password|passcode|secret|api[ _-]?key|token|credential|ssn|social security|credit card|cvv|private key)\\b|비밀번호|암호|인증\\s*코드|주민등록|카드\\s*번호|계좌\\s*번호|API\\s*키|토큰", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex PreciseLocation = new Regex("\\b\\d{1,5}\\s+[A-Za-z][A-Za-z .'-]{2,}\\s+(street|st|road|rd|avenue|ave|boulevard|blvd|lane|ln)\\b|(?:주소|번지|호)\\s*[:：]?\\s*[^.!?\\n]{3,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        public static bool IsSensitive(string value) { return !string.IsNullOrWhiteSpace(value) && (Sensitive.IsMatch(value) || PreciseLocation.IsMatch(value)); }
    }
    public sealed class MemoryItem
    {
        public MemoryItem(string memoryId, string characterId, string content, int importance, DateTimeOffset createdAt, DateTimeOffset updatedAt, string? sourceSessionId = null, DateTimeOffset? expiresAt = null)
        {
            if (string.IsNullOrWhiteSpace(memoryId) || string.IsNullOrWhiteSpace(characterId) || string.IsNullOrWhiteSpace(content)) throw new ArgumentException("Memory ID, character ID, and content are required.");
            if (importance < 0 || importance > 100) throw new ArgumentOutOfRangeException(nameof(importance));
            MemoryId = memoryId; CharacterId = characterId; Content = content; Importance = importance; CreatedAt = createdAt; UpdatedAt = updatedAt; SourceSessionId = sourceSessionId; ExpiresAt = expiresAt;
        }
        public string MemoryId { get; } public string CharacterId { get; } public string Content { get; } public int Importance { get; }
        public DateTimeOffset CreatedAt { get; } public DateTimeOffset UpdatedAt { get; } public string? SourceSessionId { get; } public DateTimeOffset? ExpiresAt { get; }
    }

    public sealed class MemorySession
    {
        public MemorySession(string sessionId, string characterId, string summaryText, DateTimeOffset createdAt, DateTimeOffset updatedAt, DateTimeOffset? expiresAt = null)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(characterId)) throw new ArgumentException("Session ID and character ID are required.");
            SessionId = sessionId; CharacterId = characterId; SummaryText = summaryText ?? string.Empty; CreatedAt = createdAt; UpdatedAt = updatedAt; ExpiresAt = expiresAt;
        }
        public string SessionId { get; } public string CharacterId { get; } public string SummaryText { get; }
        public DateTimeOffset CreatedAt { get; } public DateTimeOffset UpdatedAt { get; } public DateTimeOffset? ExpiresAt { get; }
    }

    public sealed class MemoryQuery
    {
        public MemoryQuery(string characterId, string text, int maximumResults = 8, DateTimeOffset? asOf = null)
        {
            if (string.IsNullOrWhiteSpace(characterId) || string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Character ID and query text are required.");
            if (maximumResults <= 0) throw new ArgumentOutOfRangeException(nameof(maximumResults));
            CharacterId = characterId; Text = text; MaximumResults = maximumResults; AsOf = asOf ?? DateTimeOffset.UtcNow;
        }
        public string CharacterId { get; } public string Text { get; } public int MaximumResults { get; } public DateTimeOffset AsOf { get; }
    }

    public sealed class MemoryPersistenceOptions
    {
        public MemoryPersistenceOptions(bool enabled = false, TimeSpan? retention = null, bool automaticallySaveImportantMemories = false, int automaticMemoryImportanceThreshold = 70)
        {
            if (enabled && (!retention.HasValue || retention.Value <= TimeSpan.Zero)) throw new ArgumentException("Enabled memory persistence requires a positive retention period.", nameof(retention));
            if (automaticMemoryImportanceThreshold < 1 || automaticMemoryImportanceThreshold > 100) throw new ArgumentOutOfRangeException(nameof(automaticMemoryImportanceThreshold));
            Enabled = enabled; Retention = retention; AutomaticallySaveImportantMemories = automaticallySaveImportantMemories; AutomaticMemoryImportanceThreshold = automaticMemoryImportanceThreshold;
        }
        public bool Enabled { get; } public TimeSpan? Retention { get; }
        /// <summary>Opt-in automatic capture. It never enables persistence by itself.</summary>
        public bool AutomaticallySaveImportantMemories { get; }
        public int AutomaticMemoryImportanceThreshold { get; }
    }

    /// <summary>Character-scoped controls exposed by the memory notebook/settings UX.</summary>
    public sealed class MemorySettings
    {
        public MemorySettings(string characterId, bool enabled = false, TimeSpan? retention = null, int maximumRetrievedItems = 3, int maximumPromptTokens = 256, bool includeRecentSessionSummary = true, bool automaticallySaveImportantMemories = false, int automaticMemoryImportanceThreshold = 70)
        {
            if (string.IsNullOrWhiteSpace(characterId)) throw new ArgumentException("Character ID is required.", nameof(characterId));
            if (!retention.HasValue || retention.Value <= TimeSpan.Zero) throw new ArgumentException("A positive retention period is required.", nameof(retention));
            if (maximumRetrievedItems < 1 || maximumRetrievedItems > 8) throw new ArgumentOutOfRangeException(nameof(maximumRetrievedItems));
            if (maximumPromptTokens < 32) throw new ArgumentOutOfRangeException(nameof(maximumPromptTokens));
            if (automaticMemoryImportanceThreshold < 1 || automaticMemoryImportanceThreshold > 100) throw new ArgumentOutOfRangeException(nameof(automaticMemoryImportanceThreshold));
            CharacterId = characterId; Enabled = enabled; Retention = retention.Value; MaximumRetrievedItems = maximumRetrievedItems; MaximumPromptTokens = maximumPromptTokens; IncludeRecentSessionSummary = includeRecentSessionSummary; AutomaticallySaveImportantMemories = automaticallySaveImportantMemories; AutomaticMemoryImportanceThreshold = automaticMemoryImportanceThreshold;
        }
        public string CharacterId { get; } public bool Enabled { get; } public TimeSpan Retention { get; } public int MaximumRetrievedItems { get; } public int MaximumPromptTokens { get; } public bool IncludeRecentSessionSummary { get; }
        public bool AutomaticallySaveImportantMemories { get; } public int AutomaticMemoryImportanceThreshold { get; }
        public MemoryPersistenceOptions ToPersistenceOptions() => new MemoryPersistenceOptions(Enabled, Retention, AutomaticallySaveImportantMemories, AutomaticMemoryImportanceThreshold);
        public MemoryPromptPolicy ToPromptPolicy() => new MemoryPromptPolicy(MaximumRetrievedItems, MaximumPromptTokens, IncludeRecentSessionSummary ? 1 : 0);
        public static MemorySettings Disabled(string characterId) => new MemorySettings(characterId, retention: TimeSpan.FromDays(30));
    }

    /// <summary>Bounds retrieved memory before it can displace character instructions or recent turns.</summary>
    public sealed class MemoryPromptPolicy
    {
        public MemoryPromptPolicy(int maximumItems = 3, int maximumEstimatedTokens = 256, int maximumSessionSummaries = 1)
        {
            if (maximumItems < 0 || maximumEstimatedTokens < 0 || maximumSessionSummaries < 0) throw new ArgumentOutOfRangeException(nameof(maximumItems));
            MaximumItems = maximumItems; MaximumEstimatedTokens = maximumEstimatedTokens; MaximumSessionSummaries = maximumSessionSummaries;
        }
        public int MaximumItems { get; } public int MaximumEstimatedTokens { get; } public int MaximumSessionSummaries { get; }
    }

    public enum MemoryErrorCode { Unavailable, InvalidData, Busy, StorageFull, Corrupt, Cancelled }
    public sealed class MemoryOperationException : Exception
    {
        public MemoryOperationException(MemoryErrorCode code, string message, bool recoverable = true, Exception? innerException = null) : base(message, innerException) { Code = code; Recoverable = recoverable; }
        public MemoryErrorCode Code { get; } public bool Recoverable { get; }
    }

    public interface IMemoryStore
    {
        Task UpsertSessionAsync(MemorySession session, CancellationToken cancellationToken);
        Task SaveMemoryAsync(MemoryItem item, CancellationToken cancellationToken);
        Task DeleteMemoryAsync(string characterId, string memoryId, CancellationToken cancellationToken);
        Task ClearCharacterAsync(string characterId, CancellationToken cancellationToken);
        Task<IReadOnlyList<MemorySession>> ExportSessionsAsync(string characterId, CancellationToken cancellationToken);
        Task<IReadOnlyList<MemoryItem>> ExportMemoriesAsync(string characterId, CancellationToken cancellationToken);
        Task DeleteExpiredAsync(DateTimeOffset asOf, CancellationToken cancellationToken);
    }

    public interface IMemoryRetriever
    {
        Task<IReadOnlyList<MemoryItem>> SearchAsync(MemoryQuery query, CancellationToken cancellationToken);
        Task<IReadOnlyList<MemorySession>> GetRecentSessionsAsync(string characterId, int maximumResults, DateTimeOffset asOf, CancellationToken cancellationToken);
    }

    public interface IMemorySettingsStore
    {
        Task<MemorySettings> GetSettingsAsync(string characterId, CancellationToken cancellationToken);
        Task SaveSettingsAsync(MemorySettings settings, CancellationToken cancellationToken);
    }

    public interface IMemoryCandidateFactory
    {
        MemoryItem? Create(string characterId, string sessionId, string userInput, string assistantResponse, DateTimeOffset now, DateTimeOffset? expiresAt);
    }

    public sealed class NoMemoryCandidateFactory : IMemoryCandidateFactory
    {
        public MemoryItem? Create(string characterId, string sessionId, string userInput, string assistantResponse, DateTimeOffset now, DateTimeOffset? expiresAt) { return null; }
    }

    /// <summary>
    /// Conservative, local-only extraction of durable user facts. The caller remains responsible
    /// for opt-in and retention; this class deliberately declines ambiguous or sensitive turns.
    /// </summary>
    public sealed class RuleBasedMemoryCandidateFactory : IMemoryCandidateFactory
    {
        private static readonly Regex Whitespace = new Regex("\\s+", RegexOptions.CultureInvariant);
        private static readonly Regex Sensitive = new Regex("\\b(password|passcode|secret|api[ _-]?key|token|credential|ssn|social security|credit card|cvv|private key)\\b|비밀번호|암호|인증\\s*코드|주민등록|카드\\s*번호|계좌\\s*번호|API\\s*키|토큰", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex PreciseLocation = new Regex("\\b\\d{1,5}\\s+[A-Za-z][A-Za-z .'-]{2,}\\s+(street|st|road|rd|avenue|ave|boulevard|blvd|lane|ln)\\b|(?:주소|번지|호)\\s*[:：]?\\s*[^.!?\\n]{3,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Explicit = new Regex("\\b(remember|remember that|keep in mind|don't forget)\\b|(?:기억해|기억해줘|잊지\\s*마|메모해)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Preference = new Regex("\\b(i (?:really )?(?:like|love|prefer|enjoy|hate)|my (?:favorite|preferred) |i am |i'm |my name is |i work as |i live in )\\b|(?:나는|전|제가)\\s*[^.!?\\n]{0,80}(?:좋아|싫어|선호|이름|직업|살아|입니다|이에요)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex RelationshipOrPlan = new Regex("\\b(my (?:partner|spouse|wife|husband|child|daughter|son|friend)|i (?:will|plan to|am going to|promise to))\\b|(?:내|제)\\s*[^.!?\\n]{0,80}(?:친구|가족|배우자|엄마|아빠|계획|약속)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public MemoryItem? Create(string characterId, string sessionId, string userInput, string assistantResponse, DateTimeOffset now, DateTimeOffset? expiresAt)
        {
            if (string.IsNullOrWhiteSpace(characterId) || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(userInput)) return null;
            var fact = Normalize(userInput);
            if (fact.Length < 4 || fact.Length > 400 || MemoryPrivacy.IsSensitive(fact)) return null;

            var explicitRequest = Explicit.IsMatch(fact);
            var stableFact = Preference.IsMatch(fact) || RelationshipOrPlan.IsMatch(fact);
            if (!explicitRequest && !stableFact) return null;

            // IDs are character-scoped so recurring statements replace rather than duplicate a fact.
            var memoryId = "auto-" + StableId(characterId + "\n" + fact.ToLowerInvariant());
            var importance = explicitRequest ? 85 : RelationshipOrPlan.IsMatch(fact) ? 78 : 72;
            return new MemoryItem(memoryId, characterId, fact, importance, now, now, sessionId, expiresAt);
        }

        private static string Normalize(string value) => Whitespace.Replace(value.Trim(), " ");
        private static string StableId(string value)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var valueByte in hash) builder.Append(valueByte.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return builder.ToString();
        }
    }
}
