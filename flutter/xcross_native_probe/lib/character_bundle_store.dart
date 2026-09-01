import 'dart:convert';
import 'dart:io';

import 'package:path_provider/path_provider.dart';

class CharacterBundleLore {
  const CharacterBundleLore({required this.name, required this.text});
  final String name;
  final String text;
}

class CharacterBundleExample {
  const CharacterBundleExample({required this.role, required this.text});
  final String role;
  final String text;
}

class CharacterBundleDraft {
  const CharacterBundleDraft({
    required this.id,
    required this.displayName,
    required this.system,
    this.personality = '',
    this.style = '',
    this.scenario = '',
    this.lore = const [],
    this.examples = const [],
  });

  final String id, displayName, system, personality, style, scenario;
  final List<CharacterBundleLore> lore;
  final List<CharacterBundleExample> examples;

  String? validate() {
    if (!RegExp(r'^[a-z0-9][a-z0-9-]{0,63}$').hasMatch(id)) {
      return 'ID는 소문자, 숫자, 하이픈만 사용하세요.';
    }
    if (displayName.trim().isEmpty || system.trim().isEmpty) {
      return '표시 이름과 system instruction은 필수입니다.';
    }
    final names = <String>{};
    for (final entry in lore) {
      if (!RegExp(r'^\d+-[^/\\]+\.md$').hasMatch(entry.name) ||
          !names.add(entry.name) ||
          entry.text.trim().isEmpty) {
        return 'Lore는 순서 번호가 있는 .md 파일명과 내용을 입력하세요. 예: 001-world.md';
      }
    }
    for (final example in examples) {
      if ((example.role != 'user' && example.role != 'assistant') ||
          example.text.trim().isEmpty) {
        return '예시는 user 또는 assistant 역할과 내용을 모두 입력하세요.';
      }
    }
    return null;
  }
}

class CharacterBundleSummary extends CharacterBundleDraft {
  const CharacterBundleSummary({
    required super.id,
    required super.displayName,
    required super.system,
    required this.path,
    super.personality,
    super.style,
    super.scenario,
    super.lore,
    super.examples,
  });

  final String path;

  CharacterBundleDraft get draft => CharacterBundleDraft(
      id: id,
      displayName: displayName,
      system: system,
      personality: personality,
      style: style,
      scenario: scenario,
      lore: lore,
      examples: examples);

  String get promptContext => [
        system,
        if (personality.trim().isNotEmpty) 'Personality:\n$personality',
        if (style.trim().isNotEmpty) 'Speaking style:\n$style',
        if (scenario.trim().isNotEmpty) 'Scenario:\n$scenario',
        ...lore.map((entry) => 'Lore — ${entry.name}:\n${entry.text}'),
        'Treat the declared personality and speaking style as binding. Do not substitute a generic assistant voice.',
        ...examples.map((entry) => '${entry.role}: ${entry.text}'),
      ].join('\n\n');
}

/// Test-only editor storage for the fixed, data-only Character Bundle v1 layout.
class CharacterBundleStore {
  CharacterBundleStore(this._root);
  final Directory _root;

  static Future<CharacterBundleStore> openDefault() async {
    final documents = await getApplicationDocumentsDirectory();
    return CharacterBundleStore(
        Directory('${documents.path}/HaruChatProbe/characters'));
  }

  Future<List<CharacterBundleSummary>> list() async {
    if (!await _root.exists()) return const [];
    final bundles = <CharacterBundleSummary>[];
    await for (final entry in _root.list(followLinks: false)) {
      if (entry is! Directory) continue;
      try {
        bundles.add(await _readBundle(entry));
      } on FileSystemException catch (_) {
        // Incomplete local drafts are not selectable by the test host.
      } on FormatException catch (_) {
        // Malformed data-only bundles are not selectable by the test host.
      }
    }
    bundles.sort((a, b) => a.displayName.compareTo(b.displayName));
    return bundles;
  }

  Future<CharacterBundleSummary> create(CharacterBundleDraft draft) async {
    final error = draft.validate();
    if (error != null) {
      throw ArgumentError(error);
    }
    await _root.create(recursive: true);
    final bundle = Directory('${_root.path}/${draft.id}');
    if (await bundle.exists()) {
      throw StateError('동일한 character ID가 이미 있습니다.');
    }
    await bundle.create();
    try {
      await _writeBundle(bundle, draft);
      return await _readBundle(bundle);
    } catch (_) {
      if (await bundle.exists()) await bundle.delete(recursive: true);
      rethrow;
    }
  }

  Future<CharacterBundleSummary> update(
      CharacterBundleSummary existing, CharacterBundleDraft draft) async {
    if (existing.id != draft.id) {
      throw ArgumentError('기존 bundle의 ID는 변경할 수 없습니다. 새 bundle을 만드세요.');
    }
    final error = draft.validate();
    if (error != null) {
      throw ArgumentError(error);
    }
    final bundle = Directory(existing.path);
    if (!await bundle.exists()) {
      throw StateError('수정할 character bundle을 찾을 수 없습니다.');
    }
    await _writeBundle(bundle, draft);
    return _readBundle(bundle);
  }

