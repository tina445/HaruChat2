#nullable enable
using HaruChat.LlamaCpp;
using HaruChat.Runtime.Characters;
using HaruChat.Runtime.Models;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HaruChat.Unity
{
    public enum HaruChatClientState { NoModel, Importing, Loading, Ready, Generating, Cancelling, Error }

    /// <summary>Unity-free ownership boundary for the foreground model and character session.</summary>
    public sealed class HaruChatCompositionRoot : IAsyncDisposable
    {
        private CharacterCatalog? _characters; private CharacterDefinition? _selectedCharacter; private ModelProfile? _profile; private ModelProfileCatalog? _profiles;
        private string? _bundledCharactersDirectory; private string? _importedCharactersDirectory; private string? _profilePath;
        private LlamaCppBackend? _backend; private IModelSession? _session; private CharacterChatService? _chat; private CancellationTokenSource? _generation;
        public HaruChatClientState State { get; private set; } = HaruChatClientState.NoModel;
        public string Status { get; private set; } = "모델을 가져와 시작하세요.";
        public string? ModelPath { get; private set; }
        public CharacterCatalog Characters { get { return _characters ?? throw new InvalidOperationException("Characters are not configured."); } }
        public CharacterDefinition SelectedCharacter { get { return _selectedCharacter ?? throw new InvalidOperationException("Character is not selected."); } }
        public event Action? Changed;

        public void Configure(string bundledCharactersDirectory, string importedCharactersDirectory, string profilePath)
        {
            _bundledCharactersDirectory = bundledCharactersDirectory; _importedCharactersDirectory = importedCharactersDirectory; _profilePath = profilePath;
            Directory.CreateDirectory(importedCharactersDirectory); ReloadCharacters();
            _selectedCharacter = _characters.Characters.First();
            _profile = ModelProfileLoader.Load(profilePath);
            var profileDirectory = Path.GetDirectoryName(Path.GetFullPath(profilePath))!;
            _profiles = new ModelProfileCatalog(Directory.GetFiles(profileDirectory, "*.json").Select(ModelProfileLoader.Load));
            Notify();
        }

        public async Task ImportCharacterAsync(string sourceDirectory, CancellationToken cancellationToken)
        {
            if (_importedCharactersDirectory == null) throw new InvalidOperationException("HaruChat is not configured.");
            Set(HaruChatClientState.Importing, "캐릭터 bundle을 확인하는 중…");
            var definition = await Task.Run(() => new CharacterBundleLoader().Load(sourceDirectory), cancellationToken).ConfigureAwait(false);
            var target = Path.Combine(_importedCharactersDirectory, definition.Id);
            if (Directory.Exists(target)) throw new CharacterValidationException("같은 ID의 캐릭터가 이미 있습니다: " + definition.Id);
            await Task.Run(() => CopyDirectory(sourceDirectory, target), cancellationToken).ConfigureAwait(false);
            try { ReloadCharacters(); _selectedCharacter = Characters.Get(definition.Id); Set(HaruChatClientState.NoModel, definition.DisplayName + "을(를) 추가했습니다. 모델을 불러오세요."); }
            catch { Directory.Delete(target, true); throw; }
        }

        public async Task SelectCharacterAsync(string id, CancellationToken cancellationToken)
        {
            var character = Characters.Get(id); if (_selectedCharacter != null && _selectedCharacter.Id == character.Id) return;
            await UnloadAsync(cancellationToken).ConfigureAwait(false); _selectedCharacter = character; Set(HaruChatClientState.NoModel, character.DisplayName + "을 선택했습니다. 모델을 불러오세요.");
        }

        public async Task LoadAsync(string modelPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath)) throw new FileNotFoundException("선택한 GGUF 파일을 찾을 수 없습니다.", modelPath);
            if (_profile == null || _selectedCharacter == null) throw new InvalidOperationException("HaruChat is not configured.");
            await UnloadAsync(CancellationToken.None).ConfigureAwait(false);
            Set(HaruChatClientState.Loading, "로컬 모델을 불러오는 중…");
            var backend = new LlamaCppBackend();
            try
            {
                var adapter = new LocalModelAdapter("local", backend, new ModelConfig(modelPath), _profiles!);
                var session = await adapter.CreateSessionAsync(new ModelSessionOptions(_profile.ContextWindowTokens), cancellationToken).ConfigureAwait(false);
                var selectedProfile = adapter.SelectedProfile ?? throw new InvalidOperationException("No model profile was selected.");
                _backend = backend; _session = session; _chat = new CharacterChatService(_selectedCharacter, new Conversation(), session, new PromptCompiler(), selectedProfile.ContextWindowTokens, maximumOutputTokens: selectedProfile.Defaults.MaximumOutputTokens);
                ModelPath = modelPath; Set(HaruChatClientState.Ready, Path.GetFileName(modelPath) + " 준비됨");
            }
            catch { await backend.DisposeAsync().ConfigureAwait(false); Set(HaruChatClientState.Error, "모델을 불러오지 못했습니다."); throw; }
        }

        public async Task LoadPreviewAsync(CancellationToken cancellationToken)
        {
            if (_profile == null || _selectedCharacter == null) throw new InvalidOperationException("HaruChat is not configured.");
            await UnloadAsync(CancellationToken.None).ConfigureAwait(false); Set(HaruChatClientState.Loading, "Editor 미리보기를 준비하는 중…");
            var adapter = new MockModelAdapter("preview", _ => new[] { ModelEvent.Token("지금은 Unity Editor 미리보기예요. iPad에서 GGUF를 선택하면 Haru로 응답할게요."), ModelEvent.Completed() });
            _session = await adapter.CreateSessionAsync(new ModelSessionOptions(_profile.ContextWindowTokens), cancellationToken).ConfigureAwait(false);
            _chat = new CharacterChatService(_selectedCharacter, new Conversation(), _session, new PromptCompiler(), _profile.ContextWindowTokens, maximumOutputTokens: _profile.Defaults.MaximumOutputTokens);
            ModelPath = "Editor preview"; Set(HaruChatClientState.Ready, "Editor 미리보기 준비됨");
        }

        public async Task SendAsync(string input, Action<ModelEvent> onEvent, CancellationToken cancellationToken)
        {
            if (_chat == null) throw new InvalidOperationException("먼저 모델을 불러오세요.");
            if (string.IsNullOrWhiteSpace(input)) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); _generation = linked; Set(HaruChatClientState.Generating, "Haru가 답하는 중…");
            try { await foreach (var item in _chat.SendAsync(input.Trim(), linked.Token).ConfigureAwait(false)) onEvent(item); Set(HaruChatClientState.Ready, "준비됨"); }
            catch (OperationCanceledException) { Set(HaruChatClientState.Ready, "응답을 취소했습니다."); }
            catch { Set(HaruChatClientState.Error, "응답을 생성하지 못했습니다."); throw; }
            finally { _generation = null; }
        }

        public void CancelGeneration() { if (_generation == null) return; Set(HaruChatClientState.Cancelling, "응답을 멈추는 중…"); _generation.Cancel(); }
        public async Task NewConversationAsync(CancellationToken cancellationToken) { if (_chat == null) return; await _chat.NewConversationAsync(cancellationToken).ConfigureAwait(false); Set(HaruChatClientState.Ready, "새 대화를 시작했습니다."); }
        public async Task<ModelDiagnostics?> GetDiagnosticsAsync(CancellationToken cancellationToken) { return _session == null ? null : await _session.GetDiagnosticsAsync(cancellationToken).ConfigureAwait(false); }
        public async Task<ContextWindowStatus?> GetContextWindowStatusAsync(CancellationToken cancellationToken) { return _chat == null ? null : await _chat.GetContextWindowStatusAsync(cancellationToken).ConfigureAwait(false); }
        public async Task UnloadAsync(CancellationToken cancellationToken)
        {
            _generation?.Cancel(); var chat = _chat; _chat = null; _session = null; ModelPath = null;
            if (chat != null) await chat.DisposeAsync().ConfigureAwait(false);
            if (_backend != null) { await _backend.DisposeAsync().ConfigureAwait(false); _backend = null; }
            if (State != HaruChatClientState.NoModel) Set(HaruChatClientState.NoModel, "모델을 가져와 시작하세요.");
        }
        public async ValueTask DisposeAsync() { await UnloadAsync(CancellationToken.None).ConfigureAwait(false); }
        private void ReloadCharacters()
        {
            if (_bundledCharactersDirectory == null || _importedCharactersDirectory == null) throw new InvalidOperationException("HaruChat is not configured.");
            var roots = Directory.GetDirectories(_bundledCharactersDirectory).Concat(Directory.GetDirectories(_importedCharactersDirectory)).ToArray();
            _characters = CharacterCatalog.Load(roots);
        }
        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            foreach (var directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
        private void Set(HaruChatClientState state, string status) { State = state; Status = status; Notify(); }
        private void Notify() { Changed?.Invoke(); }
    }
}
