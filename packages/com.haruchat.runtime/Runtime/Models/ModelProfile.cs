#nullable enable

using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace HaruChat.Runtime.Models
{
    public sealed class ModelConfig
    {
        public ModelConfig(string modelPath, string profileId, string? checksum = null, int? contextWindowOverride = null, GenerationOptions? generationOverride = null)
        { ModelPath = modelPath ?? throw new ArgumentNullException(nameof(modelPath)); ProfileId = profileId ?? throw new ArgumentNullException(nameof(profileId)); Checksum = checksum; ContextWindowOverride = contextWindowOverride; GenerationOverride = generationOverride; }
        public string ModelPath { get; } public string ProfileId { get; } public string? Checksum { get; } public int? ContextWindowOverride { get; } public GenerationOptions? GenerationOverride { get; }
    }

    public sealed class ModelProfile
    {
        public ModelProfile(string id, int schemaVersion, string namedTemplate, int contextWindowTokens, GenerationOptions defaults, string[]? stopSequences = null)
        {
            if (string.IsNullOrWhiteSpace(id) || schemaVersion != 1 || string.IsNullOrWhiteSpace(namedTemplate) || contextWindowTokens <= 0) throw new ArgumentException("Invalid model profile.");
            Id = id; SchemaVersion = schemaVersion; NamedTemplate = namedTemplate; ContextWindowTokens = contextWindowTokens; Defaults = defaults ?? throw new ArgumentNullException(nameof(defaults)); StopSequences = stopSequences ?? Array.Empty<string>();
        }
        public string Id { get; } public int SchemaVersion { get; } public string NamedTemplate { get; } public int ContextWindowTokens { get; } public GenerationOptions Defaults { get; } public string[] StopSequences { get; }
        public int ResolveContextWindow(ModelConfig config) { return config.ContextWindowOverride ?? ContextWindowTokens; }
        public GenerationOptions ResolveGeneration(ModelConfig config) { return config.GenerationOverride ?? Defaults; }
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
                    return new ModelProfile(value.Id ?? string.Empty, value.SchemaVersion, value.NamedTemplate ?? string.Empty, value.ContextWindowTokens, new GenerationOptions(value.MaximumOutputTokens, value.Temperature, value.TopK, value.TopP, value.Seed), value.StopSequences);
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
        }
    }
}
