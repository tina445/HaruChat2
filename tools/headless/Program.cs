using HaruChat.Runtime.Characters;
using HaruChat.Runtime.Models;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: HaruChat.Headless <character-bundle-dir> <message>");
    return 2;
}

var character = new CharacterBundleLoader().Load(args[0]);
var adapter = new MockModelAdapter("headless-mock", request => new[]
{
    ModelEvent.Token("[mock] " + request.Messages[request.Messages.Count - 1].Text),
    ModelEvent.Completed(),
});
await using var session = await adapter.CreateSessionAsync(new ModelSessionOptions(2048), CancellationToken.None);
await using var chat = new CharacterChatService(character, new Conversation(), session, new PromptCompiler(), 2048);
await foreach (var item in chat.SendAsync(args[1], CancellationToken.None))
{
    if (item.Kind == ModelEventKind.Token) Console.Write(item.Text);
    if (item.IsTerminal) Console.WriteLine();
}
return 0;
