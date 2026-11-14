import Foundation
import Darwin
// 只依赖 Command Line Tools；断言失败以非零状态结束，不要求用户安装完整 Xcode。
private let failureLock = NSLock()
private var failures = 0
func XCTFail(_ text: String, file: StaticString = #file, line: UInt = #line) {
    failureLock.lock(); failures += 1; print("FAIL \(file):\(line) \(text)"); failureLock.unlock()
}
func XCTAssertEqual<T: Equatable>(_ lhs: @autoclosure () throws -> T, _ rhs: @autoclosure () throws -> T, file: StaticString = #file, line: UInt = #line) {
    do { if try lhs() != rhs() { XCTFail("values differ",file:file,line:line) } } catch { XCTFail("unexpected \(error)",file:file,line:line) }
}
func XCTAssertTrue(_ value: @autoclosure () throws -> Bool, file: StaticString = #file, line: UInt = #line) {
    do { if try !value() { XCTFail("expected true",file:file,line:line) } } catch { XCTFail("unexpected \(error)",file:file,line:line) }
}
func XCTAssertGreaterThan<T: Comparable>(_ lhs: @autoclosure () throws -> T, _ rhs: T, file: StaticString = #file, line: UInt = #line) {
    do { if try lhs() <= rhs { XCTFail("expected greater",file:file,line:line) } } catch { XCTFail("unexpected \(error)",file:file,line:line) }
}
func XCTAssertThrowsError<T>(_ value: @autoclosure () throws -> T, file: StaticString = #file, line: UInt = #line) {
    do { _ = try value(); XCTFail("expected failure but succeeded",file:file,line:line) } catch { }
}
func XCTUnwrap<T>(_ value: T?) throws -> T {
    guard let value else { throw APAError.message("Expected non-nil value") }; return value
}
final class CheckExpectation {
    let semaphore = DispatchSemaphore(value:0)
    func fulfill() { semaphore.signal() }
}
enum CheckSkip: Error { case unavailable(String) }
class CheckSuite {
    func expectation(description: String) -> CheckExpectation { CheckExpectation() }
    func wait(for values: [CheckExpectation], timeout: TimeInterval) {
        for value in values { if value.semaphore.wait(timeout:.now()+timeout) == .timedOut { XCTFail("mock server did not finish") } }
    }
}
@main struct RunChecks {
    static func main() {
        let tests = CoreTests()
        let cases: [(String,() throws -> Void)] = [
            ("953728 MiB must be rejected before any write",tests.testCreateRejects953728MiBBeforeAnyWrite),
            ("existing partition name rejects creation without writes",tests.testCreateRejectsExistingPartitionBeforeAnyWrite),
            ("APA allocation geometry, 64 slots and 2TB",tests.testAllocationGeometryAndSlotLimits),
            ("UTF8 and truncated frames",tests.testUTF8RoundTripAndTruncatedStrings),
            ("Paths and command injection",tests.testPathsCannotEscapeDestinationOrInjectShellCommands),
            ("Unicode local listing and >4GiB",tests.testPfsshellListingPreservesUnicodeWhitespaceAndLargeSize),
            ("APA checksum cycle and bounds",tests.testAPAChecksumCycleAndBoundsAreRejected),
            ("Fragmented RPC and invalid count",tests.testFragmentedControlAndMalformedDirectoryCount),
            ("Binary PUT/GET and commit ordering",tests.testUploadDownloadBinaryIntegrityAndCommitOrdering),
            ("Cancel RST then ABORT then next RPC",tests.testCancelledUploadResetsDataBeforeAbortAndKeepsControlUsable),
            ("Failed GET preserves destination",tests.testFailedDownloadPreservesExistingDestination),
            ("Resume after data reset",tests.testResumeUsesServerOffsetAfterDataReset),
            ("Local APA/PFS image lifecycle",tests.testLocalImageEndToEnd)
        ]
        for (name,run) in cases {
            failureLock.lock(); let before = failures; failureLock.unlock()
            do { try run() }
            catch CheckSkip.unavailable(let reason) { print("SKIP \(name): \(reason)"); continue }
            catch { XCTFail("\(name): \(error)") }
            failureLock.lock(); let after = failures; failureLock.unlock()
            print("\(after == before ? "PASS":"FAIL") \(name)")
        }
        failureLock.lock(); let count = failures; failureLock.unlock()
        print("\(cases.count) checks, \(count) failures")
        exit(count == 0 ? 0 : 1)
    }
}
