import Flutter
import Foundation
import LlmCoreBridge
import UIKit
import UniformTypeIdentifiers

public final class HcLlmFlutterPlugin: NSObject, FlutterPlugin, FlutterStreamHandler, UIDocumentPickerDelegate {
  private let engine = ProbeEngine()
  private var eventSink: FlutterEventSink?
  private var pendingDocumentResult: FlutterResult?

  public static func register(with registrar: FlutterPluginRegistrar) {
    let instance = HcLlmFlutterPlugin()
    let methods = FlutterMethodChannel(name: "org.haruchat/native-probe", binaryMessenger: registrar.messenger())
    let events = FlutterEventChannel(name: "org.haruchat/native-probe/events", binaryMessenger: registrar.messenger())
    registrar.addMethodCallDelegate(instance, channel: methods)
    events.setStreamHandler(instance)
    instance.engine.emit = { event in instance.send(event) }
  }

  public func handle(_ call: FlutterMethodCall, result: @escaping FlutterResult) {
    switch call.method {
    case "chooseModel":
      guard let controller = UIApplication.shared.connectedScenes
        .compactMap({ ($0 as? UIWindowScene)?.keyWindow })
        .first?.rootViewController else {
        result(FlutterError(code: "no_view_controller", message: "No active view controller", details: nil))
        return
      }
      pendingDocumentResult = result
      let picker = UIDocumentPickerViewController(forOpeningContentTypes: [.data], asCopy: true)
      picker.delegate = self
      controller.present(picker, animated: true)
    case "load":
      let arguments = call.arguments as? [String: Any]
      let path = arguments?["path"] as? String ?? ""
      let contextWindowTokens = arguments?["contextWindowTokens"] as? Int ?? 8_192
      engine.load(path: path, contextWindowTokens: contextWindowTokens)
      result("Load dispatched")
    case "generate":
      let arguments = call.arguments as? [String: Any]
      let prompt = arguments?["prompt"] as? String ?? ""
      let messages = (arguments?["messages"] as? [[String: Any]] ?? []).compactMap { entry -> ProbeChatMessage? in
        guard let role = entry["role"] as? String, let content = entry["content"] as? String,
              role == "system" || role == "user" || role == "assistant" else { return nil }
        return ProbeChatMessage(role: role, content: content)
      }
      let maximumOutputTokens = arguments?["maximumOutputTokens"] as? Int ?? 2_048
      let temperature = arguments?["temperature"] as? Double ?? 0.7
      engine.generate(promptFallback: prompt, messages: messages, maximumOutputTokens: maximumOutputTokens, temperature: temperature)
      result("Generation dispatched")
    case "cancel": engine.cancel(); result("Cancel requested")
    case "reset": engine.reset(); result("Reset dispatched")
    case "unload": engine.unload(); result("Unload dispatched")
    default: result(FlutterMethodNotImplemented)
    }
  }

  public func documentPickerWasCancelled(_ controller: UIDocumentPickerViewController) {
    pendingDocumentResult?(nil)
    pendingDocumentResult = nil
  }

  public func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
    pendingDocumentResult?(urls.first?.path)
    pendingDocumentResult = nil
  }

  public func onListen(withArguments arguments: Any?, eventSink events: @escaping FlutterEventSink) -> FlutterError? {
    eventSink = events
    return nil
  }

  public func onCancel(withArguments arguments: Any?) -> FlutterError? {
    eventSink = nil
    return nil
  }

  private func send(_ event: [String: Any]) {
    DispatchQueue.main.async { self.eventSink?(event) }
  }
}

private struct ProbeChatMessage {
  let role: String
  let content: String
}

private final class ProbeEngine {
  private let worker = DispatchQueue(label: "org.haruchat.xcross-native-probe")
  private let jobLock = NSLock()
  private var runtime: OpaquePointer?
  private var model: OpaquePointer?
  private var context: OpaquePointer?
  private var appliedContextTokens = 0
  private var job: OpaquePointer?
  var emit: (([String: Any]) -> Void)?

  init() {
    var options = hc_llm_runtime_options()
    options.struct_size = UInt32(MemoryLayout<hc_llm_runtime_options>.size)
    options.abi_version = hc_llm_bridge_abi_version()
    options.event_queue_capacity = 32
    if hc_llm_runtime_create(&options, &runtime) != HC_LLM_STATUS_OK { runtime = nil }
  }

