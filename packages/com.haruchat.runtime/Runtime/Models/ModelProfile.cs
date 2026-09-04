#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace HaruChat.Runtime.Models
{
    public sealed class ModelConfig
    {
        public ModelConfig(string modelPath, string? profileId = null, string? checksum = null, int? contextWindowOverride = null, GenerationOptions? generationOverride = null)
        { if (string.IsNullOrWhiteSpace(modelPath) || (contextWindowOverride.HasValue && contextWindowOverride <= 0)) throw new ArgumentException("Invalid model configuration."); ModelPath = modelPath; ProfileId = profileId; Checksum = checksum; ContextWindowOverride = contextWindowOverride; GenerationOverride = generationOverride; }
        public string ModelPath { get; } public string? ProfileId { get; } public string? Checksum { get; } public int? ContextWindowOverride { get; } public GenerationOptions? GenerationOverride { get; }
    }

    /// <summary>Presentation-neutral, user-selected runtime controls. The adapter still receives a normal ModelConfig.</summary>
    public sealed class ModelRuntimeSettings
    {
        public ModelRuntimeSettings(int contextWindowTokens, float temperature)
        {
            if (contextWindowTokens <= 0 || temperature < 0 || temperature > 2) throw new ArgumentOutOfRangeException(nameof(contextWindowTokens));
            ContextWindowTokens = contextWindowTokens; Temperature = temperature;
        }
        public int ContextWindowTokens { get; } public float Temperature { get; }
        public ModelConfig Apply(ModelConfig config, GenerationOptions defaults)
        {
            if (config == null || defaults == null) throw new ArgumentNullException(config == null ? nameof(config) : nameof(defaults));
            return new ModelConfig(config.ModelPath, config.ProfileId, config.Checksum, ContextWindowTokens, new GenerationOptions(defaults.MaximumOutputTokens, Temperature, defaults.TopK, defaults.TopP, defaults.Seed));
        }
    }

    /// <summary>Explains context capacity without inventing device-memory metrics when telemetry is unavailable.</summary>
    public sealed class ContextWindowRecommendation
    {
        public ContextWindowRecommendation(int minimumTokens, int recommendedTokens, int maximumTokens, int reservedTokens, bool hardwareMeasured)
        { MinimumTokens = minimumTokens; RecommendedTokens = recommendedTokens; MaximumTokens = maximumTokens; ReservedTokens = reservedTokens; HardwareMeasured = hardwareMeasured; }
        public int MinimumTokens { get; } public int RecommendedTokens { get; } public int MaximumTokens { get; } public int ReservedTokens { get; } public bool HardwareMeasured { get; }
    }

    public static class ContextWindowAdvisor
    {
        public static ContextWindowRecommendation Recommend(int modelMaximumTokens, int characterInstructionTokens, int memoryReserveTokens, int maximumOutputTokens, int? hardwareMaximumTokens = null)
        {
            if (modelMaximumTokens <= 0 || characterInstructionTokens < 0 || memoryReserveTokens < 0 || maximumOutputTokens <= 0) throw new ArgumentOutOfRangeException(nameof(modelMaximumTokens));
            var maximum = Math.Min(modelMaximumTokens, hardwareMaximumTokens ?? modelMaximumTokens);
            var reserved = characterInstructionTokens + memoryReserveTokens + maximumOutputTokens;
            var minimum = Math.Min(maximum, Math.Max(512, reserved + 256));
            var recommended = Math.Min(maximum, Math.Max(minimum, reserved * 2));
            return new ContextWindowRecommendation(minimum, recommended, maximum, reserved, hardwareMaximumTokens.HasValue);
        }
    }

    /// <summary>
    /// A constrained, data-only chat template.  It deliberately supports only
    /// role/content substitution: model files and profiles must not execute
    /// arbitrary template code in the app process.
    /// </summary>
    public sealed class ChatTemplate
    {
        public ChatTemplate(string messageTemplate, string assistantTemplate, IReadOnlyDictionary<ModelRole, string>? roleNames = null)
        {
            if (string.IsNullOrEmpty(messageTemplate) || string.IsNullOrEmpty(assistantTemplate) ||
                !HasOnlyMessagePlaceholders(messageTemplate) ||
                messageTemplate.IndexOf("{role}", StringComparison.Ordinal) < 0 ||
                messageTemplate.IndexOf("{content}", StringComparison.Ordinal) < 0 ||
                assistantTemplate.IndexOf("{role}", StringComparison.Ordinal) >= 0 ||
                assistantTemplate.IndexOf("{content}", StringComparison.Ordinal) >= 0)
                throw new ArgumentException("Invalid chat template.");

            MessageTemplate = messageTemplate;
            AssistantTemplate = assistantTemplate;
            var names = new Dictionary<ModelRole, string>();
            foreach (ModelRole role in Enum.GetValues(typeof(ModelRole))) names[role] = role.ToString().ToLowerInvariant();
            if (roleNames != null)
                foreach (var entry in roleNames)
                {
                    if (string.IsNullOrWhiteSpace(entry.Value)) throw new ArgumentException("Chat role names must be non-empty.", nameof(roleNames));
                    names[entry.Key] = entry.Value;
                }
            RoleNames = new Dictionary<ModelRole, string>(names);
        }

        public string MessageTemplate { get; }
        public string AssistantTemplate { get; }
        public IReadOnlyDictionary<ModelRole, string> RoleNames { get; }

        public string Render(IReadOnlyList<ModelMessage> messages)
        {
            if (messages == null || messages.Count == 0) throw new ArgumentException("At least one message is required.", nameof(messages));
            var output = new StringBuilder();
            foreach (var message in messages)
            {
                var role = RoleNames[message.Role];
                output.Append(MessageTemplate.Replace("{role}", role).Replace("{content}", message.Text));
            }
            output.Append(AssistantTemplate);
            return output.ToString();
        }

        private static bool HasOnlyMessagePlaceholders(string template)
        {
            for (var index = 0; index < template.Length; index++)
            {
                if (template[index] != '{') continue;
                var close = template.IndexOf('}', index + 1);
                if (close < 0) return false;
                var token = template.Substring(index, close - index + 1);
                if (!string.Equals(token, "{role}", StringComparison.Ordinal) && !string.Equals(token, "{content}", StringComparison.Ordinal)) return false;
                index = close;
            }
            return template.IndexOf('}', StringComparison.Ordinal) < 0 ||
                   HasNoUnmatchedClosingBrace(template);
        }

        private static bool HasNoUnmatchedClosingBrace(string template)
        {
            var cursor = 0;
            while (cursor < template.Length)
            {
                var opening = template.IndexOf('{', cursor);
                var closing = template.IndexOf('}', cursor);
                if (closing >= 0 && (opening < 0 || closing < opening)) return false;
                if (opening < 0) return true;
                cursor = template.IndexOf('}', opening + 1) + 1;
            }
            return true;
        }
    }

    public enum ReasoningOutputMode { Show, Hide, Separate }

    /// <summary>Profile-owned handling for explicit reasoning delimiters in a streamed model response.</summary>
    public sealed class ReasoningOutputPolicy
    {
        public ReasoningOutputPolicy(string openMarker, string closeMarker, ReasoningOutputMode mode)
        {
            if (string.IsNullOrEmpty(openMarker) || string.IsNullOrEmpty(closeMarker) || string.Equals(openMarker, closeMarker, StringComparison.Ordinal))
                throw new ArgumentException("Reasoning markers must be distinct and non-empty.");
            OpenMarker = openMarker; CloseMarker = closeMarker; Mode = mode;
        }
        public string OpenMarker { get; } public string CloseMarker { get; } public ReasoningOutputMode Mode { get; }
    }

    public sealed class ModelProfile
    {
        public ModelProfile(string id, int schemaVersion, ChatTemplate chatTemplate, int contextWindowTokens, GenerationOptions defaults, string[]? stopSequences = null, ModelCapabilities? capabilities = null, string[]? architectureContains = null, ReasoningOutputPolicy? reasoningOutput = null)
        {
            if (string.IsNullOrWhiteSpace(id) || schemaVersion != 1 || chatTemplate == null || contextWindowTokens <= 0) throw new ArgumentException("Invalid model profile.");
            if (stopSequences != null && Array.Exists(stopSequences, string.IsNullOrEmpty)) throw new ArgumentException("Stop sequences must be non-empty.", nameof(stopSequences));
            if (architectureContains != null && Array.Exists(architectureContains, string.IsNullOrWhiteSpace)) throw new ArgumentException("Architecture matchers must be non-empty.", nameof(architectureContains));
            Id = id; SchemaVersion = schemaVersion; ChatTemplate = chatTemplate; ContextWindowTokens = contextWindowTokens; Defaults = defaults ?? throw new ArgumentNullException(nameof(defaults)); StopSequences = Array.AsReadOnly((stopSequences ?? Array.Empty<string>()).Clone() as string[] ?? Array.Empty<string>()); Capabilities = capabilities ?? new ModelCapabilities(); ArchitectureContains = Array.AsReadOnly((architectureContains ?? Array.Empty<string>()).Clone() as string[] ?? Array.Empty<string>()); ReasoningOutput = reasoningOutput;
        }
        public string Id { get; } public int SchemaVersion { get; } public ChatTemplate ChatTemplate { get; } public int ContextWindowTokens { get; } public GenerationOptions Defaults { get; } public IReadOnlyList<string> StopSequences { get; } public ModelCapabilities Capabilities { get; } public IReadOnlyList<string> ArchitectureContains { get; } public ReasoningOutputPolicy? ReasoningOutput { get; }
        public int ResolveContextWindow(ModelConfig config) { return config.ContextWindowOverride ?? ContextWindowTokens; }
        public GenerationOptions ResolveGeneration(ModelConfig config) { return config.GenerationOverride ?? Defaults; }
        public bool Matches(LocalModels.LocalModelMetadata metadata) { if (metadata == null || ArchitectureContains.Count == 0) return false; foreach (var value in ArchitectureContains) if (metadata.Architecture.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0) return true; return false; }
        public static void ValidateModelChecksum(ModelConfig config)
        {
            if (string.IsNullOrWhiteSpace(config.Checksum)) return;
            using (var sha = SHA256.Create()) using (var stream = File.OpenRead(config.ModelPath))
            {
                var actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
                var expected = config.Checksum!;
                if (expected.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) expected = expected.Substring("sha256:".Length);
                expected = expected.ToLowerInvariant();
                if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw new ModelOperationException(ModelErrorCode.InvalidConfiguration, "Model checksum does not match configuration.");
            }
        }
    }

    public static class ModelProfileLoader
    {
        public static ModelProfile Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var value = (ProfileDocument)new DataContractJsonSerializer(typeof(ProfileDocument)).ReadObject(stream)!;
                    var template = value.ChatTemplate ?? throw new ArgumentException("A chatTemplate is required.");
                    var roles = new Dictionary<ModelRole, string>();
                    foreach (var role in template.Roles ?? Array.Empty<ProfileRole>())
                    {
                        if (!Enum.TryParse<ModelRole>(role.Role, true, out var parsed)) throw new ArgumentException("Unknown model role in chatTemplate.");
                        if (!roles.TryAdd(parsed, role.Name ?? string.Empty)) throw new ArgumentException("Duplicate model role in chatTemplate.");
                    }
                    ReasoningOutputPolicy? reasoningOutput = null;
                    if (value.ReasoningOutput != null)
                    {
                        if (!Enum.TryParse<ReasoningOutputMode>(value.ReasoningOutput.Mode, true, out var mode)) throw new ArgumentException("Unknown reasoningOutput mode.");
                        reasoningOutput = new ReasoningOutputPolicy(value.ReasoningOutput.OpenMarker ?? string.Empty, value.ReasoningOutput.CloseMarker ?? string.Empty, mode);
                    }
                    return new ModelProfile(value.Id ?? string.Empty, value.SchemaVersion, new ChatTemplate(template.MessageTemplate ?? string.Empty, template.AssistantTemplate ?? string.Empty, roles), value.ContextWindowTokens, new GenerationOptions(value.MaximumOutputTokens, value.Temperature, value.TopK, value.TopP, value.Seed), value.StopSequences, new ModelCapabilities(value.Streaming, value.Cancellation, value.Tools, value.Reasoning), value.ArchitectureContains, reasoningOutput);
                }
            }
            catch (Exception error) when (!(error is ArgumentNullException)) { throw new InvalidOperationException("Invalid model profile: " + error.Message, error); }
        }

        [DataContract]
        private sealed class ProfileDocument
        {
            [DataMember(Name = "id")] public string? Id { get; set; }
            [DataMember(Name = "schemaVersion")] public int SchemaVersion { get; set; }
            [DataMember(Name = "chatTemplate")] public ProfileChatTemplate? ChatTemplate { get; set; }
            [DataMember(Name = "contextWindowTokens")] public int ContextWindowTokens { get; set; }
            [DataMember(Name = "maximumOutputTokens")] public int MaximumOutputTokens { get; set; }
            [DataMember(Name = "temperature")] public float Temperature { get; set; }
            [DataMember(Name = "topK")] public int TopK { get; set; }
            [DataMember(Name = "topP")] public float TopP { get; set; }
            [DataMember(Name = "seed")] public int? Seed { get; set; }
            [DataMember(Name = "stopSequences")] public string[]? StopSequences { get; set; }
            [DataMember(Name = "streaming")] public bool Streaming { get; set; } = true;
            [DataMember(Name = "cancellation")] public bool Cancellation { get; set; } = true;
            [DataMember(Name = "tools")] public bool Tools { get; set; }
            [DataMember(Name = "reasoning")] public bool Reasoning { get; set; }
            [DataMember(Name = "reasoningOutput")] public ProfileReasoningOutput? ReasoningOutput { get; set; }
            [DataMember(Name = "architectureContains")] public string[]? ArchitectureContains { get; set; }
        }

        [DataContract]
        private sealed class ProfileChatTemplate
        {
            [DataMember(Name = "messageTemplate")] public string? MessageTemplate { get; set; }
            [DataMember(Name = "assistantTemplate")] public string? AssistantTemplate { get; set; }
            [DataMember(Name = "roles")] public ProfileRole[]? Roles { get; set; }
        }

        [DataContract]
        private sealed class ProfileRole
        {
            [DataMember(Name = "role")] public string? Role { get; set; }
            [DataMember(Name = "name")] public string? Name { get; set; }
        }

        [DataContract]
        private sealed class ProfileReasoningOutput
        {
            [DataMember(Name = "openMarker")] public string? OpenMarker { get; set; }
            [DataMember(Name = "closeMarker")] public string? CloseMarker { get; set; }
            [DataMember(Name = "mode")] public string? Mode { get; set; }
        }
    }

    /// <summary>Explicit profile binding wins; metadata auto-selection is only a constrained fallback.</summary>
    public sealed class ModelProfileCatalog
    {
        private readonly Dictionary<string, ModelProfile> _byId;
        public ModelProfileCatalog(IEnumerable<ModelProfile> profiles)
        {
            if (profiles == null) throw new ArgumentNullException(nameof(profiles)); _byId = new Dictionary<string, ModelProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in profiles) if (profile == null || !_byId.TryAdd(profile.Id, profile)) throw new ArgumentException("Model profile IDs must be unique.", nameof(profiles));
        }
        public ModelProfile Resolve(string? explicitProfileId, LocalModels.LocalModelMetadata metadata)
        {
            if (!string.IsNullOrWhiteSpace(explicitProfileId)) { if (_byId.TryGetValue(explicitProfileId, out var explicitProfile)) return explicitProfile; throw new ModelOperationException(ModelErrorCode.NotFound, "Unknown model profile: " + explicitProfileId); }
            ModelProfile? match = null;
            foreach (var profile in _byId.Values) if (profile.Matches(metadata)) { if (match != null) throw new ModelOperationException(ModelErrorCode.InvalidConfiguration, "Model metadata matches multiple profiles."); match = profile; }
            if (match == null) throw new ModelOperationException(ModelErrorCode.InvalidConfiguration, "No safe model profile matches the model metadata.");
            return match;
        }
    }
}
