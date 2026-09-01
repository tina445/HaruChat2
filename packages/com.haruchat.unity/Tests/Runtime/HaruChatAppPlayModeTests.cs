using NUnit.Framework;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace HaruChat.Unity.Tests
{
    public sealed class HaruChatAppPlayModeTests
    {
        [UnityTest]
        public IEnumerator Sample_scene_contains_the_iPad_client_canvas()
        {
            yield return SceneManager.LoadSceneAsync("SampleScene", LoadSceneMode.Single);
            yield return null;
            Assert.That(Object.FindFirstObjectByType<HaruChatApp>(), Is.Not.Null);
            Assert.That(Object.FindFirstObjectByType<Canvas>(), Is.Not.Null);
        }
    }
}