  deinit {
    worker.sync {
      unloadLocked()
      if let runtime { _ = hc_llm_runtime_destroy(runtime) }
    }
  }

  func load(path: String, contextWindowTokens: Int) {
    worker.async {
      self.unloadLocked()
      guard let runtime = self.runtime else { self.status("Runtime initialization failed"); return }
      guard !path.isEmpty else { self.status("Choose a GGUF model first"); return }
      guard FileManager.default.fileExists(atPath: path) else { self.status("Model file unavailable. Choose the GGUF again."); return }
      guard FileManager.default.isReadableFile(atPath: path) else { self.status("Model file access denied. Import a local copy of the GGUF."); return }
      var options = hc_llm_model_load_options()
      options.struct_size = UInt32(MemoryLayout<hc_llm_model_load_options>.size)
      options.abi_version = hc_llm_bridge_abi_version()
      let status = path.withCString { hc_llm_model_load(runtime, $0, &options, &self.model) }
      guard status == HC_LLM_STATUS_OK, let model = self.model else { self.status(modelLoadFailure(status)); return }
      guard contextWindowTokens >= 8_192 && contextWindowTokens <= 131_072 else { self.unloadLocked(); self.status("Context size must be 8,192–131,072 tokens"); return }
      let contextPolicy = self.contextPolicy(path: path, requested: contextWindowTokens)
      var appliedContext = contextPolicy.applied
      var contextStatus = self.createContext(model, contextWindowTokens: appliedContext)
      if hasStatusMessage(contextStatus, "context initialization failed") && appliedContext > 8_192 {
        // This is a policy fallback, not exception recovery: a failed large KV cache
        // is retried once at the known-safe baseline and reported as such.
        appliedContext = 8_192
        contextStatus = self.createContext(model, contextWindowTokens: appliedContext)
      }
      guard contextStatus == HC_LLM_STATUS_OK else { self.unloadLocked(); self.status(contextFailure(contextStatus, requested: contextWindowTokens)); return }
      self.appliedContextTokens = appliedContext
      var metadata = hc_llm_runtime_metadata()
      metadata.struct_size = UInt32(MemoryLayout<hc_llm_runtime_metadata>.size)
      metadata.abi_version = hc_llm_bridge_abi_version()
      _ = hc_llm_runtime_get_metadata(runtime, &metadata)
      let fallback = appliedContext == contextWindowTokens ? "" : "; requested \(contextWindowTokens), using safe fallback \(appliedContext)"
      self.status("Loaded \(URL(fileURLWithPath: path).lastPathComponent) (\(string(&metadata.backend_name)), context \(appliedContext)\(fallback); Q8 KV, batch 256/ubatch 64\(contextPolicy.reason))")
    }
  }

  func generate(promptFallback: String, messages: [ProbeChatMessage], maximumOutputTokens: Int, temperature: Double) {
    worker.async {
      guard let context = self.context else { self.finishWithoutCompletion("Load a GGUF model first"); return }
      // The probe submits a complete ChatML prompt per request. Reset first so
      // previous raw prompt tokens cannot bias or duplicate the next response.
      guard hc_llm_context_reset(context) == HC_LLM_STATUS_OK else { self.finishWithoutCompletion("Context reset failed"); return }
      var options = hc_llm_generation_options()
      options.struct_size = UInt32(MemoryLayout<hc_llm_generation_options>.size)
      options.abi_version = hc_llm_bridge_abi_version()
      options.max_tokens = UInt32(max(1, min(maximumOutputTokens, 8_192)))
      options.temperature = Float(max(0.0, min(2.0, temperature)))
      options.top_p = 0.9
      options.top_k = 40
      options.seed = UInt32.random(in: UInt32.min...UInt32.max)
      let rendered = self.embeddedTemplatePrompt(messages)
      let prompt = rendered?.prompt ?? promptFallback
      let input = Array(prompt.utf8)
      guard let promptTokens = self.countPromptTokens(input),
            Int(promptTokens) + Int(options.max_tokens) <= self.appliedContextTokens else {
        self.finishWithoutCompletion("Context budget exceeded before generation; shorten the conversation or start a new one.")
        return
      }
      let startStatus = input.withUnsafeBufferPointer { buffer in
        options.prompt_utf8 = buffer.baseAddress
        options.prompt_bytes = UInt32(buffer.count)
        return hc_llm_job_start(context, &options, &self.job)
      }
      guard startStatus == HC_LLM_STATUS_OK, let job = self.job else { self.finishWithoutCompletion("Generate failed: \(message(startStatus))"); return }
      let historyMessages = max(0, messages.count - 2)
      self.status("Generating… \(rendered == nil ? "fallback ChatML" : "GGUF chat template"), prompt \(promptTokens) / \(self.appliedContextTokens), history \(historyMessages) messages, output reserve \(options.max_tokens)")
      self.jobLock.lock(); self.job = job; self.jobLock.unlock()
      while true {
        var event = hc_llm_event()
        event.struct_size = UInt32(MemoryLayout<hc_llm_event>.size)
        event.abi_version = hc_llm_bridge_abi_version()
        let pollStatus = hc_llm_job_poll(job, &event)
        if pollStatus == HC_LLM_STATUS_WOULD_BLOCK { Thread.sleep(forTimeInterval: 0.01); continue }
        guard pollStatus == HC_LLM_STATUS_OK else { self.status("Poll failed: \(message(pollStatus))"); break }
        self.event(event)
        if event.is_terminal != 0 { self.status(event.type == HC_LLM_EVENT_COMPLETED ? "Completed" : "Generation ended"); break }
      }
      self.jobLock.lock(); if self.job == job { self.job = nil }; self.jobLock.unlock()
      _ = hc_llm_job_destroy(job)
    }
  }

