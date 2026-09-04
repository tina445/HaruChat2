import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:hc_llm_flutter/hc_llm_flutter.dart';

import 'character_bundle_store.dart';

void main() {
  WidgetsFlutterBinding.ensureInitialized();
  unawaited(SystemChrome.setEnabledSystemUIMode(SystemUiMode.manual,
      overlays: const []));
  runApp(const HaruChatNativeProbeApp());
}

/// Flutter test host mirroring the Phase 6 Unity screen. Native lifecycle
/// calls remain real; this app does not replace the Unity/M4 MVP gate.
class HaruChatNativeProbeApp extends StatelessWidget {
  const HaruChatNativeProbeApp({super.key});

  @override
  Widget build(BuildContext context) => MaterialApp(
        title: 'HaruChat P6 UI Harness',
        debugShowCheckedModeBanner: false,
        builder: (context, child) => _KeyboardDismissOnTap(
          child: child ?? const SizedBox.shrink(),
        ),
        theme: ThemeData(
          useMaterial3: true,
          brightness: Brightness.dark,
          scaffoldBackgroundColor: const Color(0xff101818),
          colorScheme: const ColorScheme.dark(
            primary: Color(0xff91e7cf),
            secondary: Color(0xfff7c77a),
            surface: Color(0xff182423),
          ),
        ),
        home: const NativeProbePage(),
      );
}

/// Clears text focus only when a tap lands outside the active input. Using a
/// [Listener] keeps buttons and scrollables responsive while covering dialogs,
/// drawers, and the primary chat surface with the same iOS keyboard behavior.
class _KeyboardDismissOnTap extends StatelessWidget {
  const _KeyboardDismissOnTap({required this.child});

  final Widget child;

  void _dismissIfOutsideFocusedInput(PointerDownEvent event) {
    final focus = FocusManager.instance.primaryFocus;
    final renderObject = focus?.context?.findRenderObject();
    if (renderObject is RenderBox && renderObject.hasSize) {
      final focusedBounds =
          renderObject.localToGlobal(Offset.zero) & renderObject.size;
      if (focusedBounds.contains(event.position)) return;
    }
    focus?.unfocus();
  }

  @override
  Widget build(BuildContext context) => Listener(
        behavior: HitTestBehavior.translucent,
        onPointerDown: _dismissIfOutsideFocusedInput,
        child: child,
      );
}

class _ChatMessage {
  _ChatMessage(this.role, this.text);
  final String role;
  String text;
}

class _MemoryNote {
  const _MemoryNote({required this.text, required this.importance});
  final String text;
  final int importance;
}

class _MemoryAtelierResult {
  const _MemoryAtelierResult({
    required this.enabled,
    required this.retentionDays,
    required this.maxRetrieved,
    required this.contextTokenBudget,
    required this.contextWindowTokens,
    required this.temperature,
    required this.notes,
  });
  final bool enabled;
  final int? retentionDays;
  final int maxRetrieved;
  final int contextTokenBudget;
  final int contextWindowTokens;
  final double temperature;
  final List<_MemoryNote> notes;
}

class NativeProbePage extends StatefulWidget {
  const NativeProbePage({super.key});
  @override
  State<NativeProbePage> createState() => _NativeProbePageState();
}

class _NativeProbePageState extends State<NativeProbePage> {
  static const _railMinimumWidth = 960.0;
  static const _compactHeight = 680.0;
  final _modelPath = TextEditingController();
  final _composer = TextEditingController();
  final _log = <String>[];
  final _messages = <_ChatMessage>[
    _ChatMessage('system', '모델을 가져온 뒤 캐릭터에게 말을 걸어 보세요.'),
  ];
  StreamSubscription<NativeProbeEvent>? _events;
  CharacterBundleStore? _characterStore;
  List<CharacterBundleSummary> _characters = const [];
  CharacterBundleSummary? _selectedCharacter;
  _ChatMessage? _reply;
  String _status = '모델을 불러오지 않았습니다.';
  bool _generating = false;
  bool _modelLoaded = false;
  bool _memoryEnabled = false;
  int? _memoryRetentionDays;
  int _maxRetrievedMemories = 3;
  int _memoryContextTokenBudget = 256;
  int _contextWindowTokens = 8192;
  double _temperature = 0.7;
  List<_MemoryNote> _memoryNotes = const [];

  @override
  void initState() {
    super.initState();
    _events = NativeProbe.events.listen(_onEvent, onError: (Object error) {
      _setStatus('Native event stream failed: $error');
    });
    unawaited(_refreshCharacters());
  }

  @override
  void dispose() {
    _events?.cancel();
    _modelPath.dispose();
    _composer.dispose();
    super.dispose();
  }

  void _onEvent(NativeProbeEvent event) {
    if (!mounted) return;
    setState(() {
      if (event.status != null) _status = event.status!;
      if (event.logLine != null) _log.add(event.logLine!);
      if (event.token != null) {
        _reply ??= _ChatMessage('assistant', '');
        if (!_messages.contains(_reply)) _messages.add(_reply!);
        _reply!.text += event.token!;
      }
      if (event.isTerminal) _generating = false;
    });
  }

  void _setStatus(String value) {
    if (mounted) setState(() => _status = value);
  }

  Future<void> _chooseModel() async {
    final path = await NativeProbe.chooseModel();
    if (path != null && mounted) setState(() => _modelPath.text = path);
  }

  Future<void> _load() async {
    if (_modelPath.text.trim().isEmpty) {
      _setStatus('Files에서 GGUF를 선택하세요.');
      return;
    }
    _setStatus('모델을 준비하고 있습니다…');
    final status = await NativeProbe.load(_modelPath.text,
        contextWindowTokens: _contextWindowTokens);
    if (mounted) {
      setState(() {
        _status = status;
        _modelLoaded = !status.toLowerCase().contains('failed');
      });
    }
  }

