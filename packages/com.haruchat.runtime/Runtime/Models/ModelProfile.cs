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
        public ModelProfile(string id, int schemaVersion, string namedTemplate, int contextWindowTokens, GenerationOptions defaults, string[]? stopSequences = null, ModelCapabilities? capabilities = null)
        {
            if (string.IsNullOrWhiteSpace(id) || schemaVersion != 1 || string.IsNullOrWhiteSpace(namedTemplate) || contextWindowTokens <= 0) throw new ArgumentException("Invalid model profile.");
            if (stopSequences != null && Array.Exists(stopSequences, string.IsNullOrEmpty)) throw new ArgumentException("Stop sequences must be non-empty.", nameof(stopSequences));
            Id = id; SchemaVersion = schemaVersion; NamedTemplate = namedTemplate; ContextWindowTokens = contextWindowTokens; Defaults = defaults ?? throw new ArgumentNullException(nameof(defaults)); StopSequences = Array.AsReadOnly((stopSequences ?? Array.Empty<string>()).Clone() as string[] ?? Array.Empty<string>()); Capabilities = capabilities ?? new ModelCapabilities();
        }
        public string Id { get; } public int SchemaVersion { get; } public string NamedTemplate { get; } public int ContextWindowTokens { get; } public GenerationOptions Defaults { get; } public IReadOnlyList<string> StopSequences { get; } public ModelCapabilities Capabilities { get; }
        public int ResolveContextWindow(ModelConfig config) { return config.ContextWindowOverride ?? ContextWindowTokens; }
        public GenerationOptions ResolveGeneration(ModelConfig config) { return config.GenerationOverride ?? Defaults; }
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
                    return new ModelProfile(value.Id ?? string.Empty, value.SchemaVersion, value.NamedTemplate ?? string.Empty, value.ContextWindowTokens, new GenerationOptions(value.MaximumOutputTokens, value.Temperature, value.TopK, value.TopP, value.Seed), value.StopSequences, new ModelCapabilities(value.Streaming, value.Cancellation, value.Tools, value.Reasoning));
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
        }
    }
}
