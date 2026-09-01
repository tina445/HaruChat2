# Character Bundle v1 테스트 가이드

Character Bundle v1은 캐릭터의 지시문과 예시를 담는 **로컬 디렉터리**다. 압축 파일이나 실행 가능한 플러그인이 아니며, 모델 파일·API key·도구 권한·네트워크 설정을 포함하지 않는다. 모델 선택과 추론 설정은 별도의 `ModelConfig`/`ModelProfile`이 담당한다.

이 문서는 현재 Core의 `CharacterBundleLoader`, `CharacterCatalog`, `PromptCompiler` 구현과 [아키텍처](ARCHITECTURE.md)의 bundle v1 규칙을 테스트 관점에서 설명한다.

## 디렉터리 구조

bundle 디렉터리명은 `manifest.json`의 `id`와 정확히 같아야 한다.

```text
<character-id>/
├── manifest.json       # 필수
├── system.md           # 필수
├── personality.md      # 선택
├── style.md            # 선택
├── scenario.md         # 선택
├── lore/               # 선택; .md 파일만 사용
│   ├── 001-world.md
│   └── 010-history.md
└── examples.jsonl      # 선택
```

최소 테스트 bundle 예시:

```text
tester/
├── manifest.json
└── system.md
```

`manifest.json`:

```json
{
  "schemaVersion": 1,
  "id": "tester",
  "displayName": "테스트 캐릭터"
}
```

`system.md`에는 캐릭터의 변하지 않는 최상위 지시문을 작성한다. 예를 들어 “사용자에게 한국어로 친절하고 간결하게 답한다.”처럼 작성한다.

## 파일과 모델의 역할

| 항목 | 역할 | 현재 v1 규칙 |
| --- | --- | --- |
| `manifest.json` | bundle 식별 | `schemaVersion: 1`, 비어 있지 않은 `id`, `displayName`이 필수다. |
| `system.md` | 기본 system instruction | 필수 Markdown 파일이다. |
| `personality.md` | 성격·말투의 큰 방향 | 선택이다. |
| `style.md` | 답변 형식·문체 | 선택이다. |
| `scenario.md` | 현재 역할극/상황 | 선택이다. |
| `lore/*.md` | 세계관·사실 정보 | 선택이다. 파일명 ordinal 순서로 읽는다. |
| `examples.jsonl` | few-shot user/assistant turn | 선택이다. 각 줄은 독립 JSON 객체다. |

`examples.jsonl`의 각 줄은 다음 형식만 허용한다.

```json
{"role":"user","text":"안녕하세요"}
{"role":"assistant","text":"안녕하세요. 무엇을 도와드릴까요?"}
```

`role`은 정확히 `user` 또는 `assistant`여야 하고, `text` 필드는 반드시 있어야 한다. 현재 loader는 빈 문자열 자체는 허용하지만, 테스트 bundle에서는 의미 있는 예시 문장을 사용한다. 예시는 모델별 chat template이 아니라 provider-neutral message로 저장된다.

Core가 로드한 결과는 변경 불가능한 `CharacterDefinition`이다.

```text
CharacterDefinition
├── Id, DisplayName
├── System
├── Personality, Style, Scenario (선택)
├── Lore[]
├── Examples[] (ModelMessage: User/Assistant)
└── ContentHash
```

`ContentHash`는 bundle 내용의 snapshot 식별자다. 캐릭터 데이터가 바뀐 context를 기존 context와 혼용하지 않고, 진단과 context 재사용 판단에 사용한다. 이 구조에는 GGUF 경로, 모델 family, chat template, sampling 값이 없다. 그런 모델별 차이는 `ModelProfile`과 adapter 경계에 남긴다.

## Prompt로 조합되는 순서

`PromptCompiler`는 template 문자열을 직접 만들지 않고, 의미 있는 `ModelMessage` 목록을 만든다. 비어 있는 선택 section은 생략되며 순서는 고정이다.

```text
system
→ personality
→ style
→ scenario
→ lore (파일명 ordinal 순서)
→ output-boundary policy (기본 assistant 말투 대체 금지 및 지시/추론 비노출)
→ examples
→ memory (현재 post-MVP, 아직 없음)
→ 완료된 conversation turns
→ 현재 user input
```

