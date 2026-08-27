# 개발 환경 및 빌드 전략

## 1. 목적과 범위

이 문서는 HaruChat2를 Linux에서 개발하고, Apple 전용 산출물만 macOS CI에서 만드는 기준 절차를 정의한다. 현재 저장소는 설계 단계이므로 아래 명령은 해당 Phase의 파일이 추가된 뒤 유효해지는 목표 인터페이스다.

핵심 원칙은 다음과 같다.

- 일상 개발, managed unit test, native CPU build와 대부분의 통합 테스트는 Linux에서 수행한다.
- `llama.cpp`는 pin된 Git submodule로 관리하며 프로젝트 코드는 thin C ABI wrapper 바깥에 둔다.
- Apple SDK, Metal 및 XCFramework 생성에만 macOS CI를 사용한다.
- Unity는 Presentation 계층이며 Core Runtime의 빌드나 테스트를 위해 필요하지 않아야 한다.
- GGUF, API key, signing credential과 생성 데이터는 Git에 커밋하지 않는다.

## 2. 기준 도구 체인

| 영역 | 기준 | 비고 |
|---|---|---|
| 개발 OS | Linux x86_64 | 주 개발 및 테스트 환경 |
| Managed SDK | .NET SDK 10 | 테스트 프로젝트는 `net10.0`, Unity 공유 라이브러리는 `netstandard2.1` 호환 API만 사용 |
| Native build | CMake + Ninja | out-of-source build만 허용 |
| C/C++ | Clang 또는 GCC | wrapper는 C++17, 외부 ABI는 C11 호환 header |
| Presentation | Unity 6.3 LTS | 정확한 patch version은 프로젝트 생성 시 고정하고 CI/개발환경에서 동일하게 사용 |
| Apple build | Apple Silicon macOS CI, Xcode/CMake | Xcode 및 SDK version을 CI image에 고정 |
| Local DB | SQLite + FTS5 | system SQLite 차이를 피하기 위해 실제 배포 구성과 동일한 provider로 통합 테스트 |

Unity가 사용하는 런타임과 .NET 10은 동일하지 않다. Domain/Application 소스는 Unity가 지원하는 `netstandard2.1` API 범위로 제한하고, Linux의 test host와 도구는 .NET 10을 사용한다. Unity 전용 assembly는 Core assembly를 참조할 수 있지만 역방향 참조는 금지한다.

권장 설치 확인:

```bash
dotnet --info
cmake --version
ninja --version
clang --version
git --version
```

Unity 6.3 LTS는 Unity Hub 또는 공식 Linux 배포판으로 설치한다. 저장소에 editor binary나 license 파일을 넣지 않는다. Unity batch test를 실행할 때는 `UNITY_EDITOR_PATH`로 고정된 editor executable을 가리킨다.

## 3. 저장소 초기화와 `llama.cpp`

`llama.cpp`는 `native/third_party/llama.cpp`에 Git submodule로 추가하고 검증된 upstream commit을 superproject에서 pin한다.

```bash
git submodule sync --recursive
git submodule update --init --recursive
git -C native/third_party/llama.cpp rev-parse HEAD
```

운영 규칙:

- upstream source를 프로젝트 기능 때문에 직접 수정하지 않는다.
- CMake option, wrapper 또는 별도 patch가 꼭 필요하면 먼저 ADR을 추가한다. 일시적인 patch는 `native/patches/`에 출처, 이유와 upstream issue를 기록한다.
- submodule update는 별도 변경으로 수행하고 Linux CPU test 및 Apple CI를 모두 통과시킨다.
- pin된 commit과 기대 artifact checksum을 build log에 남긴다.
- 개발자 편의를 위한 최신 branch 추적이나 CI의 implicit submodule update를 금지한다.

## 4. Linux 빌드와 테스트

### 4.1 Managed Core

예정된 solution을 복원, 빌드, 테스트하는 표준 명령은 다음과 같다.

```bash
dotnet restore HaruChat.slnx --locked-mode
dotnet build HaruChat.slnx -c Release --no-restore
dotnet test HaruChat.slnx -c Release --no-build
```

NuGet lock file을 커밋하고 CI에서는 `--locked-mode`를 사용한다. 테스트는 실제 LLM 없이 `MockModelAdapter`, 임시 SQLite database와 deterministic clock/random 구현으로 재현 가능해야 한다.

