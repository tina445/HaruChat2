#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HaruChat.Runtime.Models;

namespace HaruChat.OpenAI
{
    /// <summary>OpenAI chat-completions-compatible streaming adapter. It never logs prompts or credentials.</summary>
    public sealed class OpenAiCompatibleAdapter : IModelAdapter, IDisposable
    {
        private readonly OpenAiCompatibleProviderConfiguration _configuration;
        private readonly ISecureApiKeyStore _keys;
        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        public OpenAiCompatibleAdapter(OpenAiCompatibleProviderConfiguration configuration, ISecureApiKeyStore keys, HttpClient? httpClient = null)
        { _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration)); _keys = keys ?? throw new ArgumentNullException(nameof(keys)); _httpClient = httpClient ?? new HttpClient(); _ownsHttpClient = httpClient == null; }
        public string Id { get { return _configuration.Id; } }
        public ModelCapabilities Capabilities { get { return new ModelCapabilities(streaming: true, cancellation: true, tools: false); } }
        public Task<IModelSession> CreateSessionAsync(ModelSessionOptions options, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult<IModelSession>(new Session(_configuration, _keys, _httpClient, options)); }
        public void Dispose() { if (_ownsHttpClient) _httpClient.Dispose(); }

        private sealed class Session : IModelSession
        {
            private readonly OpenAiCompatibleProviderConfiguration _configuration; private readonly ISecureApiKeyStore _keys; private readonly HttpClient _http; private readonly ModelSessionOptions _options; private readonly SemaphoreSlim _generationGate = new SemaphoreSlim(1, 1);
            private ModelUsage _usage = new ModelUsage(null, null, null); private bool _disposed;
            public Session(OpenAiCompatibleProviderConfiguration configuration, ISecureApiKeyStore keys, HttpClient http, ModelSessionOptions options) { _configuration = configuration; _keys = keys; _http = http; _options = options; }
            public async IAsyncEnumerable<ModelEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(Session));
                if (request == null) throw new ArgumentNullException(nameof(request));
                if (!_configuration.RemoteTransmissionOptedIn) { yield return Error(ModelErrorCode.InvalidConfiguration, "Remote model transmission has not been enabled.", false); yield break; }
                await _generationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var key = await _keys.GetApiKeyAsync(_configuration.ApiKeyReference, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(key)) { yield return Error(ModelErrorCode.InvalidConfiguration, "No API key is available for the selected remote provider.", false); yield break; }
                    var watch = Stopwatch.StartNew();
                    var opened = await OpenAsync(_http, _configuration.Endpoint, key, Serialize(OpenAiRequestProjection.From(request, _configuration.Model)), cancellationToken).ConfigureAwait(false);
                    if (opened.Error != null) { yield return opened.Error; yield break; }
                    using var remote = opened.Response!;
                    var completed = false;
                    while (!remote.Reader.EndOfStream)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); var read = await ReadSseDataAsync(remote.Reader).ConfigureAwait(false);
                        if (read.Error != null) { yield return read.Error; yield break; }
                        if (read.Data == null) continue;
                        var data = read.Data;
                        if (data == "[DONE]") { if (!completed) yield return ModelEvent.Completed(); yield break; }
                        var parsed = ParseChunk(data);
                        if (parsed.Error != null) { yield return parsed.Error; yield break; }
                        var chunk = parsed.Chunk!;
                        if (chunk.Usage != null) { _usage = new ModelUsage(chunk.Usage.PromptTokens, chunk.Usage.CompletionTokens, watch.Elapsed); yield return ModelEvent.UsageSnapshot(_usage); }
                        if (chunk.Choices == null) continue;
                        foreach (var choice in chunk.Choices)
                        {
                            if (!string.IsNullOrEmpty(choice.Delta?.Content)) yield return ModelEvent.Token(choice.Delta.Content);
                            if (!string.IsNullOrEmpty(choice.FinishReason)) { completed = true; yield return ModelEvent.Completed(MapStop(choice.FinishReason)); }
                        }
                    }
                    if (!completed) yield return Error(ModelErrorCode.BackendFailure, "The remote provider ended the stream without a completion marker.", true);
                }
                finally { _generationGate.Release(); }
            }
            public Task ResetAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
            public Task<ModelUsage> GetUsageAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(_usage); }
            public Task<ModelDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(new ModelDiagnostics("openai-compatible-http", null, _options.ContextWindowTokens, null, null, null, null)); }
            // Do not dispose the gate here: an enumerator may still be unwinding its finally block.
            public ValueTask DisposeAsync() { _disposed = true; return default; }
            private static ModelEvent Error(ModelErrorCode code, string message, bool recoverable) { return ModelEvent.ErrorEvent(new ModelError(code, message, recoverable)); }
            private static ModelErrorCode Map(HttpStatusCode status) { if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden) return ModelErrorCode.InvalidConfiguration; if (status == HttpStatusCode.NotFound) return ModelErrorCode.NotFound; if (status == HttpStatusCode.TooManyRequests) return ModelErrorCode.Busy; if ((int)status >= 400 && (int)status < 500) return ModelErrorCode.InvalidRequest; return ModelErrorCode.BackendFailure; }
            private static ModelStopReason MapStop(string value) { return string.Equals(value, "length", StringComparison.OrdinalIgnoreCase) ? ModelStopReason.Length : string.Equals(value, "tool_calls", StringComparison.OrdinalIgnoreCase) ? ModelStopReason.ToolCall : ModelStopReason.Stop; }
            private static string Serialize(OpenAiRequestProjection projection)
            {
                var result = new ChatCompletionRequest { Model = projection.Model };
                foreach (var message in projection.Messages) result.Messages.Add(new ChatCompletionMessage { Role = Role(message.Role), Content = message.Text });
                if (projection.Generation != null) { result.MaximumTokens = projection.Generation.MaximumOutputTokens; result.Temperature = projection.Generation.Temperature; result.TopP = projection.Generation.TopP; result.Seed = projection.Generation.Seed; }
                using var stream = new MemoryStream(); new DataContractJsonSerializer(typeof(ChatCompletionRequest)).WriteObject(stream, result); return Encoding.UTF8.GetString(stream.ToArray());
            }
            private static ChatCompletionChunk Deserialize(string json) { using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)); return (ChatCompletionChunk)new DataContractJsonSerializer(typeof(ChatCompletionChunk)).ReadObject(stream)!; }
            private static string Role(ModelRole role) { return role == ModelRole.System ? "system" : role == ModelRole.User ? "user" : role == ModelRole.Assistant ? "assistant" : "tool"; }
            private static async Task<OpenResult> OpenAsync(HttpClient http, Uri endpoint, string key, string payload, CancellationToken cancellationToken)
            {
                HttpRequestMessage? message = null;
                try
                {
                    message = new HttpRequestMessage(HttpMethod.Post, endpoint);
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key); message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                    message.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) { var error = Error(Map(response.StatusCode), "The remote provider rejected the request.", response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500); response.Dispose(); message.Dispose(); return new OpenResult(null, error); }
                    var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false); return new OpenResult(new RemoteResponse(message, response, stream, new StreamReader(stream, Encoding.UTF8, true)), null);
                }
                catch (OperationCanceledException) { message?.Dispose(); throw; }
                catch (HttpRequestException) { message?.Dispose(); return new OpenResult(null, Error(ModelErrorCode.BackendFailure, "The remote provider could not be reached.", true)); }
            }
            private static async Task<SseReadResult> ReadSseDataAsync(StreamReader reader)
            {
                try { var line = await reader.ReadLineAsync().ConfigureAwait(false); return line == null || !line.StartsWith("data:", StringComparison.Ordinal) ? new SseReadResult(null, null) : new SseReadResult(line.Substring(5).TrimStart(), null); }
                catch (IOException) { return new SseReadResult(null, Error(ModelErrorCode.BackendFailure, "The remote provider stream was interrupted.", true)); }
            }
            private static ChunkResult ParseChunk(string data)
            {
                try { return new ChunkResult(Deserialize(data), null); }
                catch { return new ChunkResult(null, Error(ModelErrorCode.BackendFailure, "The remote provider returned an invalid streaming event.", true)); }
            }
            private sealed class OpenResult { public OpenResult(RemoteResponse? response, ModelEvent? error) { Response = response; Error = error; } public RemoteResponse? Response { get; } public ModelEvent? Error { get; } }
            private sealed class SseReadResult { public SseReadResult(string? data, ModelEvent? error) { Data = data; Error = error; } public string? Data { get; } public ModelEvent? Error { get; } }
            private sealed class ChunkResult { public ChunkResult(ChatCompletionChunk? chunk, ModelEvent? error) { Chunk = chunk; Error = error; } public ChatCompletionChunk? Chunk { get; } public ModelEvent? Error { get; } }
            private sealed class RemoteResponse : IDisposable
            {
                private readonly HttpRequestMessage _message; private readonly HttpResponseMessage _response; private readonly Stream _stream;
                public RemoteResponse(HttpRequestMessage message, HttpResponseMessage response, Stream stream, StreamReader reader) { _message = message; _response = response; _stream = stream; Reader = reader; }
                public StreamReader Reader { get; }
                public void Dispose() { Reader.Dispose(); _stream.Dispose(); _response.Dispose(); _message.Dispose(); }
            }
        }
    }
}
