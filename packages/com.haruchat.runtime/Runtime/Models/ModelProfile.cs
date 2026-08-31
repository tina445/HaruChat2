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
        public ModelConfig(string modelPath, string profileId, string? checksum = null, int? contextWindowOverride = null, GenerationOptions? generationOverride = null)
        { if (string.IsNullOrWhiteSpace(modelPath) || string.IsNullOrWhiteSpace(profileId) || (contextWindowOverride.HasValue && contextWindowOverride <= 0)) throw new ArgumentException("Invalid model configuration."); ModelPath = modelPath; ProfileId = profileId; Checksum = checksum; ContextWindowOverride = contextWindowOverride; GenerationOverride = generationOverride; }
        public string ModelPath { get; } public string ProfileId { get; } public string? Checksum { get; } public int? ContextWindowOverride { get; } public GenerationOptions? GenerationOverride { get; }
    }

    public sealed class ModelProfile
    {
        public ModelProfile(string id, int schemaVersion, string namedTemplate, int contextWindowTokens, GenerationOptions defaults, string[]? stopSequences = null, ModelCapabilities? capabilities = null, bool disableThinking = false, string[]? architectureContains = null)
        {
            if (string.IsNullOrWhiteSpace(id) || schemaVersion != 1 || string.IsNullOrWhiteSpace(namedTemplate) || contextWindowTokens <= 0) throw new ArgumentException("Invalid model profile.");
            if (stopSequences != null && Array.Exists(stopSequences, string.IsNullOrEmpty)) throw new ArgumentException("Stop sequences must be non-empty.", nameof(stopSequences));
            if (architectureContains != null && Array.Exists(architectureContains, string.IsNullOrWhiteSpace)) throw new ArgumentException("Architecture matchers must be non-empty.", nameof(architectureContains));
            Id = id; SchemaVersion = schemaVersion; NamedTemplate = namedTemplate; ContextWindowTokens = contextWindowTokens; Defaults = defaults ?? throw new ArgumentNullException(nameof(defaults)); StopSequences = Array.AsReadOnly((stopSequences ?? Array.Empty<string>()).Clone() as string[] ?? Array.Empty<string>()); Capabilities = capabilities ?? new ModelCapabilities(); DisableThinking = disableThinking; ArchitectureContains = Array.AsReadOnly((architectureContains ?? Array.Empty<string>()).Clone() as string[] ?? Array.Empty<string>());
        }
        public string Id { get; } public int SchemaVersion { get; } public string NamedTemplate { get; } public int ContextWindowTokens { get; } public GenerationOptions Defaults { get; } public IReadOnlyList<string> StopSequences { get; } public ModelCapabilities Capabilities { get; } public bool DisableThinking { get; } public IReadOnlyList<string> ArchitectureContains { get; }
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
                    return new ModelProfile(value.Id ?? string.Empty, value.SchemaVersion, value.NamedTemplate ?? string.Empty, value.ContextWindowTokens, new GenerationOptions(value.MaximumOutputTokens, value.Temperature, value.TopK, value.TopP, value.Seed), value.StopSequences, new ModelCapabilities(value.Streaming, value.Cancellation, value.Tools, value.Reasoning), value.DisableThinking, value.ArchitectureContains);
                }
            }
            catch (Exception error) when (!(error is ArgumentNullException)) { throw new InvalidOperationException("Invalid model profile: " + error.Message, error); }
        }

        [DataContract]
        private sealed class ProfileDocument
        {
            [DataMember(Name = "id")] public string? Id { get; set; }
            [DataMember(Name = "schemaVersion")] public int SchemaVersion { get; set; }
            [DataMember(Name = "namedTemplate")] public string? NamedTemplate { get; set; }
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
            [DataMember(Name = "disableThinking")] public bool DisableThinking { get; set; }
            [DataMember(Name = "architectureContains")] public string[]? ArchitectureContains { get; set; }
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
