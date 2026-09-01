import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:haruchat_xcross_native_probe/character_bundle_store.dart';

void main() {
  test('creates and lists a valid Character Bundle v1', () async {
    final temp =
        await Directory.systemTemp.createTemp('haruchat-character-store-');
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
    expect(listed.single.personality, '친절함');
    expect(listed.single.promptContext, contains('Personality:\n친절함'));
    expect(File('${created.path}/manifest.json').existsSync(), isTrue);
    expect(File('${created.path}/personality.md').existsSync(), isTrue);
  });

  test('rejects unsafe test character IDs', () async {
    const draft =
        CharacterBundleDraft(id: '../escape', displayName: 'Bad', system: 'No');
    expect(draft.validate(), isNotNull);
  });

  test('edits optional v1 files, lore, and examples', () async {
    final temp =
        await Directory.systemTemp.createTemp('haruchat-character-store-');
    addTearDown(() => temp.delete(recursive: true));
    final store = CharacterBundleStore(Directory('${temp.path}/characters'));
    final created = await store.create(const CharacterBundleDraft(
        id: 'editor-test', displayName: 'Before', system: 'Initial system'));

    final updated = await store.update(
      created,
      const CharacterBundleDraft(
        id: 'editor-test',
        displayName: 'After',
        system: 'Updated system',
        style: 'Use short sentences.',
        lore: [
          CharacterBundleLore(name: '001-world.md', text: 'A quiet world.')
        ],
        examples: [
          CharacterBundleExample(role: 'user', text: 'Hello'),
          CharacterBundleExample(role: 'assistant', text: 'Welcome.'),
        ],
      ),
    );

    expect(updated.displayName, 'After');
    expect(updated.lore.single.name, '001-world.md');
    expect(updated.examples, hasLength(2));
    expect(await File('${updated.path}/lore/001-world.md').readAsString(),
        'A quiet world.');
    expect(await File('${updated.path}/examples.jsonl').readAsString(),
        contains('"assistant"'));
    expect(updated.promptContext, contains('Lore — 001-world.md'));
  });
}