### 4.2 Native wrapper

```bash
cmake -S native -B native/build -G Ninja \
  -DCMAKE_BUILD_TYPE=Debug \
  -DLLMCORE_BUILD_TESTS=ON \
  -DGGML_METAL=OFF
cmake --build native/build
ctest --test-dir native/build --output-on-failure
```

기본 Linux CI는 CPU backend로 ABI lifecycle, load failure, context lifecycle, event ordering, cancellation과 unload/reload를 검사한다. sanitizer job은 지원되는 compiler에서 AddressSanitizer와 UndefinedBehaviorSanitizer를 활성화한다.

실제 GGUF smoke test는 선택적이고 다음 환경변수로 명시적으로 활성화한다.

```bash
export HARUCHAT_TEST_MODEL_PATH=/absolute/path/to/model.gguf
ctest --test-dir native/build -L model-smoke --output-on-failure
```

모델 경로가 없으면 일반 test suite는 계속 통과해야 하며 `model-smoke`만 명시적으로 skip한다. CI에 GGUF를 포함할 경우 승인된 URL과 SHA-256으로 cache를 채우고 checksum 불일치 시 즉시 실패한다.

### 4.3 Unity EditMode test

Unity 프로젝트가 생성된 뒤 Presentation adapter와 serialization contract는 batch mode로 검사한다.

```bash
"$UNITY_EDITOR_PATH" \
  -batchmode -nographics -quit \
  -projectPath unity/HaruChat \
  -runTests -testPlatform EditMode \
  -testResults artifacts/unity-editmode.xml
```

Unity license 활성화는 개발자/CI 환경의 책임이며 credential을 저장소에 두지 않는다. native core CI를 Unity license나 Unity Build Automation에 종속시키지 않는다.

## 5. macOS CI와 Apple 산출물

초기 Apple job은 Codemagic Apple Silicon runner를 우선 사용하며 GitHub Actions macOS는 대체 runner다. 이 job은 Unity 전체 앱이 아니라 native core만 다음 순서로 검증한다.

1. submodule을 pin된 commit으로 checkout한다.
2. CMake와 Apple clang으로 iOS arm64 및 필요 시 arm64 simulator slice를 빌드한다.
3. `GGML_METAL=ON`으로 Metal source/shader compile 및 link를 검증한다.
4. public C header와 library를 `LlmCore.xcframework`로 조립한다.
5. 최소 native link smoke target을 빌드한다.
6. unsigned XCFramework zip, SHA-256, compiler/Xcode/SDK/llama.cpp commit metadata를 artifact로 게시한다.

XCFramework 자체는 code signing하지 않는다. 첫 CI 목표는 재사용 가능한 **unsigned Apple native artifact**의 재현 가능한 생성이지, 서명된 IPA나 App Store/TestFlight 배포가 아니다. Codemagic secret에는 필요해지기 전까지 signing certificate나 provisioning profile을 추가하지 않는다.

CI가 보장하는 항목:

- Apple clang, iOS SDK 및 Metal을 사용하는 compile/link 성공
- 기대 architecture와 public symbol을 가진 XCFramework 생성
- artifact provenance와 checksum 기록

CI가 보장하지 않는 항목:

- Personal Team provisioning 성공
- M4 iPad 설치 또는 실행
- Unity-generated Xcode project의 서명
- Metal runtime 활성화와 실제 device 성능

## 6. Phase 0 서명 가능성 Gate

무료 Apple ID의 Personal Team은 일반적으로 짧은 provisioning 유효기간과 제한된 capability를 가지며, 대화형 Xcode가 certificate/device/profile을 관리하는 흐름에 의존할 수 있다. 유료 Apple Developer Program 계정과 로컬 Mac이 없는 상황에서 **macOS CI만으로 Personal Team 앱을 서명해 M4 iPad에 설치할 수 있다고 가정하거나 보장하지 않는다.**

기능 개발에 앞서 Phase 0에서 다음 Gate를 수행한다.