  func cancel() { jobLock.lock(); let activeJob = job; jobLock.unlock(); if let activeJob { _ = hc_llm_job_cancel(activeJob) } }
  func reset() { worker.async { self.status(self.context.map { hc_llm_context_reset($0) == HC_LLM_STATUS_OK ? "Context reset" : "Reset unavailable" } ?? "No active context") } }
  func unload() { worker.async { self.unloadLocked(); self.status("Model unloaded") } }

  private func createContext(_ model: OpaquePointer, contextWindowTokens: Int) -> hc_llm_status {
    return hc_llm_bridge_context_create_long(model, UInt32(contextWindowTokens), &self.context)
  }

  private func embeddedTemplatePrompt(_ messages: [ProbeChatMessage]) -> (prompt: String, messageCount: Int)? {
    guard let model, !messages.isEmpty else { return nil }
    var roles: [UnsafeMutablePointer<CChar>?] = []
    var contents: [UnsafeMutablePointer<UInt8>?] = []
    defer { roles.forEach { free($0) }; contents.forEach { $0?.deallocate() } }
    var native: [hc_llm_chat_message] = []
    for message in messages {
      guard let role = strdup(message.role) else { return nil }
      let bytes = Array(message.content.utf8)
      let content = UnsafeMutablePointer<UInt8>.allocate(capacity: max(1, bytes.count))
      if !bytes.isEmpty { content.initialize(from: bytes, count: bytes.count) }
      roles.append(role); contents.append(content)
      native.append(hc_llm_chat_message(role_utf8: UnsafePointer(role), content_utf8: UnsafePointer(content), content_bytes: UInt32(bytes.count)))
    }
    var required: UInt32 = 0
    let queryStatus = native.withUnsafeMutableBufferPointer {
      hc_llm_model_apply_chat_template(model, $0.baseAddress, UInt32($0.count), 1, nil, 0, &required)
    }
    guard queryStatus == HC_LLM_STATUS_OK, required > 1 else { return nil }
    var output = [UInt8](repeating: 0, count: Int(required))
    let renderStatus = native.withUnsafeMutableBufferPointer { nativeBuffer in
      output.withUnsafeMutableBufferPointer { outputBuffer in
        hc_llm_model_apply_chat_template(model, nativeBuffer.baseAddress, UInt32(nativeBuffer.count), 1, outputBuffer.baseAddress, required, &required)
      }
    }
    guard renderStatus == HC_LLM_STATUS_OK else { return nil }
    guard let prompt = String(bytes: output.dropLast(), encoding: .utf8) else { return nil }
    return (prompt, messages.count)
  }

  /// A native preflight/start failure has no job to emit a terminal event.
  /// Emit one explicitly so Dart never leaves a pending assistant bubble in
  /// the next prompt after a rejected generation.
  private func finishWithoutCompletion(_ value: String) {
    status(value)
    emit?(["isTerminal": true, "completed": false])
  }

