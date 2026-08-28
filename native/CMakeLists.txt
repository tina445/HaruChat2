cmake_minimum_required(VERSION 3.22)

project(HaruChatLlmCore VERSION 0.1.0 LANGUAGES C CXX)

option(LLMCORE_BUILD_TESTS "Build llmcore native tests" ON)
option(LLMCORE_ENABLE_SANITIZERS "Enable AddressSanitizer and UndefinedBehaviorSanitizer" OFF)
option(LLMCORE_DISABLE_LEAK_SANITIZER "Disable LeakSanitizer for ptrace-restricted runners" OFF)
option(LLMCORE_ENABLE_LLAMA_CPP "Enable the pinned llama.cpp backend when its submodule is available" OFF)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(ANDROID AND NOT CMAKE_ANDROID_ARCH_ABI STREQUAL "arm64-v8a")
  message(FATAL_ERROR "HaruChat's prepared Android target supports arm64-v8a only")
endif()

add_library(llmcore SHARED
  llmcore/src/hc_llm.cpp
)
add_library(HaruChat::llmcore ALIAS llmcore)

target_include_directories(llmcore
  PUBLIC
    $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/llmcore/include>
    $<INSTALL_INTERFACE:include>
)
target_compile_definitions(llmcore PRIVATE HC_LLM_BUILDING_LIBRARY=1)
target_compile_options(llmcore PRIVATE $<$<COMPILE_LANGUAGE:CXX>:-Wall -Wextra -Wpedantic>)

if(LLMCORE_ENABLE_SANITIZERS)
  if(CMAKE_CXX_COMPILER_ID MATCHES "Clang|GNU")
    target_compile_options(llmcore PRIVATE -fsanitize=address,undefined -fno-omit-frame-pointer)
    target_link_options(llmcore PRIVATE -fsanitize=address,undefined)
  else()
    message(WARNING "LLMCORE_ENABLE_SANITIZERS is only configured for Clang/GNU")
  endif()
endif()

# Kept opt-in until the superproject provides the pinned submodule.  The public
# C ABI intentionally does not change when the inference implementation does.
if(LLMCORE_ENABLE_LLAMA_CPP)
  if(NOT EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/third_party/llama.cpp/CMakeLists.txt")
    message(FATAL_ERROR "LLMCORE_ENABLE_LLAMA_CPP requires native/third_party/llama.cpp")
  endif()
  set(LLAMA_BUILD_IS_DEV OFF CACHE BOOL "" FORCE)
  set(LLAMA_BUILD_COMMON OFF CACHE BOOL "" FORCE)
  set(LLAMA_BUILD_TESTS OFF CACHE BOOL "" FORCE)
  set(LLAMA_BUILD_TOOLS OFF CACHE BOOL "" FORCE)
  set(LLAMA_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
  set(LLAMA_BUILD_SERVER OFF CACHE BOOL "" FORCE)
  set(LLAMA_BUILD_APP OFF CACHE BOOL "" FORCE)
  set(LLAMA_BUILD_UI OFF CACHE BOOL "" FORCE)
  add_subdirectory(third_party/llama.cpp EXCLUDE_FROM_ALL)
  target_link_libraries(llmcore PRIVATE llama)
  target_compile_definitions(llmcore PRIVATE HC_LLM_WITH_LLAMA_CPP=1)
endif()

if(LLMCORE_BUILD_TESTS)
  enable_testing()
  add_executable(llmcore_tests ../tests/native/llmcore_tests.cpp)
  target_link_libraries(llmcore_tests PRIVATE llmcore)
  target_include_directories(llmcore_tests PRIVATE llmcore/include)
  if(LLMCORE_ENABLE_SANITIZERS AND CMAKE_CXX_COMPILER_ID MATCHES "Clang|GNU")
    target_compile_options(llmcore_tests PRIVATE -fsanitize=address,undefined -fno-omit-frame-pointer)
    target_link_options(llmcore_tests PRIVATE -fsanitize=address,undefined)
  endif()
  add_test(NAME llmcore.lifecycle COMMAND llmcore_tests)
  if(LLMCORE_ENABLE_LLAMA_CPP)
    # llmcore.lifecycle deliberately uses a tiny fake GGUF to exercise the
    # model-free bootstrap backend.  Do not pass that fixture to real
    # llama.cpp; the opt-in model-smoke target owns real-GGUF validation.
    set_tests_properties(llmcore.lifecycle PROPERTIES DISABLED TRUE)
  endif()

  add_executable(llmcore_c_header_smoke ../tests/native/c_header_smoke.c)
  target_link_libraries(llmcore_c_header_smoke PRIVATE llmcore)
  target_include_directories(llmcore_c_header_smoke PRIVATE llmcore/include)
  set_target_properties(llmcore_c_header_smoke PROPERTIES C_STANDARD 11 C_STANDARD_REQUIRED YES)
  if(LLMCORE_ENABLE_SANITIZERS AND CMAKE_CXX_COMPILER_ID MATCHES "Clang|GNU")
    # The executable must link the ASan runtime before the instrumented shared
    # library, including when its consumer is C rather than C++.
    target_link_options(llmcore_c_header_smoke PRIVATE -fsanitize=address,undefined)
  endif()
  add_test(NAME llmcore.c_header_smoke COMMAND llmcore_c_header_smoke)

  add_executable(llmcore_model_smoke ../tests/native/model_smoke.cpp)
  target_link_libraries(llmcore_model_smoke PRIVATE llmcore)
  target_include_directories(llmcore_model_smoke PRIVATE llmcore/include)
  if(LLMCORE_ENABLE_SANITIZERS AND CMAKE_CXX_COMPILER_ID MATCHES "Clang|GNU")
    target_compile_options(llmcore_model_smoke PRIVATE -fsanitize=address,undefined -fno-omit-frame-pointer)
    target_link_options(llmcore_model_smoke PRIVATE -fsanitize=address,undefined)
  endif()
  add_test(NAME llmcore.model_smoke COMMAND llmcore_model_smoke)
  set_tests_properties(llmcore.model_smoke PROPERTIES LABELS "model-smoke" SKIP_RETURN_CODE 77)
  if(LLMCORE_ENABLE_SANITIZERS AND LLMCORE_DISABLE_LEAK_SANITIZER)
    # Some sandboxed test runners use ptrace and cannot run LeakSanitizer.
    # ASan/UBSan remain enabled; normal host and CI builds keep LSan enabled.
    set_tests_properties(llmcore.lifecycle llmcore.c_header_smoke llmcore.model_smoke
      PROPERTIES ENVIRONMENT "ASAN_OPTIONS=detect_leaks=0")
  endif()
endif()

install(TARGETS llmcore EXPORT HaruChatLlmCoreTargets)
install(FILES llmcore/include/hc_llm.h DESTINATION include)