1. 사용할 Apple ID 유형, device 등록 가능 여부와 Unity iOS export 요구사항을 확인한다.
2. Codemagic에서 이용 가능한 signing material과 Personal Team provisioning 지원 여부를 확인하되 credential을 source/log에 노출하지 않는다.
3. 빈 native 또는 최소 Unity 앱을 대상으로 실제 M4 iPad 설치·실행 경로를 검증한다.
4. 성공한 경우 certificate/profile의 보관 위치, 만료/갱신 절차와 7일 재설치 비용을 기록한다.
5. 실패하거나 CI-only 경로가 불가능하면 device milestone의 선행조건으로 아래 대안 중 하나를 채택한다.
   - 일시적으로 접근 가능한 Mac에서 Xcode와 Personal Team을 사용해 설치한다.
   - 유료 Apple Developer Program 계정과 CI-compatible signing을 사용한다.

Gate 실패는 Linux core와 unsigned XCFramework 개발을 막지 않지만, "M4 iPad에서 실행" MVP 완료 조건은 충족할 수 없다. 실기기 설치를 simulator나 unsigned artifact 생성으로 대체해 완료 처리하지 않는다.

## 7. 모델, 설정과 비밀정보

GGUF는 source code에 hard-code하거나 Git에 커밋하지 않는다. model catalog에는 논리 ID, family/profile ID, 파일명, 예상 byte size와 SHA-256만 저장하고 실제 경로는 runtime 설정으로 주입한다. 기본 후보는 Qwen3.5 4B의 모바일용 GGUF이지만 정확한 quantization은 device 검증 결과에 따라 교체할 수 있어야 한다.

표준 환경변수:

| 이름 | 용도 | 비밀 여부 |
|---|---|---|
| `HARUCHAT_TEST_MODEL_PATH` | 로컬 native smoke test용 GGUF 절대경로 | 아니요. 단, machine-local 값 |
| `HARUCHAT_MODEL_CACHE_DIR` | 선택적 model download/cache 위치 | 아니요 |
| `HARUCHAT_REMOTE_API_KEY` | 향후 remote adapter 개발 키 | 예 |
| `HARUCHAT_REMOTE_BASE_URL` | 향후 OpenAI-compatible endpoint | 환경에 따라 다름 |
| `UNITY_EDITOR_PATH` | Linux Unity editor executable | 아니요 |

`.env`는 machine-local로만 사용하고 `.env.example`에는 빈 값과 설명만 둔다. CI secret은 Codemagic encrypted environment group에 저장하고 pull request build에는 노출하지 않는다. API key는 character bundle, `ModelProfile`, log, crash report 또는 SQLite memory에 저장하지 않으며, device에서는 platform secure-storage abstraction 뒤에 둔다.

## 8. 재현성과 진단

- .NET dependency lock, Unity editor patch version, Xcode image, CMake options, submodule commit을 고정한다.
- native artifact에는 ABI version, Git commit, build type, target triple과 llama.cpp commit을 기록한다.
- 로그에는 prompt/memory 원문이나 API key를 기본 출력하지 않는다.
- Linux, Apple CI와 device test 결과는 model profile, GGUF checksum, context size, GPU layer 설정을 함께 기록한다.
- build output은 source tree와 분리하고 생성된 XCFramework, Unity build, database와 model을 커밋하지 않는다.

## 9. 로컬 변경 검증 체크리스트

변경 범위에 따라 아래 최소 집합을 실행한다.

- Managed domain/application 변경: `dotnet test`
- Native/C ABI 변경: native build, `ctest`, sanitizer, exported symbol/ABI 확인
- llama.cpp pin 또는 CMake 변경: Linux model smoke와 Apple CI
- Unity adapter 변경: managed test와 Unity EditMode test
- SQLite schema 변경: migration upgrade/rollback이 아니라 forward migration, 빈 DB 및 이전 지원 version fixture test
- Apple artifact 변경: unsigned XCFramework build와 최소 link smoke

실제 device에서만 검증 가능한 Metal runtime, memory pressure, thermal behavior와 signing은 별도 device 결과로 표시한다.

## 10. 공식 참고 자료

- [.NET 지원 정책](https://dotnet.microsoft.com/en-us/platform/support/policy)
- [Unity 6 release와 LTS 지원](https://unity.com/releases/unity-6)
- [Unity의 .NET profile 지원](https://docs.unity3d.com/6000.0/Documentation/Manual/dotnet-profile-support.html)
- [llama.cpp upstream](https://github.com/ggml-org/llama.cpp)
- [llama.cpp XCFramework build script](https://github.com/ggml-org/llama.cpp/blob/master/build-xcframework.sh)
- [Codemagic 가격과 무료 사용량](https://docs.codemagic.io/billing/pricing/)