  private func countPromptTokens(_ input: [UInt8]) -> UInt32? {
    guard let model else { return nil }
    var count: UInt32 = 0
    let status = input.withUnsafeBufferPointer {
      hc_llm_model_count_tokens(model, $0.baseAddress, UInt32($0.count), &count)
    }
    return status == HC_LLM_STATUS_OK ? count : nil
  }

  private func contextPolicy(path: String, requested: Int) -> (applied: Int, reason: String) {
    let normalizedPath = path.lowercased()
    guard normalizedPath.contains("kanana-2-3b"), normalizedPath.contains("q8") else {
      return (min(requested, 32_768), requested > 32_768 ? "; model-safe cap 32768" : "")
    }

    // The published Kanana 2 3B Q8_0 GGUF is roughly 3.73 GiB.  Its published
    // Qwen3 config reports 32 layers, 8 KV heads and head dim 128; if all
    // layers retain full attention, Q8_0 K/V needs about 2.13 GiB at 32 Ki.
    // On 8 GiB iPads those allocations share unified memory with weights,
    // Metal graphs and Flutter/Unity surfaces, so do not attempt a context
    // that is likely to be killed by JetSAM before native code can fall back.
    let eightGiB = UInt64(8) * 1024 * 1024 * 1024
    let cap = ProcessInfo.processInfo.physicalMemory <= eightGiB ? 16_384 : 32_768
    return (min(requested, cap), requested > cap ? "; Kanana Q8 safety cap \(cap) on this device" : "")
  }

  private func unloadLocked() {
    if let context { _ = hc_llm_context_destroy(context); self.context = nil }
    appliedContextTokens = 0
    if let model { _ = hc_llm_model_unload(model); self.model = nil }
  }

  private func status(_ value: String) { emit?(["status": value]) }

  private func event(_ event: hc_llm_event) {
    let bytes = event.payload_utf8.map { Data(bytes: $0, count: Int(event.payload_bytes)) } ?? Data()
    let utf8 = String(data: bytes, encoding: .utf8)
    let line: [String: Any] = [
      "event_type_code": event.type.rawValue,
      "terminal": event.is_terminal != 0,
      "sequence": event.sequence,
      "payload_bytes": event.payload_bytes,
      "payload_utf8": utf8 ?? NSNull(),
      "payload_base64": bytes.base64EncodedString(),
      "metrics": ["emitted_token_count": event.metrics.emitted_token_count, "queue_depth": event.metrics.queue_depth, "elapsed_milliseconds": event.metrics.elapsed_milliseconds],
    ]
    let json = (try? JSONSerialization.data(withJSONObject: line)).flatMap { String(data: $0, encoding: .utf8) } ?? "{}"
    var output: [String: Any] = ["logLine": json, "isTerminal": event.is_terminal != 0, "completed": event.type == HC_LLM_EVENT_COMPLETED]
    if event.type == HC_LLM_EVENT_TOKEN { output["token"] = utf8 ?? "" }
    emit?(output)
  }
}

private func message(_ status: hc_llm_status) -> String { String(cString: hc_llm_status_message(status)) }

private func modelLoadFailure(_ status: hc_llm_status) -> String {
  if hasStatusMessage(status, "model file not found") { return "Model file was not found. Choose the GGUF again." }
  if hasStatusMessage(status, "model file access denied") { return "Model file access was denied. Import a local copy of the GGUF." }
  if hasStatusMessage(status, "model initialization failed") { return "Model initialization failed after file access. The GGUF may be unsupported, corrupted, or too large for current Metal memory." }
  return "Model load failed: \(message(status))"
}

private func contextFailure(_ status: hc_llm_status, requested: Int) -> String {
  if hasStatusMessage(status, "context initialization failed") { return "Context initialization failed at \(requested) tokens; the 8,192-token fallback also could not reserve KV-cache memory. Close other apps or use a smaller model." }
  return "Context creation failed: \(message(status))"
}

/// The staged XCFramework may be from an older ABI-v1 build. Comparing the
/// stable native message keeps the Swift source buildable with that header;
/// updated artifacts supply the detailed message and enable this branch.
private func hasStatusMessage(_ status: hc_llm_status, _ expected: String) -> Bool {
  return message(status) == expected
}

private func string<T>(_ field: inout T) -> String {
  withUnsafePointer(to: &field) {
    $0.withMemoryRebound(to: CChar.self, capacity: MemoryLayout<T>.size) { String(cString: $0) }
  }
}
