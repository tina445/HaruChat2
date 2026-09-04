#import "AppDelegate.h"
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>
#include "hc_llm.h"

static NSString *StatusText(hc_llm_status status) {
  return [NSString stringWithUTF8String:hc_llm_status_message(status)];
}

@interface ProbeRuntime : NSObject
@property(nonatomic) hc_llm_runtime *runtime;
@property(nonatomic) hc_llm_model *model;
@property(nonatomic) hc_llm_context *context;
@property(nonatomic) hc_llm_job *job;
@property(nonatomic) dispatch_queue_t worker;
@property(nonatomic, copy) void (^eventSink)(NSString *, NSString *, NSString *);
@end

@implementation ProbeRuntime

- (instancetype)initWithEventSink:(void (^)(NSString *, NSString *, NSString *))sink {
  if ((self = [super init])) {
    _eventSink = sink;
    _worker = dispatch_queue_create("org.haruchat.native-probe", DISPATCH_QUEUE_SERIAL);
    hc_llm_runtime_options options = {0};
    options.struct_size = sizeof(options);
    options.abi_version = HC_LLM_ABI_VERSION;
    options.event_queue_capacity = 32;
    hc_llm_status status = hc_llm_runtime_create(&options, &_runtime);
    if (status != HC_LLM_STATUS_OK) _runtime = nullptr;
  }
  return self;
}

- (void)emitStatus:(NSString *)status { dispatch_async(dispatch_get_main_queue(), ^{ self.eventSink(status, @"", @""); }); }

- (void)emitEvent:(const hc_llm_event &)event {
  NSData *payload = event.payload_utf8 == nullptr ? [NSData data] : [NSData dataWithBytes:event.payload_utf8 length:event.payload_bytes];
  NSString *utf8 = [[NSString alloc] initWithData:payload encoding:NSUTF8StringEncoding];
  NSDictionary *json = @{
    @"event_type_code": @(event.type), @"terminal": @(event.is_terminal != 0), @"sequence": @(event.sequence),
    @"payload_bytes": @(event.payload_bytes), @"payload_utf8": utf8 ?: [NSNull null],
    @"payload_base64": [payload base64EncodedStringWithOptions:0],
    @"metrics": @{@"emitted_token_count": @(event.metrics.emitted_token_count), @"queue_depth": @(event.metrics.queue_depth), @"elapsed_milliseconds": @(event.metrics.elapsed_milliseconds)}
  };
  NSData *data = [NSJSONSerialization dataWithJSONObject:json options:0 error:nil];
  NSString *line = [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding] ?: @"{}";
  NSLog(@"HC_EVENT %@", line);
  NSString *fragment = event.type == HC_LLM_EVENT_TOKEN ? (utf8 ?: @"") : @"";
  dispatch_async(dispatch_get_main_queue(), ^{ self.eventSink(@"", line, fragment); });
}

- (void)loadModel:(NSString *)path {
  dispatch_async(_worker, ^{
    [self unloadLocked];
    if (self.runtime == nullptr) { [self emitStatus:@"Runtime initialization failed"]; return; }
    hc_llm_model_load_options modelOptions = {0}; modelOptions.struct_size = sizeof(modelOptions); modelOptions.abi_version = HC_LLM_ABI_VERSION;
    hc_llm_model *model = nullptr;
    hc_llm_status status = hc_llm_model_load(self.runtime, path.fileSystemRepresentation, &modelOptions, &model);
    if (status != HC_LLM_STATUS_OK) { [self emitStatus:[NSString stringWithFormat:@"Load failed: %@", StatusText(status)]]; return; }
    self.model = model;
    hc_llm_context_options contextOptions = {0}; contextOptions.struct_size = sizeof(contextOptions); contextOptions.abi_version = HC_LLM_ABI_VERSION; contextOptions.context_size = 8192;
    hc_llm_context *context = nullptr;
    status = hc_llm_context_create(self.model, &contextOptions, &context);
    if (status != HC_LLM_STATUS_OK) { hc_llm_model_unload(self.model); self.model = nullptr; [self emitStatus:[NSString stringWithFormat:@"Context failed: %@", StatusText(status)]]; return; }
    self.context = context;
    hc_llm_runtime_metadata metadata = {0}; metadata.struct_size = sizeof(metadata); metadata.abi_version = HC_LLM_ABI_VERSION; hc_llm_runtime_get_metadata(self.runtime, &metadata);
    [self emitStatus:[NSString stringWithFormat:@"Loaded %@ (%s / %s)", path.lastPathComponent, metadata.backend_name, metadata.target_triple]];
  });
}

