using HaruChat.Runtime.Characters;
using HaruChat.Runtime.Models;
using HaruChat.LlamaCpp;

// Legacy: <bundle-dir> <message>. Interactive: --catalog <directory> --character <id> [--model <gguf> --profile <json>].
var catalogIndex = Array.IndexOf(args, "--catalog");
var characterIndex = Array.IndexOf(args, "--character");
var modelIndex = Array.IndexOf(args, "--model");
var profileIndex = Array.IndexOf(args, "--profile");
if (catalogIndex >= 0 && (catalogIndex + 1 >= args.Length || characterIndex < 0 || characterIndex + 1 >= args.Length) || (catalogIndex < 0 && args.Length < 2) || (modelIndex >= 0) != (profileIndex >= 0) || (modelIndex >= 0 && (modelIndex + 1 >= args.Length || profileIndex + 1 >= args.Length)))
{
    Console.Error.WriteLine("Usage: HaruChat.Headless <bundle-dir> <message> [--model <gguf> --profile <json>]\n   or: HaruChat.Headless --catalog <directory> --character <id> [--model <gguf> --profile <json>]");
    return 2;
}

CharacterDefinition character;
string? firstMessage = null;
if (catalogIndex >= 0)
{
    var roots = Directory.GetDirectories(args[catalogIndex + 1]);
    var catalog = CharacterCatalog.Load(roots);
    character = catalog.Get(args[characterIndex + 1]);
    Console.WriteLine("Selected character: " + character.DisplayName);
}
else
{
    character = new CharacterBundleLoader().Load(args[0]); firstMessage = args[1];
}

IModelAdapter adapter; IAsyncDisposable? backend = null; ModelSessionOptions sessionOptions; var contextBudget = 2048;
if (modelIndex >= 0)
{
    var profile = ModelProfileLoader.Load(args[profileIndex + 1]); var local = new LlamaCppBackend(); backend = local;
    adapter = new LocalModelAdapter("headless-local", local, new ModelConfig(args[modelIndex + 1], profile.Id), profile); sessionOptions = new ModelSessionOptions(profile.ContextWindowTokens); contextBudget = profile.ContextWindowTokens;
}
else { adapter = new MockModelAdapter("headless-mock", request => new[] { ModelEvent.Token("[mock] " + request.Messages[request.Messages.Count - 1].Text), ModelEvent.Completed() }); sessionOptions = new ModelSessionOptions(2048); }

await using var ownedBackend = backend;
await using var session = await adapter.CreateSessionAsync(sessionOptions, CancellationToken.None);
await using var chat = new CharacterChatService(character, new Conversation(), session, new PromptCompiler(), contextBudget);
for (var message = firstMessage; message != null || catalogIndex >= 0; message = Console.ReadLine())
{
    if (catalogIndex >= 0) { Console.Write("You> "); if (message == null) continue; }
    if (string.IsNullOrWhiteSpace(message) || string.Equals(message, "/quit", StringComparison.OrdinalIgnoreCase)) break;
    await foreach (var item in chat.SendAsync(message, CancellationToken.None)) { if (item.Kind == ModelEventKind.Token) Console.Write(item.Text); if (item.IsTerminal) Console.WriteLine(); }
    if (catalogIndex < 0) break;
}
return 0;