  Future<void> _send() async {
    final input = _composer.text.trim();
    if (input.isEmpty || !_modelLoaded || _generating) return;
    setState(() {
      _composer.clear();
      _messages.add(_ChatMessage('user', input));
      _reply = _ChatMessage('assistant', '');
      _messages.add(_reply!);
      _log.clear();
      _generating = true;
      _status = '응답을 스트리밍하고 있습니다…';
    });
    _setStatus(await NativeProbe.generate(_probePrompt(input),
        maximumOutputTokens: 2048));
  }

  String _probePrompt(String user) {
    final system = _selectedCharacter?.promptContext ??
        'You are a concise, helpful assistant.';
    return '<|im_start|>system\n$system<|im_end|>\n'
        '<|im_start|>user\n$user<|im_end|>\n'
        '<|im_start|>assistant\n<think>\n\n</think>\n\n';
  }

  Future<void> _cancel() async {
    _setStatus(await NativeProbe.cancel());
    if (mounted) setState(() => _generating = false);
  }

  Future<void> _unload() async {
    final status = await NativeProbe.unload();
    if (mounted) {
      setState(() {
        _status = status;
        _modelLoaded = false;
        _generating = false;
      });
    }
  }

  Future<void> _newConversation() async {
    final status = await NativeProbe.reset();
    if (mounted) {
      setState(() {
        _status = status;
        _messages
          ..clear()
          ..add(_ChatMessage('system', '새 대화를 시작했습니다.'));
        _reply = null;
      });
    }
  }

  Future<void> _refreshCharacters({String? selectId}) async {
    try {
      _characterStore ??= await CharacterBundleStore.openDefault();
      final characters = await _characterStore!.list();
      final selectedId = selectId ?? _selectedCharacter?.id;
      CharacterBundleSummary? canonicalSelection;
      if (selectedId != null) {
        for (final character in characters) {
          if (character.id == selectedId) {
            canonicalSelection = character;
            break;
          }
        }
      }
      if (mounted) {
        setState(() {
          _characters = characters;
          _selectedCharacter = canonicalSelection;
        });
      }
    } catch (error) {
      _setStatus('Character storage unavailable: $error');
    }
  }

  Future<void> _addCharacter() async {
    final draft = await showDialog<CharacterBundleDraft>(
      context: context,
      builder: (_) => const _CharacterDraftDialog(),
    );
    if (draft == null) return;
    try {
      _characterStore ??= await CharacterBundleStore.openDefault();
      final created = await _characterStore!.create(draft);
      await _refreshCharacters(selectId: created.id);
    } catch (error) {
      _setStatus('Character creation failed: $error');
    }
  }

  Future<void> _editCharacter() async {
    final existing = _selectedCharacter;
    if (existing == null) return;
    final draft = await showDialog<CharacterBundleDraft>(
      context: context,
      builder: (_) => _CharacterDraftDialog(existing: existing),
    );
    if (draft == null) return;
    try {
      _characterStore ??= await CharacterBundleStore.openDefault();
      final updated = await _characterStore!.update(existing, draft);
      await _refreshCharacters(selectId: updated.id);
    } catch (error) {
      _setStatus('Character update failed: $error');
    }
  }

  Future<void> _openMemoryAtelier() async {
    final result = await showDialog<_MemoryAtelierResult>(
      context: context,
      builder: (_) => _MemoryAtelierDialog(
        enabled: _memoryEnabled,
        retentionDays: _memoryRetentionDays,
        maxRetrieved: _maxRetrievedMemories,
        contextTokenBudget: _memoryContextTokenBudget,
        contextWindowTokens: _contextWindowTokens,
        temperature: _temperature,
        modelPath: _modelPath.text,
        modelLoaded: _modelLoaded,
        characterInstructionTokens:
            (_selectedCharacter?.promptContext.length ?? 0) ~/ 4,
        notes: _memoryNotes,
      ),
    );
    if (result == null || !mounted) return;
    setState(() {
      _memoryEnabled = result.enabled;
      _memoryRetentionDays = result.retentionDays;
      _maxRetrievedMemories = result.maxRetrieved;
      _memoryContextTokenBudget = result.contextTokenBudget;
      _contextWindowTokens = result.contextWindowTokens;
      _temperature = result.temperature;
      _memoryNotes = result.notes;
    });
  }

  @override
  Widget build(BuildContext context) => LayoutBuilder(
        builder: (context, constraints) {
          final compact = constraints.maxWidth < _railMinimumWidth ||
              constraints.maxHeight < _compactHeight;
          final keyboardVisible = MediaQuery.viewInsetsOf(context).bottom > 0;
          final rail = _ControlRail(
            modelPath: _modelPath,
            characters: _characters,
            selected: _selectedCharacter,
            loaded: _modelLoaded,
            generating: _generating,
            onCharacterChanged: (value) =>
                setState(() => _selectedCharacter = value),
            onAddCharacter: _addCharacter,
            onEditCharacter: _editCharacter,
            onRefreshCharacters: _refreshCharacters,
            onChooseModel: _chooseModel,
            onLoad: _load,
            onUnload: _unload,
            onNewConversation: _newConversation,
            onCancel: _cancel,
            memoryEnabled: _memoryEnabled,
            memoryNoteCount: _memoryNotes.length,
            onOpenMemoryAtelier: _openMemoryAtelier,
          );
          return Scaffold(
            resizeToAvoidBottomInset: true,
            appBar: AppBar(title: const _Masthead()),
            drawer: compact
                ? Drawer(
                    backgroundColor: const Color(0xff182423),
                    child: SafeArea(child: rail))
                : null,
            body: Row(children: [
              if (!compact) SizedBox(width: 344, child: rail),
              Expanded(
                  child: _Conversation(
                      messages: _messages,
                      composer: _composer,
                      status: _status,
                      loaded: _modelLoaded,
                      generating: _generating,
                      keyboardVisible: keyboardVisible,
                      memoryEnabled: _memoryEnabled,
                      memoryNoteCount: _memoryNotes.length,
                      memoryBudget: _memoryContextTokenBudget,
                      contextWindowTokens: _contextWindowTokens,
                      characterInstructionTokens:
                          (_selectedCharacter?.promptContext.length ?? 0) ~/ 4,
                      onOpenMemoryAtelier: _openMemoryAtelier,
                      onSend: _send)),
            ]),
          );
        },
      );
}

