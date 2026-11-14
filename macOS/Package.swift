// swift-tools-version: 5.9
import PackageDescription
let package = Package(
    name: "APAFileManagerMac",
    platforms: [.macOS(.v12)],
    products: [.executable(name: "APAFileManager", targets: ["APANative"])],
    targets: [
        .target(name: "APACore"),
        .executableTarget(name: "APANative", dependencies: ["APACore"])
    ]
)
