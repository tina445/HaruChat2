// swift-tools-version: 5.9
import PackageDescription

let package = Package(
  name: "hc_llm_flutter",
  platforms: [.iOS(.v15)],
  // xcross derives the SwiftPM product name from Flutter's hyphenated package
  // name, while the Swift target keeps its underscore identifier.
  products: [.library(name: "hc-llm-flutter", targets: ["hc_llm_flutter"])],
  targets: [
    // xcross stages path plugins before invoking SwiftPM. Keep the artifact
    // within this package so the relative binary target survives that staging.
    .binaryTarget(name: "LlmCore", path: "Vendor/LlmCore.xcframework"),
    .target(
      name: "hc_llm_flutter",
      dependencies: ["LlmCore"],
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