class _Masthead extends StatelessWidget {
  const _Masthead();
  @override
  Widget build(BuildContext context) => LayoutBuilder(
        builder: (context, constraints) => Row(children: [
          const Icon(Icons.auto_awesome, color: Color(0xff91e7cf)),
          const SizedBox(width: 10),
          const Expanded(
            child: Text('HARU / P6',
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style:
                    TextStyle(fontWeight: FontWeight.w800, letterSpacing: 1.2)),
          ),
          if (constraints.maxWidth >= 310) ...const [
            SizedBox(width: 10),
            Text('FLUTTER UI HARNESS',
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: TextStyle(
                    fontSize: 11,
                    letterSpacing: 1.2,
                    color: Color(0xffa7bcba))),
          ],
        ]),
      );
}

class _ControlRail extends StatelessWidget {
  const _ControlRail(
      {required this.modelPath,
      required this.characters,
      required this.selected,
      required this.loaded,
      required this.generating,
      required this.onCharacterChanged,
      required this.onAddCharacter,
      required this.onEditCharacter,
      required this.onRefreshCharacters,
      required this.onChooseModel,
      required this.onLoad,
      required this.onUnload,
      required this.onNewConversation,
      required this.onCancel,
      required this.memoryEnabled,
      required this.memoryNoteCount,
      required this.onOpenMemoryAtelier});
  final TextEditingController modelPath;
  final List<CharacterBundleSummary> characters;
  final CharacterBundleSummary? selected;
  final bool loaded, generating;
  final bool memoryEnabled;
  final int memoryNoteCount;
  final ValueChanged<CharacterBundleSummary?> onCharacterChanged;
  final Future<void> Function() onAddCharacter,
      onEditCharacter,
      onRefreshCharacters,
      onChooseModel,
      onLoad,
      onUnload,
      onNewConversation,
      onCancel,
      onOpenMemoryAtelier;
  @override
  Widget build(BuildContext context) =>
      ListView(padding: const EdgeInsets.all(16), children: [
        const Text('RUNTIME CONTROLS',
            style: TextStyle(
                letterSpacing: 1.5, fontSize: 11, color: Color(0xff91e7cf))),
        const SizedBox(height: 14),
        DropdownButtonFormField<CharacterBundleSummary?>(
            initialValue: selected,
            isExpanded: true,
            decoration: const InputDecoration(
                labelText: '캐릭터', border: OutlineInputBorder()),
            items: [
              const DropdownMenuItem(value: null, child: Text('기본 assistant')),
              ...characters.map((item) =>
                  DropdownMenuItem(value: item, child: Text(item.displayName)))
            ],
            onChanged: onCharacterChanged),
        const SizedBox(height: 8),
        _RailButton(
            icon: Icons.person_add_alt_1,
            label: 'character 추가',
            onTap: onAddCharacter),
        _RailButton(
            icon: Icons.edit_note,
            label: 'character 편집',
            onTap: selected == null ? null : onEditCharacter),
        _RailButton(
            icon: Icons.refresh, label: '새로고침', onTap: onRefreshCharacters),
        _RailButton(
            icon: Icons.auto_stories_outlined,
            label: memoryEnabled
                ? '기억 노트 · 설정 ($memoryNoteCount)'
                : '기억 노트 · 설정 (꺼짐)',
            onTap: onOpenMemoryAtelier),
        const Divider(height: 30),
        TextField(
            controller: modelPath,
            maxLines: 2,
            decoration: const InputDecoration(
                labelText: 'Imported GGUF path',
                hintText: 'Files에서 선택',
                border: OutlineInputBorder())),
        const SizedBox(height: 8),
        _RailButton(
            icon: Icons.folder_open,
            label: 'GGUF 모델 가져오기',
            onTap: onChooseModel),
        _RailButton(
            icon: Icons.play_circle_outline,
            label: '모델 로드',
            onTap: generating ? null : onLoad),
        _RailButton(
            icon: Icons.eject_outlined,
            label: '모델 언로드',
            onTap: loaded ? onUnload : null),
        const Divider(height: 30),
        _RailButton(
            icon: Icons.refresh_rounded,
            label: '새 대화',
            onTap: onNewConversation),
        _RailButton(
            icon: Icons.stop_circle_outlined,
            label: '응답 취소',
            onTap: generating ? onCancel : null),
        const SizedBox(height: 24),
        const _Diagnostics(),
      ]);
}

class _RailButton extends StatelessWidget {
  const _RailButton(
      {required this.icon, required this.label, required this.onTap});
  final IconData icon;
  final String label;
  final VoidCallback? onTap;
  @override
  Widget build(BuildContext context) => Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: OutlinedButton.icon(
          onPressed: onTap,
          icon: Icon(icon, size: 18),
          label: Align(alignment: Alignment.centerLeft, child: Text(label))));
}

class _Diagnostics extends StatelessWidget {
  const _Diagnostics();
  @override
  Widget build(BuildContext context) => const DecoratedBox(
      decoration: BoxDecoration(
          color: Color(0xff111c1b),
          borderRadius: BorderRadius.all(Radius.circular(12))),
      child: Padding(
          padding: EdgeInsets.all(14),
          child: Text(
              'DIAGNOSTICS\nBackend: native probe\nMetal: device-only\nContext: native reported\nLoad: native reported',
              style: TextStyle(
                  fontSize: 12, height: 1.55, color: Color(0xffb5c9c6)))));
}

