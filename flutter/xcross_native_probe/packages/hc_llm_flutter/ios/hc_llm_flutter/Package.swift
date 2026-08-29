// swift-tools-version: 5.9
import PackageDescription

let package = Package(
  name: "hc_llm_flutter",
  platforms: [.iOS(.v15)],
  products: [.library(name: "hc_llm_flutter", targets: ["hc_llm_flutter"])],
  targets: [
    .binaryTarget(name: "LlmCore", path: "../../../../vendor/LlmCore.xcframework"),
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
