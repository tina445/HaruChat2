#nullable enable

using HaruChat.Runtime.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace HaruChat.Runtime.Characters
{
    public sealed class CharacterDefinition
    {
        public CharacterDefinition(string id, string displayName, string system, string? personality, string? style, string? scenario, IReadOnlyList<string> lore, IReadOnlyList<ModelMessage> examples, string contentHash)
        {
            Id = id; DisplayName = displayName; System = system; Personality = personality; Style = style; Scenario = scenario;
            Lore = Array.AsReadOnly((lore ?? throw new ArgumentNullException(nameof(lore))).ToArray());
            Examples = Array.AsReadOnly((examples ?? throw new ArgumentNullException(nameof(examples))).ToArray());
            ContentHash = contentHash;
        }
        public string Id { get; } public string DisplayName { get; } public string System { get; } public string? Personality { get; } public string? Style { get; } public string? Scenario { get; } public IReadOnlyList<string> Lore { get; } public IReadOnlyList<ModelMessage> Examples { get; } public string ContentHash { get; }
    }

    public sealed class CharacterBundleLoader
    {
        private readonly int _maximumFileBytes; private readonly int _maximumBundleBytes;
        public CharacterBundleLoader(int maximumFileBytes = 256 * 1024, int maximumBundleBytes = 1024 * 1024) { _maximumFileBytes = maximumFileBytes; _maximumBundleBytes = maximumBundleBytes; }
        public CharacterDefinition Load(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) throw new CharacterValidationException("Character root does not exist.");
            var rootInfo = new DirectoryInfo(root);
            if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0) throw new CharacterValidationException("Symlinked character roots are not allowed.");
            var canonicalRoot = Path.GetFullPath(root);
            var fullRoot = canonicalRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var consumed = 0;
            string Read(string name, bool required)
            {
                var path = Path.GetFullPath(Path.Combine(fullRoot, name));
                if (!path.StartsWith(fullRoot, StringComparison.Ordinal) || Path.IsPathRooted(name)) throw new CharacterValidationException("Path escapes character root: " + name);
                EnsureNoReparsePoint(canonicalRoot, path);
                if (!File.Exists(path)) { if (required) throw new CharacterValidationException("Required file is missing: " + name); return string.Empty; }
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new CharacterValidationException("Symlinks are not allowed: " + name);
                if (info.Length > _maximumFileBytes || consumed + info.Length > _maximumBundleBytes) throw new CharacterValidationException("Character bundle exceeds its size limit.");
                consumed += (int)info.Length;
                var bytes = File.ReadAllBytes(path);
                try { return new UTF8Encoding(false, true).GetString(bytes); } catch (DecoderFallbackException) { throw new CharacterValidationException("File is not strict UTF-8: " + name); }
            }
            var manifest = ParseManifest(Read("manifest.json", true));
            if (manifest.SchemaVersion != 1 || string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.DisplayName)) throw new CharacterValidationException("manifest.json has invalid required fields.");
            if (!string.Equals(Path.GetFileName(root), manifest.Id, StringComparison.Ordinal)) throw new CharacterValidationException("Bundle directory must equal manifest ID.");
            var lore = new List<string>(); var lorePath = Path.Combine(fullRoot, "lore");
            if (Directory.Exists(lorePath))
            {
                var loreInfo = new DirectoryInfo(lorePath); if ((loreInfo.Attributes & FileAttributes.ReparsePoint) != 0) throw new CharacterValidationException("Symlinked lore directory is not allowed.");
                foreach (var entry in Directory.GetFileSystemEntries(lorePath))
                {
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new CharacterValidationException("Symlinks are not allowed in lore.");
                    if (Directory.Exists(entry)) throw new CharacterValidationException("Lore must contain files only.");
                }
                foreach (var file in Directory.GetFiles(lorePath, "*.md").OrderBy(x => Path.GetFileName(x), StringComparer.Ordinal)) lore.Add(Read(Path.Combine("lore", Path.GetFileName(file)), false));
            }
            var examples = ParseExamples(Read("examples.jsonl", false));
            var sections = new[] { manifest.Id, manifest.DisplayName, Read("system.md", true), Read("personality.md", false), Read("style.md", false), Read("scenario.md", false) }.Concat(lore).Concat(examples.Select(x => x.Role + ":" + x.Text));
            return new CharacterDefinition(manifest.Id, manifest.DisplayName, sections.ElementAt(2), EmptyToNull(sections.ElementAt(3)), EmptyToNull(sections.ElementAt(4)), EmptyToNull(sections.ElementAt(5)), lore.AsReadOnly(), examples.AsReadOnly(), Hash(string.Join("\n", sections)));
        }
        private static void EnsureNoReparsePoint(string root, string path)
        {
            var relative = path.Substring(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = root;
            foreach (var segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new CharacterValidationException("Symlinks are not allowed: " + relative);
            }
        }
        private static string? EmptyToNull(string value) { return string.IsNullOrWhiteSpace(value) ? null : value; }
        private static string Hash(string value) { using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", string.Empty).ToLowerInvariant(); }
        private static Manifest ParseManifest(string json)
        {
            try { using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json))) return (Manifest)new DataContractJsonSerializer(typeof(Manifest)).ReadObject(stream)!; }
            catch (Exception error) { throw new CharacterValidationException("Invalid manifest.json: " + error.Message); }
        }
        private static List<ModelMessage> ParseExamples(string jsonl)
        {
            var result = new List<ModelMessage>(); if (string.IsNullOrWhiteSpace(jsonl)) return result;
            foreach (var line in jsonl.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                try { using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(line))) { var item = (Example)new DataContractJsonSerializer(typeof(Example)).ReadObject(stream)!; if (item.Role != "user" && item.Role != "assistant") throw new SerializationException(); result.Add(new ModelMessage(item.Role == "user" ? ModelRole.User : ModelRole.Assistant, item.Text ?? throw new SerializationException())); } }
                catch { throw new CharacterValidationException("Invalid examples.jsonl line."); }
            }
            return result;
        }
        [DataContract] private sealed class Manifest { [DataMember(Name = "schemaVersion")] public int SchemaVersion { get; set; } [DataMember(Name = "id")] public string? Id { get; set; } [DataMember(Name = "displayName")] public string? DisplayName { get; set; } }
        [DataContract] private sealed class Example { [DataMember(Name = "role")] public string? Role { get; set; } [DataMember(Name = "text")] public string? Text { get; set; } }
    }
    public sealed class CharacterValidationException : Exception { public CharacterValidationException(string message) : base(message) { } }

    public sealed class CharacterCatalog
    {
        private readonly Dictionary<string, CharacterDefinition> _byId;
        private readonly IReadOnlyList<CharacterDefinition> _characters;

        public CharacterCatalog(IEnumerable<CharacterDefinition> characters)
        {
            if (characters == null) throw new ArgumentNullException(nameof(characters));
            _byId = new Dictionary<string, CharacterDefinition>(StringComparer.OrdinalIgnoreCase);
            var copy = new List<CharacterDefinition>();
            foreach (var character in characters)
            {
                if (character == null || string.IsNullOrWhiteSpace(character.Id)) throw new CharacterValidationException("Character IDs must be non-empty.");
                var normalizedId = character.Id.Normalize(NormalizationForm.FormC);
                if (!_byId.TryAdd(normalizedId, character)) throw new CharacterValidationException("Duplicate character ID: " + character.Id);
                copy.Add(character);
            }
            _characters = Array.AsReadOnly(copy.ToArray());
        }

        public IReadOnlyList<CharacterDefinition> Characters { get { return _characters; } }
        public CharacterDefinition Get(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !_byId.TryGetValue(id.Normalize(NormalizationForm.FormC), out var character)) throw new KeyNotFoundException("Unknown character ID: " + id);
            return character;
        }
        public static CharacterCatalog Load(IEnumerable<string> bundleRoots, CharacterBundleLoader? loader = null)
        {
            if (bundleRoots == null) throw new ArgumentNullException(nameof(bundleRoots));
            var actualLoader = loader ?? new CharacterBundleLoader();
            return new CharacterCatalog(bundleRoots.Select(actualLoader.Load));
        }
    }

    public sealed class Conversation
    {
        private readonly List<ModelMessage> _committed = new List<ModelMessage>(); private ModelMessage? _pending;
        public IReadOnlyList<ModelMessage> Committed { get { return Array.AsReadOnly(_committed.ToArray()); } }
        public void BeginUserTurn(string text) { if (_pending != null) throw new InvalidOperationException("A turn is already pending."); _pending = new ModelMessage(ModelRole.User, text); }
        public void CommitAssistant(string text) { if (_pending == null) throw new InvalidOperationException("No pending turn."); _committed.Add(_pending); _committed.Add(new ModelMessage(ModelRole.Assistant, text)); _pending = null; }
        public void RollbackPending() { _pending = null; }
        public void Reset() { _pending = null; _committed.Clear(); }
    }

    public sealed class PromptCompiler
    {
        public ModelRequest Compile(CharacterDefinition character, Conversation conversation, string userInput, int contextBudget, GenerationOptions? generation = null)
        {
            if (contextBudget <= 0) throw new ArgumentOutOfRangeException(nameof(contextBudget));
            var messages = new List<ModelMessage>();
            Add(messages, ModelRole.System, character.System); Add(messages, ModelRole.System, character.Personality); Add(messages, ModelRole.System, character.Style); Add(messages, ModelRole.System, character.Scenario);
            foreach (var item in character.Lore) Add(messages, ModelRole.System, item); messages.AddRange(character.Examples);
            var retained = new List<ModelMessage>(conversation.Committed); retained.Add(new ModelMessage(ModelRole.User, userInput));
            while (Estimate(messages) + Estimate(retained) > contextBudget && retained.Count > 1) retained.RemoveRange(0, Math.Min(2, retained.Count - 1));
            if (Estimate(messages) + Estimate(retained) > contextBudget) throw new ContextBudgetExceededException();
            messages.AddRange(retained); return new ModelRequest(messages, generation);
        }
        private static void Add(List<ModelMessage> messages, ModelRole role, string? text) { if (!string.IsNullOrWhiteSpace(text)) messages.Add(new ModelMessage(role, text)); }
        private static int Estimate(IEnumerable<ModelMessage> messages) { return messages.Sum(x => Math.Max(1, (x.Text.Length + 3) / 4)); }
    }
    public sealed class ContextBudgetExceededException : Exception { public ContextBudgetExceededException() : base("The required prompt exceeds the context budget.") { } }
}