class _Conversation extends StatelessWidget {
  const _Conversation(
      {required this.messages,
      required this.composer,
      required this.status,
      required this.loaded,
      required this.generating,
      required this.keyboardVisible,
      required this.memoryEnabled,
      required this.memoryNoteCount,
      required this.memoryBudget,
      required this.contextWindowTokens,
      required this.characterInstructionTokens,
      required this.onOpenMemoryAtelier,
      required this.onSend});
  final List<_ChatMessage> messages;
  final TextEditingController composer;
  final String status;
  final bool loaded, generating, keyboardVisible, memoryEnabled;
  final int memoryNoteCount,
      memoryBudget,
      contextWindowTokens,
      characterInstructionTokens;
  final Future<void> Function() onOpenMemoryAtelier;
  final Future<void> Function() onSend;
  void _dismissKeyboard() => FocusManager.instance.primaryFocus?.unfocus();
  @override
  Widget build(BuildContext context) => LayoutBuilder(
        builder: (context, constraints) {
          // A landscape iPhone with its keyboard open can leave less than a
          // composer height after the app bar. Prioritize composing over the
          // secondary status panels rather than allowing a bottom overflow.
          final dense = constraints.maxHeight < 360;
          return Column(children: [
            if (!dense)
              Container(
                  width: double.infinity,
                  margin: const EdgeInsets.fromLTRB(20, 16, 20, 0),
                  padding:
                      const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
                  decoration: BoxDecoration(
                      color: const Color(0xff192726),
                      borderRadius: BorderRadius.circular(12)),
                  child: Row(children: [
                    Icon(Icons.circle,
                        size: 10,
                        color: loaded
                            ? const Color(0xff91e7cf)
                            : const Color(0xfff7c77a)),
                    const SizedBox(width: 9),
                    Expanded(
                        child: Text(status,
                            maxLines: 2, overflow: TextOverflow.ellipsis))
                  ])),
            if (!dense)
              Padding(
                padding: const EdgeInsets.fromLTRB(20, 10, 20, 0),
                child: _MemoryPulse(
                  enabled: memoryEnabled,
                  noteCount: memoryNoteCount,
                  memoryBudget: memoryBudget,
                  contextWindowTokens: contextWindowTokens,
                  characterInstructionTokens: characterInstructionTokens,
                  onTap: onOpenMemoryAtelier,
                ),
              ),
            Expanded(
                child: ListView.builder(
                    padding: EdgeInsets.all(dense ? 12 : 20),
                    itemCount: messages.length,
                    itemBuilder: (_, index) =>
                        _MessageBubble(message: messages[index]))),
            // Scaffold resizes the body above the software keyboard. Adding
            // the inset again here would move the composer up twice.
            SafeArea(
                top: false,
                child: Padding(
                    padding: EdgeInsets.fromLTRB(
                        20, dense ? 4 : 4, 20, dense ? 8 : 16),
                    child: Row(
                        crossAxisAlignment: CrossAxisAlignment.end,
                        children: [
                          Expanded(
                              child: TextField(
                                  key: const Key('composer'),
                                  controller: composer,
                                  enabled: loaded && !generating,
                                  minLines: 1,
                                  maxLines: dense ? 2 : 4,
                                  decoration: const InputDecoration(
                                      hintText: '메시지를 입력하세요', filled: true))),
                          if (keyboardVisible) ...[
                            const SizedBox(width: 6),
                            IconButton(
                                key: const Key('dismiss-keyboard'),
                                tooltip: '키보드 내리기',
                                onPressed: _dismissKeyboard,
                                icon: const Icon(Icons.keyboard_hide_outlined)),
                          ],
                          const SizedBox(width: 10),
                          FilledButton.icon(
                              key: const Key('send'),
                              onPressed: loaded && !generating ? onSend : null,
                              icon: const Icon(Icons.arrow_upward),
                              label: const Text('전송'))
                        ]))),
          ]);
        },
      );
}

class _MemoryPulse extends StatelessWidget {
  const _MemoryPulse({
    required this.enabled,
    required this.noteCount,
    required this.memoryBudget,
    required this.contextWindowTokens,
    required this.characterInstructionTokens,
    required this.onTap,
  });
  final bool enabled;
  final int noteCount,
      memoryBudget,
      contextWindowTokens,
      characterInstructionTokens;
  final Future<void> Function() onTap;

  @override
  Widget build(BuildContext context) {
    const outputReserve = 8192;
    final reserved = memoryBudget + characterInstructionTokens + outputReserve;
    final safeHeadroom =
        (contextWindowTokens - reserved).clamp(0, contextWindowTokens);
    final pressure =
        contextWindowTokens == 0 ? 0.0 : reserved / contextWindowTokens;
    final warning = pressure > .62;
    final accent = enabled
        ? (warning ? const Color(0xfff7c77a) : const Color(0xff91e7cf))
        : const Color(0xff8a9a98);
    return Semantics(
      button: true,
      label: '기억 노트 및 context 설정 열기',
      child: Material(
        color: Colors.transparent,
        child: InkWell(
          key: const Key('memory-pulse'),
          borderRadius: BorderRadius.circular(14),
          onTap: onTap,
          child: Ink(
            padding: const EdgeInsets.fromLTRB(14, 11, 12, 12),
            decoration: BoxDecoration(
              color: const Color(0xff14211f),
              borderRadius: BorderRadius.circular(14),
              border: Border.all(color: accent.withValues(alpha: .42)),
            ),
            child: LayoutBuilder(
              builder: (context, constraints) {
                final tight = constraints.maxWidth < 430;
                return Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Row(children: [
                        Icon(Icons.auto_stories_outlined,
                            size: 18, color: accent),
                        const SizedBox(width: 8),
                        Expanded(
                          child: Text(
                            enabled
                                ? '기억 $noteCount개 · $memoryBudget t reserve'
                                : '장기 기억 꺼짐 · 노트 $noteCount개',
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: TextStyle(
                                fontWeight: FontWeight.w700, color: accent),
                          ),
                        ),
                        const Icon(Icons.tune,
                            size: 18, color: Color(0xffb5c9c6)),
                      ]),
                      const SizedBox(height: 9),
                      ClipRRect(
                        borderRadius: BorderRadius.circular(99),
                        child: LinearProgressIndicator(
                          minHeight: 5,
                          value: pressure.clamp(0.0, 1.0),
                          backgroundColor: const Color(0xff283b38),
                          valueColor: AlwaysStoppedAnimation<Color>(accent),
                        ),
                      ),
                      const SizedBox(height: 7),
                      Text(
                        tight
                            ? '여유 ${safeHeadroom}t / ${contextWindowTokens}t · 설정'
                            : '지시문 ${characterInstructionTokens}t + 기억 ${memoryBudget}t + 출력 ${outputReserve}t · 대화 여유 ${safeHeadroom}t / ${contextWindowTokens}t',
                        maxLines: tight ? 1 : 2,
                        overflow: TextOverflow.ellipsis,
                        style: const TextStyle(
                            fontSize: 11, color: Color(0xffb5c9c6)),
                      ),
                    ]);
              },
            ),
          ),
        ),
      ),
    );
  }
}

