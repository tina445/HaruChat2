// swift-tools-version: 5.9
import PackageDescription

let package = Package(
  name: "hc_llm_flutter",
  // xcross builds its generated plugin aggregate with iOS 13. The linked
  // LlmCore artifact and this probe still require an iOS 15+ device; this
  // declaration only prevents SwiftPM from rejecting the aggregate first.
  platforms: [.iOS(.v13)],
  // xcross derives the SwiftPM product name from Flutter's hyphenated package
  // name, while the Swift target keeps its underscore identifier.
  products: [.library(name: "hc-llm-flutter", targets: ["hc_llm_flutter"])],
  targets: [
    // xcross stages path plugins before invoking SwiftPM. Keep the artifact
    // within this package so the relative binary target survives that staging.
    .binaryTarget(name: "LlmCore", path: "Vendor/LlmCore.xcframework"),
    // SwiftPM links a static-library XCFramework binary target but doesn't
    // automatically make its C headers importable from Swift. This C target
    // exposes the same header as a Clang module and retains the binary target.
    .target(
      name: "LlmCoreBridge",
      dependencies: ["LlmCore"],
      path: "Sources/LlmCoreBridge",
      publicHeadersPath: "include",
      cSettings: [
        .headerSearchPath("../../../Vendor/LlmCore.xcframework/ios-arm64/Headers"),
      ]
    ),
    .target(
      name: "hc_llm_flutter",
      dependencies: ["LlmCore", "LlmCoreBridge"],
      path: "Sources/hc_llm_flutter",
      linkerSettings: [
        .linkedFramework("Accelerate"),
        .linkedFramework("Metal"),
        .linkedFramework("MetalKit"),
        .linkedFramework("UniformTypeIdentifiers"),
        .linkedLibrary("c++"),
      ]
    ),
  ]
)
