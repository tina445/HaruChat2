#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using HaruChat.Runtime.Models;

namespace HaruChat.OpenAI
{
#pragma warning disable CS0649 // Fields are populated by DataContractJsonSerializer.
    /// <summary>Configuration deliberately requires a user-visible opt-in before any request can leave the device.</summary>
    public sealed class OpenAiCompatibleProviderConfiguration
    {
        public OpenAiCompatibleProviderConfiguration(string id, Uri endpoint, string model, string apiKeyReference, bool remoteTransmissionOptedIn)
        {
            if (string.IsNullOrWhiteSpace(id) || endpoint == null || !endpoint.IsAbsoluteUri || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(apiKeyReference)) throw new ArgumentException("Provider ID, absolute endpoint, model, and API-key reference are required.");
            Id = id; Endpoint = endpoint; Model = model; ApiKeyReference = apiKeyReference; RemoteTransmissionOptedIn = remoteTransmissionOptedIn;
        }
        public string Id { get; }
        public Uri Endpoint { get; }
        public string Model { get; }
        /// <summary>Opaque secure-storage lookup key; never the API key itself.</summary>
        public string ApiKeyReference { get; }
        public bool RemoteTransmissionOptedIn { get; }
    }

    /// <summary>Implemented by the platform secure-storage adapter, never by the HTTP adapter.</summary>
    public interface ISecureApiKeyStore
    {
        System.Threading.Tasks.Task<string?> GetApiKeyAsync(string keyReference, System.Threading.CancellationToken cancellationToken);
    }

    /// <summary>
    /// A provider-safe request view. Current model contracts have no provenance field, so memory is
    /// recognized only by the canonical prompt compiler's "Relevant memory:" marker.
    /// </summary>
    public sealed class OpenAiRequestProjection
    {
        public OpenAiRequestProjection(string model, IReadOnlyList<ModelMessage> messages, GenerationOptions? generation)
        { Model = model; Messages = messages; Generation = generation; }
        public string Model { get; }
        public IReadOnlyList<ModelMessage> Messages { get; }
        public GenerationOptions? Generation { get; }
        public static OpenAiRequestProjection From(ModelRequest request, string model)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var messages = request.Messages.Where(message => message.Role != ModelRole.Tool && !message.Text.StartsWith("Relevant memory:", StringComparison.Ordinal)).ToList().AsReadOnly();
            return new OpenAiRequestProjection(model, messages, request.Generation);
        }
    }

    [DataContract] internal sealed class ChatCompletionRequest
    {
        [DataMember(Name = "model")] public string Model = string.Empty;
        [DataMember(Name = "messages")] public List<ChatCompletionMessage> Messages = new List<ChatCompletionMessage>();
        [DataMember(Name = "stream")] public bool Stream = true;
        [DataMember(Name = "max_tokens", EmitDefaultValue = false)] public int? MaximumTokens;
        [DataMember(Name = "temperature", EmitDefaultValue = false)] public float? Temperature;
        [DataMember(Name = "top_p", EmitDefaultValue = false)] public float? TopP;
        [DataMember(Name = "seed", EmitDefaultValue = false)] public int? Seed;
    }
    [DataContract] internal sealed class ChatCompletionMessage
    {
        [DataMember(Name = "role")] public string Role = string.Empty;
        [DataMember(Name = "content")] public string Content = string.Empty;
    }
    [DataContract] internal sealed class ChatCompletionChunk
    {
        [DataMember(Name = "choices")] public List<ChatCompletionChoice>? Choices;
        [DataMember(Name = "usage")] public ChatCompletionUsage? Usage;
    }
    [DataContract] internal sealed class ChatCompletionChoice
    {
        [DataMember(Name = "delta")] public ChatCompletionDelta? Delta;
        [DataMember(Name = "finish_reason")] public string? FinishReason;
    }
    [DataContract] internal sealed class ChatCompletionDelta { [DataMember(Name = "content")] public string? Content; }
    [DataContract] internal sealed class ChatCompletionUsage
    {
        [DataMember(Name = "prompt_tokens")] public long? PromptTokens;
        [DataMember(Name = "completion_tokens")] public long? CompletionTokens;
    }
#pragma warning restore CS0649
}
