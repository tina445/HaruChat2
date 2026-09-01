#nullable enable
using TMPro;
using UnityEngine;

namespace HaruChat.Unity
{
    /// <summary>Applies the scene-selected Korean TMP asset to all authored UI text.</summary>
    public sealed class HaruChatTypography : MonoBehaviour
    {
        [SerializeField] private TMP_FontAsset _font = null!;
        private void Awake()
        {
            foreach (var text in GetComponentsInChildren<TMP_Text>(true)) text.font = _font;
        }
    }
}