- (void)generate:(NSString *)prompt {
  dispatch_async(_worker, ^{
    if (self.context == nullptr) { [self emitStatus:@"Load a GGUF model first"]; return; }
    NSData *input = [prompt dataUsingEncoding:NSUTF8StringEncoding];
    hc_llm_generation_options options = {0}; options.struct_size = sizeof(options); options.abi_version = HC_LLM_ABI_VERSION; options.temperature = 0.7f; options.top_p = 0.9f; options.top_k = 40;
    options.prompt_utf8 = (const uint8_t *)input.bytes; options.prompt_bytes = (uint32_t)input.length; options.max_tokens = 2048;
    hc_llm_job *job = nullptr;
    hc_llm_status status = hc_llm_job_start(self.context, &options, &job);
    if (status != HC_LLM_STATUS_OK) { [self emitStatus:[NSString stringWithFormat:@"Generate failed: %@", StatusText(status)]]; return; }
    @synchronized (self) { self.job = job; }
    [self emitStatus:@"Generating…"];
    for (;;) {
      hc_llm_event event = {0}; event.struct_size = sizeof(event); event.abi_version = HC_LLM_ABI_VERSION;
      status = hc_llm_job_poll(job, &event);
      if (status == HC_LLM_STATUS_WOULD_BLOCK) { [NSThread sleepForTimeInterval:0.01]; continue; }
      if (status != HC_LLM_STATUS_OK) { [self emitStatus:[NSString stringWithFormat:@"Poll failed: %@", StatusText(status)]]; break; }
      [self emitEvent:event];
      if (event.is_terminal) { [self emitStatus:event.type == HC_LLM_EVENT_COMPLETED ? @"Completed" : @"Generation ended"]; break; }
    }
    @synchronized (self) { if (self.job == job) self.job = nullptr; }
    hc_llm_job_destroy(job);
  });
}

- (void)cancel { @synchronized (self) { if (_job != nullptr) hc_llm_job_cancel(_job); } }
- (void)reset { dispatch_async(_worker, ^{ if (self.context != nullptr) [self emitStatus:hc_llm_context_reset(self.context) == HC_LLM_STATUS_OK ? @"Context reset" : @"Reset unavailable"]; }); }
- (void)unloadLocked { if (self.context != nullptr) { hc_llm_context_destroy(self.context); self.context = nullptr; } if (self.model != nullptr) { hc_llm_model_unload(self.model); self.model = nullptr; } }
- (void)unload { dispatch_async(_worker, ^{ [self unloadLocked]; [self emitStatus:@"Model unloaded"]; }); }
- (void)dealloc { if (_runtime != nullptr) { dispatch_sync(_worker, ^{ [self unloadLocked]; hc_llm_runtime_destroy(self.runtime); self.runtime = nullptr; }); } }
@end

@interface AppDelegate () <UIDocumentPickerDelegate>
@property(nonatomic) UITextField *modelPath; @property(nonatomic) UITextView *prompt; @property(nonatomic) UITextView *response; @property(nonatomic) UITextView *log; @property(nonatomic) UILabel *status; @property(nonatomic) ProbeRuntime *engine;
@end