class _MessageBubble extends StatelessWidget {
  const _MessageBubble({required this.message});
  final _ChatMessage message;
  @override
  Widget build(BuildContext context) {
    final user = message.role == 'user';
    final system = message.role == 'system';
    return Align(
        alignment: user ? Alignment.centerRight : Alignment.centerLeft,
        child: Container(
            constraints: const BoxConstraints(maxWidth: 640),
            margin: const EdgeInsets.only(bottom: 12),
            padding: const EdgeInsets.all(14),
            decoration: BoxDecoration(
                color: user
                    ? const Color(0xff24594f)
                    : system
                        ? const Color(0xff28312b)
                        : const Color(0xff1d2b2a),
                borderRadius: BorderRadius.circular(14),
                border: Border.all(
                    color:
                        system ? const Color(0xff536761) : Colors.transparent)),
            child:
                Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text(message.role.toUpperCase(),
                  style: const TextStyle(
                      fontSize: 10,
                      letterSpacing: 1.4,
                      color: Color(0xff91e7cf))),
              const SizedBox(height: 5),
              Text(message.text.isEmpty ? '…' : message.text)
            ])));
  }
}

/// UI-only P7 preview. Persistence remains disabled until the managed SQLite
/// bridge owns these values; this harness deliberately never writes memories.
class _MemoryAtelierDialog extends StatefulWidget {
  const _MemoryAtelierDialog({
    required this.enabled,
    required this.retentionDays,
    required this.maxRetrieved,
    required this.contextTokenBudget,
    required this.contextWindowTokens,
    required this.temperature,
    required this.modelPath,
    required this.modelLoaded,
    required this.characterInstructionTokens,
    required this.notes,
  });
  final bool enabled;
  final int? retentionDays;
  final int maxRetrieved;
  final int contextTokenBudget;
  final int contextWindowTokens;
  final double temperature;
  final String modelPath;
  final bool modelLoaded;
  final int characterInstructionTokens;
  final List<_MemoryNote> notes;

  @override
  State<_MemoryAtelierDialog> createState() => _MemoryAtelierDialogState();
}

class _MemoryAtelierDialogState extends State<_MemoryAtelierDialog> {
  late bool _enabled;
  late int? _retentionDays;
  late int _maxRetrieved;
  late int _contextTokenBudget;
  late int _contextWindowTokens;
  late double _temperature;
  late List<_MemoryNote> _notes;

  @override
  void initState() {
    super.initState();
    _enabled = widget.enabled;
    _retentionDays = widget.retentionDays;
    _maxRetrieved = widget.maxRetrieved;
    _contextTokenBudget = widget.contextTokenBudget;
    _contextWindowTokens = widget.contextWindowTokens;
    _temperature = widget.temperature;
    _notes = List.of(widget.notes);
  }

  Future<void> _editNote({int? index}) async {
    final note = await showDialog<_MemoryNote>(
      context: context,
      builder: (_) =>
          _MemoryNoteDialog(existing: index == null ? null : _notes[index]),
    );
    if (note == null || !mounted) return;
    setState(() {
      if (index == null) {
        _notes.add(note);
      } else {
        _notes[index] = note;
      }
    });
  }

  void _save() => Navigator.of(context).pop(_MemoryAtelierResult(
        enabled: _enabled && _retentionDays != null,
        retentionDays: _enabled ? _retentionDays : null,
        maxRetrieved: _maxRetrieved,
        contextTokenBudget: _contextTokenBudget,
        contextWindowTokens: _contextWindowTokens,
        temperature: _temperature,
        notes: _notes,
      ));

