import 'package:flutter/services.dart';

class NativeProbeEvent {
  const NativeProbeEvent({this.status, this.logLine, this.token, this.isTerminal = false});
  final String? status;
  final String? logLine;
  final String? token;
  final bool isTerminal;

  factory NativeProbeEvent.fromMap(Map<Object?, Object?> map) => NativeProbeEvent(
        status: map['status'] as String?,
        logLine: map['logLine'] as String?,
        token: map['token'] as String?,
        isTerminal: map['isTerminal'] as bool? ?? false,
      );
}

class HcLlmFlutter {
  static const _method = MethodChannel('org.haruchat/native-probe');
  static const _events = EventChannel('org.haruchat/native-probe/events');

  static Stream<NativeProbeEvent> get events => _events.receiveBroadcastStream().map(
        (Object? value) => NativeProbeEvent.fromMap(Map<Object?, Object?>.from(value! as Map)),
      );

  static Future<String?> chooseModel() => _method.invokeMethod<String>('chooseModel');
  static Future<String> load(String path) => _invoke('load', {'path': path});
  static Future<String> generate(String prompt) => _invoke('generate', {'prompt': prompt});
  static Future<String> cancel() => _invoke('cancel');
  static Future<String> reset() => _invoke('reset');
  static Future<String> unload() => _invoke('unload');

  static Future<String> _invoke(String method, [Map<String, Object?>? arguments]) async =>
      await _method.invokeMethod<String>(method, arguments) ?? '$method dispatched';
}

typedef NativeProbe = HcLlmFlutter;
