cmake_minimum_required(VERSION 3.22)
project(HaruChatNativeProbe LANGUAGES OBJC OBJCXX)

set(CMAKE_OBJCXX_STANDARD 17)
set(CMAKE_OBJCXX_STANDARD_REQUIRED ON)

set(PROBE_VENDOR_DIR "${CMAKE_CURRENT_SOURCE_DIR}/Vendor")
set(PROBE_LIBRARY "${PROBE_VENDOR_DIR}/LlmCore.xcframework/ios-arm64/libLlmCore.a")
set(PROBE_HEADERS "${PROBE_VENDOR_DIR}/LlmCore.xcframework/ios-arm64/Headers")
if(NOT EXISTS "${PROBE_LIBRARY}" OR NOT EXISTS "${PROBE_HEADERS}/hc_llm.h")
  message(FATAL_ERROR "Run scripts/prepare-native-probe.sh with the verified Phase 2 artifact first")
endif()

add_executable(HaruChatNativeProbe MACOSX_BUNDLE
  Probe/main.m
  Probe/AppDelegate.mm
  Probe/AppDelegate.h
)
target_include_directories(HaruChatNativeProbe PRIVATE "${PROBE_HEADERS}")
target_link_libraries(HaruChatNativeProbe PRIVATE "${PROBE_LIBRARY}" "-framework UIKit" "-framework Foundation" "-framework UniformTypeIdentifiers" "-framework Accelerate" "-framework Metal" "-framework MetalKit")
set_target_properties(HaruChatNativeProbe PROPERTIES
  MACOSX_BUNDLE_GUI_IDENTIFIER "org.haruchat.NativeProbe"
  MACOSX_BUNDLE_BUNDLE_NAME "HaruChat Native Probe"
  XCODE_ATTRIBUTE_IPHONEOS_DEPLOYMENT_TARGET "15.0"
  XCODE_ATTRIBUTE_TARGETED_DEVICE_FAMILY "1,2"
  XCODE_ATTRIBUTE_CODE_SIGN_STYLE "Automatic"
  XCODE_ATTRIBUTE_PRODUCT_BUNDLE_IDENTIFIER "org.haruchat.NativeProbe"
)
