import 'dart:async';

import 'package:flutter/material.dart';
import 'package:hc_llm_flutter/hc_llm_flutter.dart';

void main() => runApp(const HaruChatNativeProbeApp());

class HaruChatNativeProbeApp extends StatelessWidget {
  const HaruChatNativeProbeApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'HaruChat Native Probe',
      theme: ThemeData(colorSchemeSeed: Colors.indigo, useMaterial3: true),
      home: const NativeProbePage(),
    );
  }
}

class NativeProbePage extends StatefulWidget {
  const NativeProbePage({super.key});

  @override
  State<NativeProbePage> createState() => _NativeProbePageState();
}

class _NativeProbePageState extends State<NativeProbePage> {
  final _modelPath = TextEditingController();
  final _prompt = TextEditingController(text: 'Hello');
  final _response = StringBuffer();
  final _log = <String>[];
  StreamSubscription<NativeProbeEvent>? _events;
  String _status = 'Select a GGUF model';
  bool _generating = false;

  @override
  void initState() {
    super.initState();
    _events = NativeProbe.events.listen(_onEvent, onError: (Object error) {
      _setStatus('Native event stream failed: $error');
    });
  }

  @override
  void dispose() {
    _events?.cancel();
    _modelPath.dispose();
    _prompt.dispose();
    super.dispose();
  }

  void _onEvent(NativeProbeEvent event) {
    if (!mounted) return;
    setState(() {
      if (event.status != null) _status = event.status!;
      if (event.logLine != null) _log.add(event.logLine!);
      if (event.token != null) _response.write(event.token);
      if (event.isTerminal) _generating = false;
    });
  }

  void _setStatus(String status) {
    if (mounted) setState(() => _status = status);
  }

  Future<void> _chooseModel() async {
    final path = await NativeProbe.chooseModel();
    if (path != null && mounted) setState(() => _modelPath.text = path);
  }

  Future<void> _load() async => _setStatus(await NativeProbe.load(_modelPath.text));

  Future<void> _generate() async {
    setState(() {
      _response.clear();
      _log.clear();
      _generating = true;
    });
    _setStatus(await NativeProbe.generate(_prompt.text));
  }

  Future<void> _cancel() async => _setStatus(await NativeProbe.cancel());
  Future<void> _reset() async => _setStatus(await NativeProbe.reset());
  Future<void> _unload() async => _setStatus(await NativeProbe.unload());

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return Scaffold(
      appBar: AppBar(title: const Text('HaruChat Native Probe')),
      body: SafeArea(
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(_status, style: TextStyle(color: scheme.primary)),
              const SizedBox(height: 8),
              Row(children: [
                Expanded(child: TextField(controller: _modelPath, decoration: const InputDecoration(labelText: 'Imported GGUF path'))),
                const SizedBox(width: 8),
                FilledButton(onPressed: _chooseModel, child: const Text('Choose GGUF')),
                const SizedBox(width: 8),
                FilledButton(onPressed: _load, child: const Text('Load')),
              ]),
              const SizedBox(height: 8),
              TextField(controller: _prompt, minLines: 2, maxLines: 4, decoration: const InputDecoration(border: OutlineInputBorder(), labelText: 'Prompt')),
              const SizedBox(height: 8),
              Wrap(spacing: 8, children: [
                FilledButton(onPressed: _generating ? null : _generate, child: const Text('Generate')),
                OutlinedButton(onPressed: _generating ? _cancel : null, child: const Text('Cancel')),
                OutlinedButton(onPressed: _reset, child: const Text('Reset')),
                OutlinedButton(onPressed: _unload, child: const Text('Unload')),
              ]),
              const SizedBox(height: 8),
              const Text('Response'),
              Expanded(child: _TextPanel(text: _response.toString())),
              const SizedBox(height: 8),
              const Text('Structured native event log'),
              Expanded(child: _TextPanel(text: _log.join('\n'))),
            ],
          ),
        ),
      ),
    );
  }
}

class _TextPanel extends StatelessWidget {
  const _TextPanel({required this.text});
  final String text;

  @override
  Widget build(BuildContext context) => DecoratedBox(
        decoration: BoxDecoration(border: Border.all(color: Theme.of(context).colorScheme.outline)),
        child: SingleChildScrollView(padding: const EdgeInsets.all(8), child: SelectableText(text)),
      );
}
