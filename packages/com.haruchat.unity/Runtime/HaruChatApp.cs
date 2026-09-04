#nullable enable
using HaruChat.Runtime.Models;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using TMPro;
using UnityEngine.UI;

namespace HaruChat.Unity
{
    /// <summary>Scene-wired iPad client. UI hierarchy and visual layout live in SampleScene.</summary>
    public sealed class HaruChatApp : MonoBehaviour
    {
        [Header("Scene controls")]
        [SerializeField] private TMP_InputField _composer = null!;
        [SerializeField] private TextMeshProUGUI _status = null!;
        [SerializeField] private TextMeshProUGUI _diagnostics = null!;
        [SerializeField] private Button _characterButton = null!;
        [SerializeField] private Button _addCharacterButton = null!;
        [SerializeField] private Button _importModelButton = null!;
        [SerializeField] private Button _previewButton = null!;
        [SerializeField] private Button _unloadButton = null!;
        [SerializeField] private Button _newConversationButton = null!;
        [SerializeField] private Button _cancelButton = null!;
        [SerializeField] private Button _sendButton = null!;
        [SerializeField] private Button _drawerToggleButton = null!;
        [SerializeField] private HaruChatMessageList _messages = null!;
        [SerializeField] private HaruChatResponsiveLayout _responsiveLayout = null!;

        private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private HaruChatCompositionRoot? _root;
        private bool _pickerForCharacter;
        private int _characterIndex;

        private void Awake()
        {
            ValidateSceneReferences();
            BindControls();
            _root = new HaruChatCompositionRoot();
            try
            {
                var stream = Path.Combine(Application.streamingAssetsPath, "HaruChat");
                var imported = Path.Combine(Application.persistentDataPath, "HaruChat", "Characters");
                _root.Configure(Path.Combine(stream, "Characters"), imported, Path.Combine(stream, "Profiles", "qwen35.json"));
                PopulateCharacters();
                AddMessage("system", "모델을 가져온 뒤 캐릭터에게 말을 걸어 보세요.");
                RenderState();
            }
            catch (Exception error) { ShowError(error); }
#if !UNITY_EDITOR
            _previewButton.gameObject.SetActive(false);
#endif
        }

        private void Update()
        {
            while (_mainThread.TryDequeue(out var work)) work();
            if (_pickerForCharacter || (_root != null && _root.State == HaruChatClientState.Importing)) PollPicker();
        }

        private void OnDestroy() { _lifetime.Cancel(); _ = _root?.DisposeAsync(); }

        private void BindControls()
        {
            _characterButton.onClick.AddListener(NextCharacter); _addCharacterButton.onClick.AddListener(ImportCharacter);
            _importModelButton.onClick.AddListener(ImportModel); _previewButton.onClick.AddListener(LoadPreview);
            _unloadButton.onClick.AddListener(Unload); _newConversationButton.onClick.AddListener(NewConversation);
            _cancelButton.onClick.AddListener(Cancel); _sendButton.onClick.AddListener(Send);
            _drawerToggleButton.onClick.AddListener(_responsiveLayout.ToggleDrawer);
        }

