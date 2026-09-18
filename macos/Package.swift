// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "Evict",
    platforms: [.macOS(.v13)],
    products: [
        .library(name: "EvictKit", targets: ["EvictKit"]),
        .executable(name: "Evict", targets: ["EvictApp"]),
    ],
    targets: [
        .target(name: "EvictKit"),
        .executableTarget(name: "EvictApp", dependencies: ["EvictKit"]),
        .testTarget(name: "EvictKitTests", dependencies: ["EvictKit"]),
    ]
)