  @override
  Widget build(BuildContext context) => AlertDialog(
        title: const Text('기억 아틀리에'),
        scrollable: true,
        content: SizedBox(
          width: 620,
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            Container(
              width: double.infinity,
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(
                color: const Color(0xff192726),
                borderRadius: BorderRadius.circular(10),
              ),
              child: const Text(
                'P7 HARNESS · managed SQLite bridge 대기 중\n'
                '이 화면의 노트와 설정은 테스트용 메모리 상태이며 기기에 저장되지 않습니다.',
                style: TextStyle(
                    fontSize: 12, height: 1.5, color: Color(0xffb5c9c6)),
              ),
            ),
            const SizedBox(height: 12),
            SwitchListTile.adaptive(
              key: const Key('memory-enable-toggle'),
              contentPadding: EdgeInsets.zero,
              title: const Text('장기 기억 사용'),
              subtitle: const Text('명시적 보존 기간을 정한 경우에만 P7 저장을 허용합니다.'),
              value: _enabled,
              onChanged: (value) => setState(() => _enabled = value),
            ),
            DropdownButtonFormField<int?>(
              key: const Key('memory-retention-choice'),
              initialValue: _retentionDays,
              isExpanded: true,
              decoration: const InputDecoration(labelText: '보존 기간'),
              items: const [
                DropdownMenuItem(
                    value: null,
                    child: Text('선택 필요 · 저장하지 않음',
                        maxLines: 1, overflow: TextOverflow.ellipsis)),
                DropdownMenuItem(value: 7, child: Text('7일')),
                DropdownMenuItem(value: 30, child: Text('30일')),
                DropdownMenuItem(value: 365, child: Text('1년')),
              ],
              onChanged: _enabled
                  ? (value) => setState(() => _retentionDays = value)
                  : null,
            ),
            const SizedBox(height: 14),
            _BudgetCard(
              maxRetrieved: _maxRetrieved,
              tokenBudget: _contextTokenBudget,
              characterInstructionTokens: widget.characterInstructionTokens,
              contextWindowTokens: _contextWindowTokens,
              modelPath: widget.modelPath,
              modelLoaded: widget.modelLoaded,
            ),
            Slider(
              key: const Key('memory-max-retrieved-slider'),
              value: _maxRetrieved.toDouble(),
              min: 1,
              max: 6,
              divisions: 5,
              label: '$_maxRetrieved개',
              onChanged: (value) =>
                  setState(() => _maxRetrieved = value.round()),
            ),
            Slider(
              key: const Key('memory-token-budget-slider'),
              value: _contextTokenBudget.toDouble(),
              min: 128,
              max: 512,
              divisions: 3,
              label: '$_contextTokenBudget tokens',
              onChanged: (value) =>
                  setState(() => _contextTokenBudget = value.round()),
            ),
            const SizedBox(height: 8),
            Text('Context window: $_contextWindowTokens tokens'),
            Slider(
              key: const Key('context-window-slider'),
              value: _contextWindowTokens.toDouble(),
              min: 8192,
              max: 131072,
              divisions: 127,
              label: '$_contextWindowTokens tokens',
              onChanged: (value) =>
                  setState(() => _contextWindowTokens = value.round()),
            ),
            const Wrap(
              alignment: WrapAlignment.spaceBetween,
              spacing: 12,
              runSpacing: 4,
              children: [
                Text('기본 8k',
                    style: TextStyle(fontSize: 11, color: Color(0xffa7bcba))),
                Text('실험 96k',
                    style: TextStyle(fontSize: 11, color: Color(0xff91e7cf))),
                Text('실험 상한 128k',
                    style: TextStyle(fontSize: 11, color: Color(0xffa7bcba))),
              ],
            ),
            Text('Temperature: ${_temperature.toStringAsFixed(1)}'),
            Slider(
              key: const Key('memory-temperature-slider'),
              value: _temperature,
              min: 0.1,
              max: 1.2,
              divisions: 11,
              label: _temperature.toStringAsFixed(1),
              onChanged: (value) => setState(() => _temperature = value),
            ),
            const Divider(height: 28),
            _EditorListHeader(
              title: '기억 노트',
              subtitle: '명시적으로 적은 사실만 장기 기억 후보가 됩니다.',
              onAdd: () => _editNote(),
            ),
            if (_notes.isEmpty)
              const Padding(
                padding: EdgeInsets.symmetric(vertical: 18),
                child: Text('아직 기억 노트가 없습니다.',
                    style: TextStyle(color: Color(0xffa7bcba))),
              ),
            ..._notes.asMap().entries.map((entry) => ListTile(
                  contentPadding: EdgeInsets.zero,
                  title: Text(entry.value.text),
                  subtitle: Text('중요도 ${entry.value.importance}/100'),
                  trailing: Wrap(spacing: 0, children: [
                    IconButton(
                      tooltip: '기억 노트 편집',
                      onPressed: () => _editNote(index: entry.key),
                      icon: const Icon(Icons.edit_outlined),
                    ),
                    IconButton(
                      tooltip: '기억 노트 삭제',
                      onPressed: () =>
                          setState(() => _notes.removeAt(entry.key)),
                      icon: const Icon(Icons.delete_outline),
                    ),
                  ]),
                )),
          ]),
        ),
        actions: [
          TextButton(
              onPressed: () => Navigator.pop(context),
              child: const Text('Cancel')),
          FilledButton(
              key: const Key('memory-save'),
              onPressed: _save,
              child: const Text('하네스에 적용')),
        ],
      );
}

class _BudgetCard extends StatelessWidget {
  const _BudgetCard({
    required this.maxRetrieved,
    required this.tokenBudget,
    required this.characterInstructionTokens,
    required this.contextWindowTokens,
    required this.modelPath,
    required this.modelLoaded,
  });
  final int maxRetrieved,
      tokenBudget,
      characterInstructionTokens,
      contextWindowTokens;
  final String modelPath;
  final bool modelLoaded;

  @override
  Widget build(BuildContext context) {
    const replyReserve = 8192;
    final headroom = (contextWindowTokens -
            tokenBudget -
            characterInstructionTokens -
            replyReserve)
        .clamp(0, contextWindowTokens);
    final probeSource = modelLoaded && modelPath.trim().isNotEmpty
        ? 'loaded probe model'
        : '96k-token experimental fallback';
    return Container(
      key: const Key('memory-context-budget'),
      width: double.infinity,
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        border: Border.all(color: const Color(0xff536761)),
        borderRadius: BorderRadius.circular(10),
      ),
      child: Text(
        'CONTEXT GUARD\n'
        '검색: 최대 $maxRetrieved개 · 기억 예산: $tokenBudget tokens\n'
        'Character bundle 추정: $characterInstructionTokens tokens\n'
        'Memory reservation: $tokenBudget tokens · 대화 headroom: $headroom tokens\n'
        'Recommended context: $contextWindowTokens tokens (diagnostic estimate, $probeSource)\n'
        '모바일 로컬 모델에서는 bundle 지시문과 검색 기억이 같은 context를 공유합니다. 예산을 넘는 노트는 prompt에 넣지 않습니다.',
        style: const TextStyle(
            fontSize: 12, height: 1.5, color: Color(0xffb5c9c6)),
      ),
    );
  }
}

