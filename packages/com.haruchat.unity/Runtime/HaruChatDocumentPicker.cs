#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace HaruChat.Unity
{
    internal enum HaruChatPickerResult { Pending, Selected, Cancelled, Error }
    internal static class HaruChatDocumentPicker
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void hc_unity_open_gguf_picker();
        [DllImport("__Internal")] private static extern void hc_unity_open_character_picker();
        [DllImport("__Internal")] private static extern int hc_unity_gguf_picker_result(IntPtr buffer, int bufferBytes, out int requiredBytes);
#endif
        public static void Open()
        {
#if UNITY_IOS && !UNITY_EDITOR
            hc_unity_open_gguf_picker();
#endif
        }
        public static void OpenCharacter()
        {
#if UNITY_IOS && !UNITY_EDITOR
            hc_unity_open_character_picker();
#endif
        }
        public static HaruChatPickerResult Poll(out string? path)
        {
            path = null;
#if UNITY_IOS && !UNITY_EDITOR
            var code = hc_unity_gguf_picker_result(IntPtr.Zero, 0, out var required); if (code == 0) return HaruChatPickerResult.Pending; if (code == 2) return HaruChatPickerResult.Cancelled; if (code != 1 || required <= 1) return HaruChatPickerResult.Error;
            var memory = Marshal.AllocHGlobal(required); try { if (hc_unity_gguf_picker_result(memory, required, out _) != 1) return HaruChatPickerResult.Error; var bytes = new byte[required - 1]; Marshal.Copy(memory, bytes, 0, bytes.Length); path = Encoding.UTF8.GetString(bytes); return HaruChatPickerResult.Selected; } finally { Marshal.FreeHGlobal(memory); }
#else
            return HaruChatPickerResult.Error;
#endif
        }
    }
}
