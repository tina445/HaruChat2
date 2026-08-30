import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:haruchat_xcross_native_probe/character_bundle_store.dart';

void main() {
  test('creates and lists a valid Character Bundle v1', () async {
    final temp = await Directory.systemTemp.createTemp('haruchat-character-store-');
    addTearDown(() => temp.delete(recursive: true));
    final store = CharacterBundleStore(Directory('${temp.path}/characters'));

    final created = await store.create(const CharacterBundleDraft(
      id: 'test-guide',
      displayName: '테스트 가이드',
      system: '항상 간결하게 안내합니다.',
      personality: '친절함',
    ));

    expect(created.id, 'test-guide');
    final listed = await store.list();
    expect(listed, hasLength(1));
    expect(listed.single.system, '항상 간결하게 안내합니다.');
    expect(File('${created.path}/manifest.json').existsSync(), isTrue);
    expect(File('${created.path}/personality.md').existsSync(), isTrue);
  });

  test('rejects unsafe test character IDs', () async {
    const draft = CharacterBundleDraft(id: '../escape', displayName: 'Bad', system: 'No');
    expect(draft.validate(), isNotNull);
  });
}