class _MemoryNoteDialog extends StatefulWidget {
  const _MemoryNoteDialog({this.existing});
  final _MemoryNote? existing;
  @override
  State<_MemoryNoteDialog> createState() => _MemoryNoteDialogState();
}

class _MemoryNoteDialogState extends State<_MemoryNoteDialog> {
  late final TextEditingController _text;
  late int _importance;
  String? _error;
  @override
  void initState() {
    super.initState();
    _text = TextEditingController(text: widget.existing?.text ?? '');
    _importance = widget.existing?.importance ?? 50;
  }

  @override
  void dispose() {
    _text.dispose();
    super.dispose();
  }

  void _save() {
    final text = _text.text.trim();
    if (text.isEmpty) {
      setState(() => _error = '기억할 내용을 입력하세요.');
      return;
    }
    Navigator.of(context).pop(_MemoryNote(text: text, importance: _importance));
  }

  @override
  Widget build(BuildContext context) => AlertDialog(
        title: Text(widget.existing == null ? '기억 노트 추가' : '기억 노트 편집'),
        scrollable: true,
        content: Column(mainAxisSize: MainAxisSize.min, children: [
          TextField(
            key: const Key('memory-note-text'),
            controller: _text,
            minLines: 2,
            maxLines: 5,
            decoration: const InputDecoration(labelText: '기억할 사실 또는 설정'),
          ),
          const SizedBox(height: 12),
          Text('중요도 $_importance/100'),
          Slider(
            key: const Key('memory-note-importance'),
            value: _importance.toDouble(),
            min: 0,
            max: 100,
            divisions: 10,
            onChanged: (value) => setState(() => _importance = value.round()),
          ),
          if (_error != null)
            Text(_error!,
                style: TextStyle(color: Theme.of(context).colorScheme.error)),
        ]),
        actions: [
          TextButton(
              onPressed: () => Navigator.pop(context),
              child: const Text('Cancel')),
          FilledButton(
              key: const Key('memory-note-save'),
              onPressed: _save,
              child: const Text('저장')),
        ],
      );
}

class _CharacterDraftDialog extends StatefulWidget {
  const _CharacterDraftDialog({this.existing});
  final CharacterBundleSummary? existing;
  @override
  State<_CharacterDraftDialog> createState() => _CharacterDraftDialogState();
}

class _CharacterDraftDialogState extends State<_CharacterDraftDialog> {
  late final TextEditingController _id;
  late final TextEditingController _name;
  late final TextEditingController _system;
  late final TextEditingController _personality;
  late final TextEditingController _style;
  late final TextEditingController _scenario;
  final _lore = <_LoreFields>[];
  final _examples = <_ExampleFields>[];
  String? _error;

  @override
  void initState() {
    super.initState();
    final draft = widget.existing?.draft;
    _id = TextEditingController(text: draft?.id ?? 'test-character');
    _name = TextEditingController(text: draft?.displayName ?? 'Test Character');
    _system = TextEditingController(
        text: draft?.system ?? 'You are a helpful test character.');
    _personality = TextEditingController(text: draft?.personality ?? '');
    _style = TextEditingController(text: draft?.style ?? '');
    _scenario = TextEditingController(text: draft?.scenario ?? '');
    for (final item in draft?.lore ?? const <CharacterBundleLore>[]) {
      _lore.add(_LoreFields(item));
    }
    for (final item in draft?.examples ?? const <CharacterBundleExample>[]) {
      _examples.add(_ExampleFields(item));
    }
  }

  @override
  void dispose() {
    _id.dispose();
    _name.dispose();
    _system.dispose();
    _personality.dispose();
    _style.dispose();
    _scenario.dispose();
    for (final item in _lore) {
      item.dispose();
    }
    for (final item in _examples) {
      item.dispose();
    }
    super.dispose();
  }

  void _submit() {
    final draft = CharacterBundleDraft(
        id: _id.text.trim(),
        displayName: _name.text.trim(),
        system: _system.text.trim(),
        personality: _personality.text.trim(),
        style: _style.text.trim(),
        scenario: _scenario.text.trim(),
        lore: _lore
            .map((item) => CharacterBundleLore(
                name: item.name.text.trim(), text: item.text.text.trim()))
            .toList(),
        examples: _examples
            .map((item) => CharacterBundleExample(
                role: item.role, text: item.text.text.trim()))
            .toList());
    final error = draft.validate();
    if (error != null) {
      setState(() => _error = error);
      return;
    }
    Navigator.of(context).pop(draft);
  }

