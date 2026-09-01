#if defined(__APPLE__)
#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>

extern UIViewController *UnityGetGLViewController(void);

@interface HaruChatGgufPicker : NSObject<UIDocumentPickerDelegate>
@property(nonatomic, assign) NSInteger state;
@property(nonatomic, copy) NSString *result;
@end

@implementation HaruChatGgufPicker
+ (instancetype)shared { static HaruChatGgufPicker *instance; static dispatch_once_t once; dispatch_once(&once, ^{ instance = [HaruChatGgufPicker new]; }); return instance; }
- (void)openType:(UTType *)type { self.state = 0; self.result = nil; UIDocumentPickerViewController *picker = [[UIDocumentPickerViewController alloc] initForOpeningContentTypes:@[type] asCopy:YES]; picker.delegate = self; [UnityGetGLViewController() presentViewController:picker animated:YES completion:nil]; }
- (void)documentPickerWasCancelled:(UIDocumentPickerViewController *)controller { self.state = 2; }
- (void)documentPicker:(UIDocumentPickerViewController *)controller didPickDocumentsAtURLs:(NSArray<NSURL *> *)urls { NSURL *url = urls.firstObject; if (url == nil) { self.state = 3; return; } self.result = url.path; self.state = 1; }
@end

extern "C" void hc_unity_open_gguf_picker(void) { dispatch_async(dispatch_get_main_queue(), ^{ [[HaruChatGgufPicker shared] openType:UTTypeData]; }); }
extern "C" void hc_unity_open_character_picker(void) { dispatch_async(dispatch_get_main_queue(), ^{ [[HaruChatGgufPicker shared] openType:UTTypeFolder]; }); }
extern "C" int hc_unity_gguf_picker_result(char *buffer, int bufferBytes, int *requiredBytes) { HaruChatGgufPicker *picker = [HaruChatGgufPicker shared]; if (picker.state != 1) return (int)picker.state; NSData *data = [picker.result dataUsingEncoding:NSUTF8StringEncoding]; int required = (int)data.length + 1; if (requiredBytes) *requiredBytes = required; if (buffer == NULL || bufferBytes < required) return 1; memcpy(buffer, data.bytes, data.length); buffer[data.length] = 0; return 1; }
#endif
