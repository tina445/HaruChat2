#nullable enable
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HaruChat.Unity
{
    /// <summary>Owns message projection and scrolling; templates remain authored in the scene.</summary>
    public sealed class HaruChatMessageList : MonoBehaviour
    {
        [SerializeField] private Transform _content = null!;
        [SerializeField] private ScrollRect _scroll = null!;
        [SerializeField] private TMP_FontAsset _font = null!;
        [SerializeField] private HaruChatMessageView _systemTemplate = null!;
        [SerializeField] private HaruChatMessageView _userTemplate = null!;
        [SerializeField] private HaruChatMessageView _assistantTemplate = null!;
        private readonly List<HaruChatMessageView> _items = new List<HaruChatMessageView>();

        public HaruChatMessageView Add(string role, string text)
        {
            var template = role == "user" ? _userTemplate : role == "assistant" ? _assistantTemplate : _systemTemplate;
            var item = Instantiate(template, _content);
            item.gameObject.SetActive(true);
            item.SetText(text, _font);
            _items.Add(item);
            ScrollToEnd();
            return item;
        }

        public void Clear()
        {
            foreach (var item in _items) Destroy(item.gameObject);
            _items.Clear();
        }

        public void ScrollToEnd()
        {
            Canvas.ForceUpdateCanvases();
            _scroll.verticalNormalizedPosition = 0;
        }
    }
}
