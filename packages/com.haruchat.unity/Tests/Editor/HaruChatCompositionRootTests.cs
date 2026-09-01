using HaruChat.Runtime.Models;
using NUnit.Framework;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HaruChat.Unity.Tests
{
    public sealed class HaruChatCompositionRootTests
    {
        [Test]
        public void Sample_scene_serializes_all_iPad_control_references()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
            var app = UnityEngine.Object.FindFirstObjectByType<HaruChatApp>();
            Assert.That(app, Is.Not.Null);
            var serialized = new SerializedObject(app);
            foreach (var name in new[] { "_composer", "_status", "_diagnostics", "_characterButton", "_addCharacterButton", "_importModelButton", "_previewButton", "_unloadButton", "_newConversationButton", "_cancelButton", "_sendButton", "_drawerToggleButton", "_messages", "_responsiveLayout" })
                Assert.That(serialized.FindProperty(name).objectReferenceValue, Is.Not.Null, name);
            var canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>();
            Assert.That(canvas.renderMode, Is.EqualTo(RenderMode.ScreenSpaceOverlay));
            Assert.That(UnityEngine.Object.FindFirstObjectByType<HaruChatSafeArea>(), Is.Not.Null);
            Assert.That(UnityEngine.Object.FindFirstObjectByType<HaruChatResponsiveLayout>(), Is.Not.Null);
        }

        [Test]
        public async Task Preview_stream_commits_and_unload_returns_to_no_model()
        {
            var rootDirectory = Path.Combine(Path.GetTempPath(), "haruchat-unity-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootDirectory);
            try
            {
                CreateFixture(rootDirectory, "haru");
                await using var root = new HaruChatCompositionRoot();
                root.Configure(Path.Combine(rootDirectory, "bundled"), Path.Combine(rootDirectory, "imported"), Path.Combine(rootDirectory, "qwen35.json"));
                await root.LoadPreviewAsync(CancellationToken.None);
                var reply = new StringBuilder();
                await root.SendAsync("안녕", item => { if (item.Kind == ModelEventKind.Token) reply.Append(item.Text); }, CancellationToken.None);
                Assert.That(root.State, Is.EqualTo(HaruChatClientState.Ready));
                Assert.That(reply.ToString(), Does.Contain("Unity Editor"));
                await root.NewConversationAsync(CancellationToken.None);
                await root.UnloadAsync(CancellationToken.None);
                Assert.That(root.State, Is.EqualTo(HaruChatClientState.NoModel));
            }
            finally { if (Directory.Exists(rootDirectory)) Directory.Delete(rootDirectory, true); }
        }

        [Test]
        public async Task Character_import_validates_copies_and_selects_the_bundle()
        {
            var rootDirectory = Path.Combine(Path.GetTempPath(), "haruchat-unity-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootDirectory);
            try
            {
                CreateFixture(rootDirectory, "haru");
                var source = Path.Combine(rootDirectory, "outside", "mira"); Directory.CreateDirectory(source);
                File.WriteAllText(Path.Combine(source, "manifest.json"), "{\"schemaVersion\":1,\"id\":\"mira\",\"displayName\":\"Mira\"}");
                File.WriteAllText(Path.Combine(source, "system.md"), "Be Mira.");
                await using var root = new HaruChatCompositionRoot();
                root.Configure(Path.Combine(rootDirectory, "bundled"), Path.Combine(rootDirectory, "imported"), Path.Combine(rootDirectory, "qwen35.json"));
                await root.ImportCharacterAsync(source, CancellationToken.None);
                Assert.That(root.Characters.Characters.Count, Is.EqualTo(2));
                Assert.That(root.SelectedCharacter.Id, Is.EqualTo("mira"));
                Assert.That(File.Exists(Path.Combine(rootDirectory, "imported", "mira", "system.md")), Is.True);
            }
            finally { if (Directory.Exists(rootDirectory)) Directory.Delete(rootDirectory, true); }
        }

        private static void CreateFixture(string root, string id)
        {
            var character = Path.Combine(root, "bundled", id); Directory.CreateDirectory(character);
            File.WriteAllText(Path.Combine(character, "manifest.json"), "{\"schemaVersion\":1,\"id\":\"" + id + "\",\"displayName\":\"Haru\"}");
            File.WriteAllText(Path.Combine(character, "system.md"), "Be Haru.");
            File.WriteAllText(Path.Combine(root, "qwen35.json"), "{\"id\":\"qwen35\",\"schemaVersion\":1,\"namedTemplate\":\"Qwen3.5\",\"contextWindowTokens\":128,\"maximumOutputTokens\":8,\"temperature\":0.7,\"topK\":40,\"topP\":0.9}");
        }
    }
}