        private void ImportModel() { if (_root == null) return; _pickerForCharacter = false; HaruChatDocumentPicker.Open(); SetStatus("Files에서 GGUF를 선택하세요."); }
        private void ImportCharacter() { if (_root == null) return; _pickerForCharacter = true; HaruChatDocumentPicker.OpenCharacter(); SetStatus("Files에서 character bundle 폴더를 선택하세요."); }
        private void PollPicker()
        {
            var result = HaruChatDocumentPicker.Poll(out var path); if (result == HaruChatPickerResult.Pending) return;
            var character = _pickerForCharacter; _pickerForCharacter = false;
            if (result == HaruChatPickerResult.Cancelled) { SetStatus("파일 선택을 취소했습니다."); return; }
            if (result != HaruChatPickerResult.Selected || string.IsNullOrWhiteSpace(path)) { SetStatus(Application.isEditor ? "iOS Files 선택기는 iPad player에서 사용할 수 있습니다." : "파일을 가져오지 못했습니다."); return; }
            if (character) Run(async () => { await _root!.ImportCharacterAsync(path!, _lifetime.Token); PopulateCharacters(); }); else Run(() => _root!.LoadAsync(path!, _lifetime.Token));
        }
        private void LoadPreview() { Run(() => _root!.LoadPreviewAsync(_lifetime.Token)); }
        private void Unload() { Run(() => _root!.UnloadAsync(_lifetime.Token)); }
        private void NewConversation()
        {
            Run(() => _root!.NewConversationAsync(_lifetime.Token)); _messages.Clear(); AddMessage("system", "새 대화를 시작했습니다.");
        }
        private void Cancel() { _root?.CancelGeneration(); RenderState(); }
        private void NextCharacter()
        {
            if (_root == null || _root.Characters.Characters.Count == 0) return;
            _characterIndex = (_characterIndex + 1) % _root.Characters.Characters.Count; var item = _root.Characters.Characters[_characterIndex];
            SetCharacterButton(item.DisplayName); Run(() => _root.SelectCharacterAsync(item.Id, _lifetime.Token));
        }
        private void Send()
        {
            if (_root == null || string.IsNullOrWhiteSpace(_composer.text)) return;
            var input = _composer.text; _composer.text = string.Empty; AddMessage("user", input); HaruChatMessageView? reply = null;
            Run(() => _root.SendAsync(input, item => _mainThread.Enqueue(() =>
            {
                if (item.Kind == ModelEventKind.Token) { if (reply == null) reply = AddMessage("assistant", string.Empty); reply.Append(item.Text ?? string.Empty); _messages.ScrollToEnd(); }
                if (item.Kind == ModelEventKind.Error) AddMessage("system", item.Error?.Message ?? "알 수 없는 오류");
            }), _lifetime.Token));
        }
        private void Run(Func<Task> action) { _ = RunInner(action); }
        private async Task RunInner(Func<Task> action) { try { await action(); await UpdateDiagnostics(); RenderState(); } catch (Exception error) { ShowError(error); } }
        private async Task UpdateDiagnostics()
        {
            if (_root == null) return; var d = await _root.GetDiagnosticsAsync(_lifetime.Token); var context = await _root.GetContextWindowStatusAsync(_lifetime.Token);
            var contextText = context == null ? "—" : context.UsedTokens + " / " + context.PromptBudgetTokens + " (출력 예약 " + context.OutputReserveTokens + ", 여유 " + context.RemainingTokens + ")" + (context.ActualTokenizerPromptTokens.HasValue ? " · tokenizer 실측" : " · 추정") + (context.SummaryEstimatedTokens > 0 ? " · 요약 " + context.SummaryEstimatedTokens : string.Empty) + (context.CompactedCompletedTurns > 0 ? " · 압축 " + context.CompactedCompletedTurns + " turn" : string.Empty) + (context.ExcludedCompletedTurns > 0 ? " · 이전 turn " + context.ExcludedCompletedTurns + "개 제외" : string.Empty);
            _diagnostics.text = d == null ? "Backend: —\nMetal: —\nContext: " + contextText : "Backend: " + d.Backend + "\nMetal: " + (d.AccelerationEnabled.HasValue ? (d.AccelerationEnabled.Value ? "활성" : "비활성") : "unknown") + "\nContext: " + contextText + "\nLoad: " + (d.LoadDuration?.TotalSeconds.ToString("0.0") ?? "—") + "s";
        }
        private void PopulateCharacters() { if (_root == null || _root.Characters.Characters.Count == 0) return; _characterIndex = 0; SetCharacterButton(_root.Characters.Characters[0].DisplayName); }
        private void SetCharacterButton(string name) { var label = _characterButton.GetComponentInChildren<TextMeshProUGUI>(true); if (label != null) label.text = "캐릭터: " + name; }
        private void RenderState() { if (_root != null) SetStatus(_root.Status); }
        private void ShowError(Exception error) { SetStatus(error.Message); }
        private void SetStatus(string value) { _status.text = "● " + value; }
        private HaruChatMessageView AddMessage(string role, string text)
        {
            return _messages.Add(role, text);
        }
        private void ValidateSceneReferences()
        {
            if (_composer == null || _status == null || _diagnostics == null || _characterButton == null || _addCharacterButton == null || _importModelButton == null || _previewButton == null || _unloadButton == null || _newConversationButton == null || _cancelButton == null || _sendButton == null || _drawerToggleButton == null || _messages == null || _responsiveLayout == null) throw new InvalidOperationException("SampleScene의 HaruChatApp 참조가 연결되지 않았습니다.");
        }
    }
}
