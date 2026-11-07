import Foundation
import EvictKit
#if os(macOS)
import SwiftUI
import Darwin

if CommandLine.arguments.contains("--scheduled-scan") {
    exit(ScheduledScanRunner.run())
} else {
    EvictApp.main()
}
#else
// The portable core and fixtures can be tested on Linux. The native UI requires macOS.
print("Evict's desktop application requires macOS 14 or later.")
#endif