  Widget _section(String file, String role, TextEditingController controller,
          {bool required = false}) =>
      Padding(
          padding: const EdgeInsets.only(top: 16),
          child:
              Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [
              Text(file, style: const TextStyle(fontWeight: FontWeight.w700)),
              const SizedBox(width: 8),
              Text(required ? '필수 · $role' : '선택 · $role',
                  style:
                      const TextStyle(fontSize: 12, color: Color(0xffa7bcba))),
            ]),
            const SizedBox(height: 6),
            TextField(
                controller: controller,
                minLines: 2,
                maxLines: 5,
                decoration: InputDecoration(
                    labelText: file, border: const OutlineInputBorder())),
          ]));

  @override
  Widget build(BuildContext context) => AlertDialog(
          title: Text(widget.existing == null
              ? 'Character bundle 만들기'
              : 'Character bundle 편집'),
          scrollable: true,
          content: SizedBox(
              width: 620,
              child: Column(mainAxisSize: MainAxisSize.min, children: [
                const _BundleMap(),
                TextField(
                    controller: _id,
                    enabled: widget.existing == null,
                    decoration: const InputDecoration(labelText: 'ID')),
                TextField(
                    controller: _name,
                    decoration:
                        const InputDecoration(labelText: 'Display name')),
                _section('system.md', '변하지 않는 최상위 지시문', _system,
                    required: true),
                _section('personality.md', '성격·말투의 큰 방향', _personality),
                _section('style.md', '답변 형식·문체', _style),
                _section('scenario.md', '현재 역할극/상황', _scenario),
                const SizedBox(height: 18),
                _EditorListHeader(
                    title: 'lore/',
                    subtitle: '선택 · 번호 파일명 순서로 prompt에 추가',
                    onAdd: () => setState(
                        () => _lore.add(_LoreFields.empty(_lore.length + 1)))),
                ..._lore.asMap().entries.map((entry) => _LoreEditor(
                    fields: entry.value,
                    onRemove: () => setState(() {
                          final item = _lore.removeAt(entry.key);
                          item.dispose();
                        }))),
                const SizedBox(height: 18),
                _EditorListHeader(
                    title: 'examples.jsonl',
                    subtitle: '선택 · user/assistant few-shot turn',
                    onAdd: () =>
                        setState(() => _examples.add(_ExampleFields()))),
                ..._examples.asMap().entries.map((entry) => _ExampleEditor(
                    fields: entry.value,
                    onRemove: () => setState(() {
                          final item = _examples.removeAt(entry.key);
                          item.dispose();
                        }))),
                if (_error != null)
                  Padding(
                      padding: const EdgeInsets.only(top: 12),
                      child: Text(_error!,
                          style: TextStyle(
                              color: Theme.of(context).colorScheme.error)))
              ])),
          actions: [
            TextButton(
                onPressed: () => Navigator.pop(context),
                child: const Text('Cancel')),
            FilledButton(
                onPressed: _submit,
                child: Text(widget.existing == null ? 'Create & add' : '저장'))
          ]);
}

class _BundleMap extends StatelessWidget {
  const _BundleMap();
  @override
  Widget build(BuildContext context) => Container(
      width: double.infinity,
      padding: const EdgeInsets.all(12),
      margin: const EdgeInsets.only(bottom: 12),
      decoration: BoxDecoration(
          color: const Color(0xff192726),
          borderRadius: BorderRadius.circular(10)),
      child: const Text(
          'BUNDLE MAP  ·  manifest.json + system.md는 필수\n선택 파일은 비워 두면 bundle에서 제거됩니다.',
          style:
              TextStyle(fontSize: 12, height: 1.5, color: Color(0xffb5c9c6))));
}

class _EditorListHeader extends StatelessWidget {
  const _EditorListHeader(
      {required this.title, required this.subtitle, required this.onAdd});
  final String title, subtitle;
  final VoidCallback onAdd;
  @override
  Widget build(BuildContext context) => Row(children: [
        Expanded(
            child:
                Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text(title, style: const TextStyle(fontWeight: FontWeight.w700)),
          Text(subtitle,
              style: const TextStyle(fontSize: 12, color: Color(0xffa7bcba))),
        ])),
        TextButton.icon(
            onPressed: onAdd,
            icon: const Icon(Icons.add),
            label: const Text('추가')),
      ]);
}

class _LoreFields {
  _LoreFields(CharacterBundleLore item)
      : name = TextEditingController(text: item.name),
        text = TextEditingController(text: item.text);
  _LoreFields.empty(int ordinal)
      : name = TextEditingController(
            text: '${ordinal.toString().padLeft(3, '0')}-lore.md'),
        text = TextEditingController();
  final TextEditingController name, text;
  void dispose() {
    name.dispose();
    text.dispose();
  }
}

class _ExampleFields {
  _ExampleFields([CharacterBundleExample? item])
      : role = item?.role ?? 'user',
        text = TextEditingController(text: item?.text ?? '');
  String role;
  final TextEditingController text;
  void dispose() => text.dispose();
}

class _LoreEditor extends StatelessWidget {
  const _LoreEditor({required this.fields, required this.onRemove});
  final _LoreFields fields;
  final VoidCallback onRemove;
  @override
  Widget build(BuildContext context) => Padding(
      padding: const EdgeInsets.only(top: 8),
      child: Column(children: [
        Row(children: [
          Expanded(
              child: TextField(
                  controller: fields.name,
                  decoration: const InputDecoration(
                      labelText: '파일명 (예: 001-world.md)'))),
          IconButton(
              onPressed: onRemove,
              icon: const Icon(Icons.remove_circle_outline),
              tooltip: 'Lore 제거'),
        ]),
        TextField(
            controller: fields.text,
            minLines: 2,
            maxLines: 4,
            decoration: const InputDecoration(labelText: 'Lore 내용')),
      ]));
}

class _ExampleEditor extends StatelessWidget {
  const _ExampleEditor({required this.fields, required this.onRemove});
  final _ExampleFields fields;
  final VoidCallback onRemove;
  @override
  Widget build(BuildContext context) => Padding(
      padding: const EdgeInsets.only(top: 8),
      child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
        SizedBox(
            width: 112,
            child: DropdownButtonFormField<String>(
                initialValue: fields.role,
                items: const [
                  DropdownMenuItem(value: 'user', child: Text('user')),
                  DropdownMenuItem(value: 'assistant', child: Text('assistant'))
                ],
                onChanged: (value) {
                  if (value != null) fields.role = value;
                })),
        const SizedBox(width: 8),
        Expanded(
            child: TextField(
                controller: fields.text,
                minLines: 1,
                maxLines: 3,
                decoration: const InputDecoration(labelText: '예시 메시지'))),
        IconButton(
            onPressed: onRemove,
            icon: const Icon(Icons.remove_circle_outline),
            tooltip: '예시 제거'),
      ]));
}
