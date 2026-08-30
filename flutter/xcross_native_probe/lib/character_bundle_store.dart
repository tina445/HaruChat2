import 'dart:convert';
import 'dart:io';

import 'package:path_provider/path_provider.dart';

class CharacterBundleDraft {
  const CharacterBundleDraft({
    required this.id,
    required this.displayName,
    required this.system,
    this.personality = '',
    this.style = '',
    this.scenario = '',
  });

  final String id;
  final String displayName;
  final String system;
  final String personality;
  final String style;
  final String scenario;

  String? validate() {
    if (!RegExp(r'^[a-z0-9][a-z0-9-]{0,63}$').hasMatch(id)) {
      return 'ID는 소문자, 숫자, 하이픈만 사용하세요.';
    }
    if (displayName.trim().isEmpty || system.trim().isEmpty) {
      return '표시 이름과 system instruction은 필수입니다.';
    }
    return null;
  }
}

class CharacterBundleSummary {
  const CharacterBundleSummary({
    required this.id,
    required this.displayName,
    required this.system,
    required this.path,
  });

  final String id;
  final String displayName;
  final String system;
  final String path;
}

/// Test-only writer for the fixed Character Bundle v1 directory layout.
class CharacterBundleStore {
  CharacterBundleStore(this._root);

  final Directory _root;

  static Future<CharacterBundleStore> openDefault() async {
    final documents = await getApplicationDocumentsDirectory();
    return CharacterBundleStore(Directory('${documents.path}/HaruChatProbe/characters'));
  }

  Future<List<CharacterBundleSummary>> list() async {
    if (!await _root.exists()) return const [];
    final bundles = <CharacterBundleSummary>[];
    await for (final entry in _root.list(followLinks: false)) {
      if (entry is! Directory) continue;
      final manifest = File('${entry.path}/manifest.json');
      final system = File('${entry.path}/system.md');
      if (!await manifest.exists() || !await system.exists()) continue;
      try {
        final raw = jsonDecode(await manifest.readAsString()) as Map<String, dynamic>;
        if (raw['schemaVersion'] != 1 || raw['id'] is! String || raw['displayName'] is! String) continue;
        bundles.add(CharacterBundleSummary(
          id: raw['id'] as String,
          displayName: raw['displayName'] as String,
          system: await system.readAsString(),
          path: entry.path,
        ));
      } on FormatException {
        // Invalid local test data is intentionally omitted from the selector.
      }
    }
    bundles.sort((a, b) => a.displayName.compareTo(b.displayName));
    return bundles;
  }

  Future<CharacterBundleSummary> create(CharacterBundleDraft draft) async {
    final error = draft.validate();
    if (error != null) throw ArgumentError(error);
    await _root.create(recursive: true);
    final bundle = Directory('${_root.path}/${draft.id}');
    if (await bundle.exists()) throw StateError('동일한 character ID가 이미 있습니다.');
    await bundle.create();
    try {
      await File('${bundle.path}/manifest.json').writeAsString(const JsonEncoder.withIndent('  ').convert({
        'schemaVersion': 1,
        'id': draft.id,
        'displayName': draft.displayName.trim(),
      }));
      await File('${bundle.path}/system.md').writeAsString(draft.system.trim());
      await _writeOptional(bundle, 'personality.md', draft.personality);
      await _writeOptional(bundle, 'style.md', draft.style);
      await _writeOptional(bundle, 'scenario.md', draft.scenario);
    } catch (_) {
      if (await bundle.exists()) await bundle.delete(recursive: true);
      rethrow;
    }
    return CharacterBundleSummary(id: draft.id, displayName: draft.displayName.trim(), system: draft.system.trim(), path: bundle.path);
  }

  Future<void> _writeOptional(Directory bundle, String name, String value) async {
    if (value.trim().isNotEmpty) await File('${bundle.path}/$name').writeAsString(value.trim());
  }
}
