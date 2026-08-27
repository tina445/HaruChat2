# HaruChat2

HaruChat2는 iOS/iPadOS에서 동작하는 로컬 LLM 기반 AI 캐릭터 챗 애플리케이션이다. 단일 채팅 UI보다 모델, 캐릭터, Memory, Agent, Unity/Live2D 표현을 서로 교체 가능한 경계로 분리한 AI Character Runtime을 목표로 한다.

## MVP

첫 MVP는 M4 iPad에서 다음 vertical slice를 완성하는 것이다.

```text
Unity application
  → character selection
  → local Qwen GGUF
  → llama.cpp + Metal
  → character instruction
  → streaming response
```

Memory, Agent, Live2D, 외부 LLM provider는 후속 단계이며 MVP의 필수 구현 대상이 아니다.

## 현재 상태

프로젝트는 구현 전 설계 단계다. 기능 코드를 시작하기 전에 요구사항, 아키텍처, 개발환경, 로드맵과 주요 기술 결정을 문서로 확정한다.

## 문서

- [프로젝트 작업 지침](AGENTS.md)
- [기능·비기능 요구사항](docs/REQUIREMENTS.md)
- [아키텍처](docs/ARCHITECTURE.md)
- [개발환경과 빌드](docs/DEVELOPMENT.md)
- [단계별 로드맵](docs/ROADMAP.md)
- [Architecture Decision Records](docs/adr/README.md)

## 핵심 원칙

- 로컬 LLM 구현과 Character/Agent Runtime을 분리한다.
- 모델별 규칙과 inference backend를 분리한다.
- Unity는 Presentation 계층으로 제한한다.
- llama.cpp는 pinned upstream dependency로 유지한다.
- Linux를 주 개발환경으로 사용하고 Apple 전용 빌드만 macOS CI에 위임한다.
- 모델과 개인 데이터는 기본적으로 device-local로 유지한다.

## 라이선스

프로젝트 코드는 [MIT License](LICENSE)를 따른다. 모델, llama.cpp, Live2D Cubism 등 외부 dependency와 asset은 각각의 라이선스를 별도로 확인해야 한다.
