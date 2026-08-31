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
      let path = (call.arguments as? [String: Any])?["path"] as? String ?? ""
      engine.load(path: path)
      result("Load dispatched")
    case "generate":
      let prompt = (call.arguments as? [String: Any])?["prompt"] as? String ?? ""
      engine.generate(prompt: prompt)
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

private final class ProbeEngine {
  private let worker = DispatchQueue(label: "org.haruchat.xcross-native-probe")
  private let jobLock = NSLock()
  private var runtime: OpaquePointer?
  private var model: OpaquePointer?
  private var context: OpaquePointer?
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

  func load(path: String) {
    worker.async {
      self.unloadLocked()
      guard let runtime = self.runtime else { self.status("Runtime initialization failed"); return }
      guard !path.isEmpty else { self.status("Choose a GGUF model first"); return }
      var options = hc_llm_model_load_options()
      options.struct_size = UInt32(MemoryLayout<hc_llm_model_load_options>.size)
      options.abi_version = hc_llm_bridge_abi_version()
      let status = path.withCString { hc_llm_model_load(runtime, $0, &options, &self.model) }
      guard status == HC_LLM_STATUS_OK, let model = self.model else { self.status("Load failed: \(message(status))"); return }
      var contextOptions = hc_llm_context_options()
      contextOptions.struct_size = UInt32(MemoryLayout<hc_llm_context_options>.size)
      contextOptions.abi_version = hc_llm_bridge_abi_version()
      contextOptions.context_size = 2048
      let contextStatus = hc_llm_context_create(model, &contextOptions, &self.context)
      guard contextStatus == HC_LLM_STATUS_OK else { self.unloadLocked(); self.status("Context failed: \(message(contextStatus))"); return }
      var metadata = hc_llm_runtime_metadata()
      metadata.struct_size = UInt32(MemoryLayout<hc_llm_runtime_metadata>.size)
      metadata.abi_version = hc_llm_bridge_abi_version()
      _ = hc_llm_runtime_get_metadata(runtime, &metadata)
      self.status("Loaded \(URL(fileURLWithPath: path).lastPathComponent) (\(string(&metadata.backend_name)))")
    }
  }

  func generate(prompt: String) {
    worker.async {
      guard let context = self.context else { self.status("Load a GGUF model first"); return }
      // The probe submits a complete ChatML prompt per request. Reset first so
      // previous raw prompt tokens cannot bias or duplicate the next response.
      guard hc_llm_context_reset(context) == HC_LLM_STATUS_OK else { self.status("Context reset failed"); return }
      var options = hc_llm_generation_options()
      options.struct_size = UInt32(MemoryLayout<hc_llm_generation_options>.size)
      options.abi_version = hc_llm_bridge_abi_version()
      options.max_tokens = 128
      options.temperature = 0.7
      options.top_p = 0.9
      options.top_k = 40
      options.seed = UInt32.random(in: UInt32.min...UInt32.max)
      let input = Array(prompt.utf8)
      let startStatus = input.withUnsafeBufferPointer { buffer in
        options.prompt_utf8 = buffer.baseAddress
        options.prompt_bytes = UInt32(buffer.count)
        return hc_llm_job_start(context, &options, &self.job)
      }
      guard startStatus == HC_LLM_STATUS_OK, let job = self.job else { self.status("Generate failed: \(message(startStatus))"); return }
      self.status("Generating…")
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

  private func unloadLocked() {
    if let context { _ = hc_llm_context_destroy(context); self.context = nil }
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
    var output: [String: Any] = ["logLine": json, "isTerminal": event.is_terminal != 0]
    if event.type == HC_LLM_EVENT_TOKEN { output["token"] = utf8 ?? "" }
    emit?(output)
  }
}

private func message(_ status: hc_llm_status) -> String { String(cString: hc_llm_status_message(status)) }

private func string<T>(_ field: inout T) -> String {
  withUnsafePointer(to: &field) {
    $0.withMemoryRebound(to: CChar.self, capacity: MemoryLayout<T>.size) { String(cString: $0) }
  }
}
