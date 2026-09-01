#nullable enable
using UnityEngine;
using UnityEngine.UI;

namespace HaruChat.Unity
{
    /// <summary>Switches the scene-authored control rail between a landscape column and a compact drawer.</summary>
    public sealed class HaruChatResponsiveLayout : MonoBehaviour
    {
        [SerializeField] private RectTransform _shell = null!;
        [SerializeField] private RectTransform _drawer = null!;
        [SerializeField] private LayoutElement _drawerLayout = null!;
        [SerializeField] private float _compactAspectRatio = 1.18f;
        [SerializeField] private float _compactDrawerWidth = 344f;

        private bool _compact;
        private bool _drawerOpen;
        private Vector2Int _lastSize;

        private void Awake() { Refresh(force: true); }
        private void Update()
        {
            var size = new Vector2Int(Screen.width, Screen.height);
            if (size != _lastSize) Refresh(force: false);
        }

        public void ToggleDrawer()
        {
            if (!_compact) return;
            _drawerOpen = !_drawerOpen;
            Apply();
        }

        public void CloseDrawer()
        {
            if (!_compact || !_drawerOpen) return;
            _drawerOpen = false;
            Apply();
        }

        private void Refresh(bool force)
        {
            _lastSize = new Vector2Int(Screen.width, Screen.height);
            var compact = Screen.width <= Screen.height || (float)Screen.width / Mathf.Max(1, Screen.height) < _compactAspectRatio;
            if (!force && compact == _compact) return;
            _compact = compact;
            _drawerOpen = !_compact;
            Apply();
        }

        private void Apply()
        {
            _drawerLayout.ignoreLayout = _compact;
            if (_compact)
            {
                _drawer.anchorMin = new Vector2(0, 0);
                _drawer.anchorMax = new Vector2(0, 1);
                _drawer.pivot = new Vector2(0, .5f);
                _drawer.anchoredPosition = Vector2.zero;
                _drawer.sizeDelta = new Vector2(_compactDrawerWidth, 0);
                _drawer.gameObject.SetActive(_drawerOpen);
            }
            else
            {
                _drawer.gameObject.SetActive(true);
            }
            LayoutRebuilder.ForceRebuildLayoutImmediate(_shell);
        }
    }
}
