#nullable enable
using TMPro;
using UnityEngine;

namespace HaruChat.Unity
{
    /// <summary>A scene-authored visual message template whose content is supplied by the chat session.</summary>
    public sealed class HaruChatMessageView : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI _body = null!;
        public void SetText(string value, TMP_FontAsset font) { _body.font = font; _body.text = value; }
        public void Append(string value) { _body.text += value; }
    }
}
