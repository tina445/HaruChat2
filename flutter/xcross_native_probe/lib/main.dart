import 'dart:async';

import 'package:flutter/material.dart';
import 'package:hc_llm_flutter/hc_llm_flutter.dart';

import 'character_bundle_store.dart';

void main() => runApp(const HaruChatNativeProbeApp());

/// Flutter test host mirroring the Phase 6 Unity screen. Native lifecycle
/// calls remain real; this app does not replace the Unity/M4 MVP gate.
class HaruChatNativeProbeApp extends StatelessWidget {
  const HaruChatNativeProbeApp({super.key});

  @override
  Widget build(BuildContext context) => MaterialApp(
        title: 'HaruChat P6 UI Harness',
        debugShowCheckedModeBanner: false,
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

class _ChatMessage {
  _ChatMessage(this.role, this.text);
  final String role;
  String text;
}

class NativeProbePage extends StatefulWidget {
  const NativeProbePage({super.key});
  @override
  State<NativeProbePage> createState() => _NativeProbePageState();
}

class _NativeProbePageState extends State<NativeProbePage> {
  static const _compactWidth = 800.0;
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
    final status = await NativeProbe.load(_modelPath.text);
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
    _setStatus(await NativeProbe.generate(_probePrompt(input)));
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

  Future<void> _refreshCharacters() async {
    try {
      _characterStore ??= await CharacterBundleStore.openDefault();
      final characters = await _characterStore!.list();
      if (mounted) setState(() => _characters = characters);
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
      await _refreshCharacters();
      if (mounted) setState(() => _selectedCharacter = created);
    } catch (error) {
      _setStatus('Character creation failed: $error');
    }
  }

  @override
  Widget build(BuildContext context) => LayoutBuilder(
        builder: (context, constraints) {
          final compact = constraints.maxWidth < _compactWidth;
          final rail = _ControlRail(
            modelPath: _modelPath,
            characters: _characters,
            selected: _selectedCharacter,
            loaded: _modelLoaded,
            generating: _generating,
            onCharacterChanged: (value) =>
                setState(() => _selectedCharacter = value),
            onAddCharacter: _addCharacter,
            onRefreshCharacters: _refreshCharacters,
            onChooseModel: _chooseModel,
            onLoad: _load,
            onUnload: _unload,
            onNewConversation: _newConversation,
            onCancel: _cancel,
          );
          return Scaffold(
            resizeToAvoidBottomInset: false,
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
      required this.onRefreshCharacters,
      required this.onChooseModel,
      required this.onLoad,
      required this.onUnload,
      required this.onNewConversation,
      required this.onCancel});
  final TextEditingController modelPath;
  final List<CharacterBundleSummary> characters;
  final CharacterBundleSummary? selected;
  final bool loaded, generating;
  final ValueChanged<CharacterBundleSummary?> onCharacterChanged;
  final Future<void> Function() onAddCharacter,
      onRefreshCharacters,
      onChooseModel,
      onLoad,
      onUnload,
      onNewConversation,
      onCancel;
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
            icon: Icons.refresh, label: '새로고침', onTap: onRefreshCharacters),
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
      required this.onSend});
  final List<_ChatMessage> messages;
  final TextEditingController composer;
  final String status;
  final bool loaded, generating;
  final Future<void> Function() onSend;
  @override
  Widget build(BuildContext context) => Column(children: [
        Container(
            width: double.infinity,
            margin: const EdgeInsets.fromLTRB(20, 16, 20, 0),
            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
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
              Expanded(child: Text(status))
            ])),
        Expanded(
            child: ListView.builder(
                padding: const EdgeInsets.all(20),
                itemCount: messages.length,
                itemBuilder: (_, index) =>
                    _MessageBubble(message: messages[index]))),
        AnimatedPadding(
            duration: const Duration(milliseconds: 180),
            curve: Curves.easeOutCubic,
            padding: EdgeInsets.only(
                bottom: MediaQuery.viewInsetsOf(context).bottom),
            child: SafeArea(
                top: false,
                child: Padding(
                    padding: const EdgeInsets.fromLTRB(20, 4, 20, 16),
                    child: Row(
                        crossAxisAlignment: CrossAxisAlignment.end,
                        children: [
                          Expanded(
                              child: TextField(
                                  key: const Key('composer'),
                                  controller: composer,
                                  enabled: loaded && !generating,
                                  minLines: 1,
                                  maxLines: 4,
                                  decoration: const InputDecoration(
                                      hintText: '메시지를 입력하세요', filled: true))),
                          const SizedBox(width: 10),
                          FilledButton.icon(
                              key: const Key('send'),
                              onPressed: loaded && !generating ? onSend : null,
                              icon: const Icon(Icons.arrow_upward),
                              label: const Text('전송'))
                        ])))),
      ]);
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

class _CharacterDraftDialog extends StatefulWidget {
  const _CharacterDraftDialog();
  @override
  State<_CharacterDraftDialog> createState() => _CharacterDraftDialogState();
}

class _CharacterDraftDialogState extends State<_CharacterDraftDialog> {
  final _id = TextEditingController(text: 'test-character');
  final _name = TextEditingController(text: 'Test Character');
  final _system =
      TextEditingController(text: 'You are a helpful test character.');
  String? _error;
  @override
  void dispose() {
    _id.dispose();
    _name.dispose();
    _system.dispose();
    super.dispose();
  }

  void _submit() {
    final draft = CharacterBundleDraft(
        id: _id.text.trim(),
        displayName: _name.text.trim(),
        system: _system.text.trim());
    final error = draft.validate();
    if (error != null) {
      setState(() => _error = error);
      return;
    }
    Navigator.of(context).pop(draft);
  }

  @override
  Widget build(BuildContext context) => AlertDialog(
          title: const Text('Create test character'),
          scrollable: true,
          content: SizedBox(
              width: 520,
              child: Column(mainAxisSize: MainAxisSize.min, children: [
                TextField(
                    controller: _id,
                    decoration: const InputDecoration(labelText: 'ID')),
                TextField(
                    controller: _name,
                    decoration:
                        const InputDecoration(labelText: 'Display name')),
                TextField(
                    controller: _system,
                    minLines: 3,
                    maxLines: 6,
                    decoration:
                        const InputDecoration(labelText: 'System instruction')),
                if (_error != null)
                  Text(_error!,
                      style:
                          TextStyle(color: Theme.of(context).colorScheme.error))
              ])),
          actions: [
            TextButton(
                onPressed: () => Navigator.pop(context),
                child: const Text('Cancel')),
            FilledButton(onPressed: _submit, child: const Text('Create & add'))
          ]);
}
