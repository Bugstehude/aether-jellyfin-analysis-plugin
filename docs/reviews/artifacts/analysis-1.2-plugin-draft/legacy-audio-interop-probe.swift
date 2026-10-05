import Foundation

// Small offline import proof. No playback, network, latency or RSS benchmark.
let input = URL(fileURLWithPath: CommandLine.arguments[1])
let expectedData = try Data(contentsOf: input.appendingPathComponent("composition-wire-expected.json"))
let expected = try JSONDecoder().decode([String: AnalysisJSONValue].self, from: expectedData)
var results: [[String: Any]] = []
var full: AnalysisPackedDenseAudioFrames?
var fullNormalization: AnalysisAudioNormalization?
var fullLegacy: AnalysisJSONValue?
for detail in ["full", "balanced", "compact"] {
    let data = try Data(contentsOf: input.appendingPathComponent("composed-\(detail).json"))
    let document = try AnalysisDocumentDecoder.decode(data)
    let raw = try JSONDecoder().decode([String: AnalysisJSONValue].self, from: data)
    let exported = try JSONEncoder().encode(document)
    let roundtrip = try JSONDecoder().decode([String: AnalysisJSONValue].self, from: exported)
    let packed = document.packedDenseAudioFrames!
    if detail == "full" { full = packed; fullLegacy = raw["audioFrames"] }
    guard case .object(let wire)? = expected[detail],
          case .string(let base64)? = wire["dataBase64"],
          case .string(let checksum)? = wire["checksum"] else { fatalError("expected wire") }
    let wireExact = packed.recordBytes == Data(base64Encoded: base64) && packed.checksum == checksum
    let reductionExact = try detail == "full" || full!.reduce(bucketMs: detail == "balanced" ? 50 : 100).recordBytes == packed.recordBytes
    let legacyExact = ["frames", "audioFrames", "sampling", "representation", "item", "algorithm"]
        .allSatisfy { raw[$0] == roundtrip[$0] }
    let opaqueExact = document.unknownFields.allSatisfy { roundtrip[$0.key] == $0.value }
    let metadataExact = document.originalMetadata.allSatisfy { roundtrip[$0.key] == $0.value }
    let normalized = document.audioAnalysis!.normalization!
    let peaksExact: Bool
    if detail == "full" {
        fullNormalization = normalized
        peaksExact = packed.map(\.rmsLinear).max() == normalized.peakRmsLinear
            && packed.map(\.spectralFluxLinear).max() == normalized.peakFluxLinear
    } else {
        peaksExact = normalized == fullNormalization
    }
    precondition(document.extensionDiagnostics.isEmpty && wireExact && reductionExact && legacyExact
        && opaqueExact && metadataExact && peaksExact && raw["audioFrames"] == fullLegacy && document.audioFrames?.count == 4)
    results.append([
        "detail": detail, "bytes": data.count, "sha256": AnalysisSHA256.hexDigest(data),
        "diagnostics": document.extensionDiagnostics.map { ["group": $0.group, "reason": $0.reason] },
        "timebaseRetained": document.timebase != nil, "audioRetained": document.audioAnalysis != nil,
        "cutsRetained": document.cutAnalysis != nil, "sourceFrameCount": document.timebase!.video.sourceTimeline!.frameCount,
        "sourceLastMs": document.timebase!.video.sourceTimeline!.lastTimestampMs,
        "lastRepresentationFrameMs": document.frames.last!.timestampMs,
        "cutCoverageEndMs": document.cutAnalysis!.coverage!.last!.endMs,
        "packedPoints": packed.count, "wireChecksum": packed.checksum,
        "wireExact": wireExact, "fullReductionExact": reductionExact, "fullPeaksExact": peaksExact,
        "legacyJsonValuesExact": legacyExact,
        "legacyAudioFramesUnchanged": raw["audioFrames"] == fullLegacy,
        "legacyCount": document.audioFrames?.count ?? 0,
        "legacyTimesMs": document.audioFrames?.map(\.timestampMs) ?? [], "opaqueJsonValuesExact": opaqueExact,
        "metadataJsonValuesExact": metadataExact,
        "gateDefaultOff": try !AnalysisTimeline(document).useDraftSignals
    ])
}
let output = try JSONSerialization.data(withJSONObject: results, options: [.prettyPrinted, .sortedKeys])
FileHandle.standardOutput.write(output)
FileHandle.standardOutput.write(Data([10]))
