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
  static const _compactWidth = 700.0;
  static const _comfortableHeight = 560.0;
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

  Future<void> _load() async =>
      _setStatus(await NativeProbe.load(_modelPath.text));

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
  Widget build(BuildContext context) => Scaffold(
        appBar: AppBar(title: const Text('HaruChat Native Probe')),
        body: SafeArea(
          top: false,
          child: LayoutBuilder(
            builder: (context, constraints) {
              final compact = constraints.maxWidth < _compactWidth;
              final short = constraints.maxHeight < _comfortableHeight;
              final controls = _controls(compact);
              return Center(
                child: ConstrainedBox(
                  constraints: const BoxConstraints(maxWidth: 1100),
                  child: Padding(
                    padding: const EdgeInsets.all(16),
                    child: short
                        ? _ScrollableProbeBody(
                            controls: controls,
                            response: _response.toString(),
                            log: _log.join('\n'),
                          )
                        : _ExpandedProbeBody(
                            controls: controls,
                            response: _response.toString(),
                            log: _log.join('\n'),
                          ),
                  ),
                ),
              );
            },
          ),
        ),
      );

  Widget _controls(bool compact) => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: [
          _StatusBanner(status: _status),
          const SizedBox(height: 12),
          compact ? _modelControlsCompact() : _modelControlsWide(),
          const SizedBox(height: 12),
          TextField(
            controller: _prompt,
            minLines: 2,
            maxLines: 4,
            textInputAction: TextInputAction.newline,
            decoration: const InputDecoration(
                border: OutlineInputBorder(), labelText: 'Prompt'),
          ),
          const SizedBox(height: 12),
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              _ProbeButton.filled(
                  onPressed: _generating ? null : _generate, label: 'Generate'),
              _ProbeButton.outlined(
                  onPressed: _generating ? _cancel : null, label: 'Cancel'),
              _ProbeButton.outlined(onPressed: _reset, label: 'Reset'),
              _ProbeButton.outlined(onPressed: _unload, label: 'Unload'),
            ],
          ),
        ],
      );

  Widget _modelControlsCompact() => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _modelPathField(),
          const SizedBox(height: 8),
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              _ProbeButton.filled(
                  onPressed: _chooseModel, label: 'Choose GGUF'),
              _ProbeButton.filled(onPressed: _load, label: 'Load'),
            ],
          ),
        ],
      );

  Widget _modelControlsWide() => Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Expanded(child: _modelPathField()),
          const SizedBox(width: 8),
          _ProbeButton.filled(onPressed: _chooseModel, label: 'Choose GGUF'),
          const SizedBox(width: 8),
          _ProbeButton.filled(onPressed: _load, label: 'Load'),
        ],
      );

  Widget _modelPathField() => TextField(
        controller: _modelPath,
        minLines: 1,
        maxLines: 2,
        keyboardType: TextInputType.url,
        decoration: const InputDecoration(
          border: OutlineInputBorder(),
          labelText: 'Imported GGUF path',
          hintText: 'Choose a model imported into Files',
        ),
      );
}

class _ExpandedProbeBody extends StatelessWidget {
  const _ExpandedProbeBody(
      {required this.controls, required this.response, required this.log});
  final Widget controls;
  final String response;
  final String log;

  @override
  Widget build(BuildContext context) => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          controls,
          const SizedBox(height: 16),
          Expanded(child: _PanelSection(title: 'Response', text: response)),
          const SizedBox(height: 12),
          Expanded(
              child: _PanelSection(
                  title: 'Structured native event log', text: log)),
        ],
      );
}

class _ScrollableProbeBody extends StatelessWidget {
  const _ScrollableProbeBody(
      {required this.controls, required this.response, required this.log});
  final Widget controls;
  final String response;
  final String log;

  @override
  Widget build(BuildContext context) => ListView(
        children: [
          controls,
          const SizedBox(height: 16),
          SizedBox(
              height: 180,
              child: _PanelSection(title: 'Response', text: response)),
          const SizedBox(height: 12),
          SizedBox(
              height: 180,
              child: _PanelSection(
                  title: 'Structured native event log', text: log)),
        ],
      );
}

class _StatusBanner extends StatelessWidget {
  const _StatusBanner({required this.status});
  final String status;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return DecoratedBox(
      decoration: BoxDecoration(
          color: scheme.primaryContainer,
          borderRadius: BorderRadius.circular(8)),
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: SelectableText(status,
            style: TextStyle(color: scheme.onPrimaryContainer)),
      ),
    );
  }
}

class _ProbeButton extends StatelessWidget {
  const _ProbeButton._(
      {required this.onPressed, required this.label, required this.filled});
  factory _ProbeButton.filled(
          {required VoidCallback? onPressed, required String label}) =>
      _ProbeButton._(onPressed: onPressed, label: label, filled: true);
  factory _ProbeButton.outlined(
          {required VoidCallback? onPressed, required String label}) =>
      _ProbeButton._(onPressed: onPressed, label: label, filled: false);

  final VoidCallback? onPressed;
  final String label;
  final bool filled;

  @override
  Widget build(BuildContext context) {
    final style =
        ButtonStyle(minimumSize: WidgetStateProperty.all(const Size(0, 48)));
    return filled
        ? FilledButton(onPressed: onPressed, style: style, child: Text(label))
        : OutlinedButton(
            onPressed: onPressed, style: style, child: Text(label));
  }
}

class _PanelSection extends StatelessWidget {
  const _PanelSection({required this.title, required this.text});
  final String title;
  final String text;

  @override
  Widget build(BuildContext context) => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(title, style: Theme.of(context).textTheme.labelLarge),
          const SizedBox(height: 6),
          Expanded(child: _TextPanel(text: text)),
        ],
      );
}

class _TextPanel extends StatelessWidget {
  const _TextPanel({required this.text});
  final String text;

  @override
  Widget build(BuildContext context) => DecoratedBox(
        decoration: BoxDecoration(
          color: Theme.of(context).colorScheme.surfaceContainerLowest,
          border: Border.all(color: Theme.of(context).colorScheme.outline),
          borderRadius: BorderRadius.circular(8),
        ),
        child: Scrollbar(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(12),
            child: SelectableText(
              text.isEmpty ? 'No events yet.' : text,
              style: Theme.of(context)
                  .textTheme
                  .bodySmall
                  ?.copyWith(fontFamily: 'monospace'),
            ),
          ),
        ),
      );
}