이 메시지 목록은 `ModelRequest`로 adapter에 전달되고, 선택된 `ModelProfile`을 아는 adapter가 Qwen 등 모델의 chat template로 직렬화한다. 따라서 캐릭터 파일에 `<think>`, Qwen 전용 token, llama.cpp 옵션을 넣어 동작에 의존하지 않는다.

`CharacterPromptPolicy`는 bundle data와 분리된 composition 설정이다. 기본값은 character의 personality/style 및 예시를 일반적인 assistant 말투보다 우선시키고 지시·추론의 노출을 금지한다. 모델의 reasoning on/off와 sampling은 캐릭터 bundle이 아니라 `ModelProfile`의 책임이다.

Conversation은 user turn을 pending으로 시작한다. streaming이 성공하면 user와 assistant 응답을 함께 committed history로 저장하고, 취소·오류면 pending turn을 rollback한다. context budget을 넘으면 오래된 완료 turn부터 user/assistant 쌍 단위로 제거한다. system/character section과 최신 user input도 담을 수 없으면 `ContextBudgetExceeded` 오류가 난다.

## 검증 및 보안 규칙

- 모든 텍스트 파일은 strict UTF-8이다. 잘못된 byte sequence는 거부한다.
- 지원하지 않는 schema version, 필수 파일 누락, 빈 ID/display name, 잘못된 JSON 또는 JSONL은 거부한다.
- loader는 bundle root와 후보 파일을 canonical path로 확인한다. absolute path, root 탈출, symlink/reparse point 및 symlink root/lore directory를 허용하지 않는다.
- `lore/`에는 디렉터리나 symlink를 둘 수 없으며 `.md` 파일만 읽는다.
- 기본 상한은 파일당 256 KiB, bundle 전체 1 MiB다. loader 생성 시 테스트 목적에 맞게 더 작은 상한을 줄 수 있지만 무제한 읽기는 지원하지 않는다.
- `CharacterCatalog`에서 ID는 Unicode NFC 정규화 후 대소문자를 구분하지 않고 전역 유일해야 한다. 예: `Haru`와 `haru`는 함께 등록할 수 없다.
- bundle의 Markdown/JSON은 data-only다. 파일 경로 지시, 코드 실행, tool 권한, filesystem/network 접근 권한으로 해석되지 않는다.

## 테스트 방법과 Flutter probe 범위

Unity 없이 Core 흐름을 확인하려면 headless 도구를 사용한다. 기본은 deterministic mock adapter이며 실제 모델 파일이 필요 없다.

```bash
dotnet run --project tools/headless/HaruChat.Headless.csproj -- \
  <character-bundle-dir> "안녕하세요"
```

실제 GGUF로 확인하려면 model/profile을 함께 명시한다.

```bash
dotnet run --project tools/headless/HaruChat.Headless.csproj -- \
  <character-bundle-dir> "안녕하세요" \
  --model <absolute-gguf-path> --profile <profile.json>
```

Flutter probe는 P3/P4 native ABI의 load/generate/cancel/reset/unload을 진단하는 **테스트 host**다. `Character test bench`의 **Create & add**는 앱 문서 영역의 `HaruChatProbe/characters/<id>/`에 위 v1 파일 구조를 생성하고 selector에 등록한다. 선택한 character의 **character 편집**은 v1의 고정 파일(`manifest.json`, Markdown section, `lore/*.md`, `examples.jsonl`)만 수정하며, 비운 선택 항목은 bundle에서 제거한다. ID는 디렉터리명과의 일치를 보장하기 위해 기존 bundle에서 바꿀 수 없다. 선택한 character의 section과 examples는 probe가 생성하는 raw diagnostic prompt 앞에 붙는다.

이 기능은 bundle 파일·기본 instruction을 빠르게 점검하기 위한 test fixture 도구다. Core loader의 전체 검증, profile 기반 chat template, conversation commit과 Unity의 제품 character UX를 대체하지 않는다. 최종 picker와 production lifecycle은 P6 Unity Presentation 범위다.
