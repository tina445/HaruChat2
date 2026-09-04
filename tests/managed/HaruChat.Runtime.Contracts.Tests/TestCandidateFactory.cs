using HaruChat.Runtime.Memory;

sealed class TestCandidateFactory : IMemoryCandidateFactory
{
    public MemoryItem? Create(string characterId, string sessionId, string userInput, string assistantResponse, DateTimeOffset now, DateTimeOffset? expiresAt)
        => new MemoryItem("candidate-" + sessionId, characterId, "candidate", 50, now, now, sessionId, expiresAt);
}
