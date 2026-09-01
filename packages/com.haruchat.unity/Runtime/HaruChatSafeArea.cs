#nullable enable
using UnityEngine;

namespace HaruChat.Unity
{
    /// <summary>Anchors a scene-authored content root inside iOS/iPadOS safe-area insets.</summary>
    public sealed class HaruChatSafeArea : MonoBehaviour
    {
        [SerializeField] private RectTransform _content = null!;
        private Rect _lastSafeArea;
        private Vector2Int _lastScreen;

        private void Awake() { Apply(); }
        private void Update()
        {
            if (_lastSafeArea != Screen.safeArea || _lastScreen.x != Screen.width || _lastScreen.y != Screen.height) Apply();
        }

        private void Apply()
        {
            var safe = Screen.safeArea;
            _lastSafeArea = safe;
            _lastScreen = new Vector2Int(Screen.width, Screen.height);
            _content.anchorMin = new Vector2(safe.xMin / Screen.width, safe.yMin / Screen.height);
            _content.anchorMax = new Vector2(safe.xMax / Screen.width, safe.yMax / Screen.height);
            _content.offsetMin = Vector2.zero;
            _content.offsetMax = Vector2.zero;
        }
    }
}