@implementation AppDelegate
- (UIButton *)button:(NSString *)title action:(SEL)action { UIButton *b = [UIButton buttonWithType:UIButtonTypeSystem]; [b setTitle:title forState:UIControlStateNormal]; [b addTarget:self action:action forControlEvents:UIControlEventTouchUpInside]; return b; }
- (void)application:(UIApplication *)application didFinishLaunchingWithOptions:(NSDictionary *)launchOptions {
  self.window = [[UIWindow alloc] initWithFrame:UIScreen.mainScreen.bounds]; UIViewController *vc = [UIViewController new]; vc.view.backgroundColor = UIColor.systemBackgroundColor;
  UIStackView *stack = [[UIStackView alloc] initWithFrame:CGRectInset(vc.view.bounds, 16, 44)]; stack.autoresizingMask = UIViewAutoresizingFlexibleWidth | UIViewAutoresizingFlexibleHeight; stack.axis = UILayoutConstraintAxisVertical; stack.spacing = 8; [vc.view addSubview:stack];
  self.status = [UILabel new]; self.status.numberOfLines = 2; self.status.text = @"Select a GGUF model"; [stack addArrangedSubview:self.status];
  self.modelPath = [UITextField new]; self.modelPath.borderStyle = UITextBorderStyleRoundedRect; self.modelPath.placeholder = @"Imported GGUF path"; [stack addArrangedSubview:self.modelPath];
  UIStackView *modelButtons = [UIStackView new]; modelButtons.axis = UILayoutConstraintAxisHorizontal; modelButtons.distribution = UIStackViewDistributionFillEqually; [modelButtons addArrangedSubview:[self button:@"Choose GGUF" action:@selector(choose)]]; [modelButtons addArrangedSubview:[self button:@"Load Model" action:@selector(load)]]; [stack addArrangedSubview:modelButtons];
  self.prompt = [UITextView new]; self.prompt.text = @"Hello"; self.prompt.layer.borderWidth = 1; self.prompt.layer.borderColor = UIColor.separatorColor.CGColor; [stack addArrangedSubview:self.prompt]; [self.prompt.heightAnchor constraintEqualToConstant:86].active = YES;
  UIStackView *actions = [UIStackView new]; actions.axis = UILayoutConstraintAxisHorizontal; actions.distribution = UIStackViewDistributionFillEqually; [actions addArrangedSubview:[self button:@"Generate" action:@selector(generate)]]; [actions addArrangedSubview:[self button:@"Cancel" action:@selector(cancel)]]; [actions addArrangedSubview:[self button:@"Reset" action:@selector(reset)]]; [actions addArrangedSubview:[self button:@"Unload" action:@selector(unload)]]; [stack addArrangedSubview:actions];
  self.response = [UITextView new]; self.response.editable = NO; self.response.layer.borderWidth = 1; self.response.layer.borderColor = UIColor.separatorColor.CGColor; [stack addArrangedSubview:self.response]; [self.response.heightAnchor constraintEqualToConstant:120].active = YES;
  self.log = [UITextView new]; self.log.editable = NO; self.log.font = [UIFont monospacedSystemFontOfSize:10 weight:UIFontWeightRegular]; self.log.layer.borderWidth = 1; self.log.layer.borderColor = UIColor.separatorColor.CGColor; [stack addArrangedSubview:self.log];
  __weak typeof(self) weakSelf = self; self.engine = [[ProbeRuntime alloc] initWithEventSink:^(NSString *status, NSString *line, NSString *fragment) { if (status.length) weakSelf.status.text = status; if (line.length) weakSelf.log.text = [weakSelf.log.text stringByAppendingFormat:@"%@\n", line]; if (fragment.length) weakSelf.response.text = [weakSelf.response.text stringByAppendingString:fragment]; }];
  self.window.rootViewController = vc; [self.window makeKeyAndVisible]; return YES;
}
- (void)choose { UIDocumentPickerViewController *picker = [[UIDocumentPickerViewController alloc] initForOpeningContentTypes:@[UTTypeData] asCopy:YES]; picker.delegate = self; [self.window.rootViewController presentViewController:picker animated:YES completion:nil]; }
- (void)documentPicker:(UIDocumentPickerViewController *)controller didPickDocumentsAtURLs:(NSArray<NSURL *> *)urls { self.modelPath.text = urls.firstObject.path; }
- (void)load { [self.engine loadModel:self.modelPath.text]; } - (void)generate { self.response.text = @""; self.log.text = @""; [self.engine generate:self.prompt.text]; } - (void)cancel { [self.engine cancel]; } - (void)reset { [self.engine reset]; } - (void)unload { [self.engine unload]; }
@end