  Future<CharacterBundleSummary> _readBundle(Directory bundle) async {
    final manifest = File('${bundle.path}/manifest.json');
    final system = File('${bundle.path}/system.md');
    if (!await manifest.exists() || !await system.exists()) {
      throw const FileSystemException('Required bundle file is missing.');
    }
    final raw =
        jsonDecode(await manifest.readAsString()) as Map<String, dynamic>;
    if (raw['schemaVersion'] != 1 ||
        raw['id'] is! String ||
        raw['displayName'] is! String) {
      throw const FormatException('Invalid manifest.json');
    }
    final id = raw['id'] as String;
    if (bundle.uri.pathSegments.where((part) => part.isNotEmpty).last != id) {
      throw const FormatException('Bundle directory and manifest ID differ.');
    }
    final draft = CharacterBundleDraft(
      id: id,
      displayName: raw['displayName'] as String,
      system: await system.readAsString(),
      personality: await _readOptional(bundle, 'personality.md'),
      style: await _readOptional(bundle, 'style.md'),
      scenario: await _readOptional(bundle, 'scenario.md'),
      lore: await _readLore(bundle),
      examples: await _readExamples(bundle),
    );
    final error = draft.validate();
    if (error != null) throw FormatException(error);
    return CharacterBundleSummary(
      path: bundle.path,
      id: draft.id,
      displayName: draft.displayName,
      system: draft.system,
      personality: draft.personality,
      style: draft.style,
      scenario: draft.scenario,
      lore: draft.lore,
      examples: draft.examples,
    );
  }

  Future<void> _writeBundle(
      Directory bundle, CharacterBundleDraft draft) async {
    await File('${bundle.path}/manifest.json').writeAsString(
      const JsonEncoder.withIndent('  ').convert({
        'schemaVersion': 1,
        'id': draft.id,
        'displayName': draft.displayName.trim(),
      }),
    );
    await File('${bundle.path}/system.md').writeAsString(draft.system.trim());
    await _writeOptional(bundle, 'personality.md', draft.personality);
    await _writeOptional(bundle, 'style.md', draft.style);
    await _writeOptional(bundle, 'scenario.md', draft.scenario);
    await _writeLore(bundle, draft.lore);
    await _writeExamples(bundle, draft.examples);
  }

  Future<void> _writeOptional(
      Directory bundle, String name, String value) async {
    final file = File('${bundle.path}/$name');
    if (value.trim().isEmpty) {
      if (await file.exists()) await file.delete();
    } else {
      await file.writeAsString(value.trim());
    }
  }

  Future<String> _readOptional(Directory bundle, String name) async {
    final file = File('${bundle.path}/$name');
    if (!await file.exists()) return '';
    return file.readAsString();
  }

  Future<List<CharacterBundleLore>> _readLore(Directory bundle) async {
    final lore = Directory('${bundle.path}/lore');
    if (!await lore.exists()) {
      return const [];
    }
    final entries = <CharacterBundleLore>[];
    await for (final entry in lore.list(followLinks: false)) {
      if (entry is! File) {
        throw const FormatException('Lore must contain only Markdown files.');
      }
      final name = entry.uri.pathSegments.last;
      entries.add(
          CharacterBundleLore(name: name, text: await entry.readAsString()));
    }
    entries.sort((a, b) => a.name.compareTo(b.name));
    return entries;
  }

  Future<void> _writeLore(
      Directory bundle, List<CharacterBundleLore> entries) async {
    final lore = Directory('${bundle.path}/lore');
    if (await lore.exists()) await lore.delete(recursive: true);
    if (entries.isEmpty) return;
    await lore.create();
    for (final entry in entries) {
      await File('${lore.path}/${entry.name}').writeAsString(entry.text.trim());
    }
  }

  Future<List<CharacterBundleExample>> _readExamples(Directory bundle) async {
    final file = File('${bundle.path}/examples.jsonl');
    if (!await file.exists()) return const [];
    final examples = <CharacterBundleExample>[];
    for (final line in await file.readAsLines()) {
      if (line.trim().isEmpty) throw const FormatException('Empty JSONL line.');
      final item = jsonDecode(line) as Map<String, dynamic>;
      if (item['role'] is! String || item['text'] is! String) {
        throw const FormatException('Invalid example JSONL entry.');
      }
      examples.add(CharacterBundleExample(
          role: item['role'] as String, text: item['text'] as String));
    }
    return examples;
  }

  Future<void> _writeExamples(
      Directory bundle, List<CharacterBundleExample> examples) async {
    final file = File('${bundle.path}/examples.jsonl');
    if (examples.isEmpty) {
      if (await file.exists()) await file.delete();
      return;
    }
    await file.writeAsString(examples
        .map((entry) =>
            jsonEncode({'role': entry.role, 'text': entry.text.trim()}))
        .join('\n'));
  }
}
