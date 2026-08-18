namespace ObsidianSync.Server.Services;

public sealed class SyncConflictException(string message) : Exception(message);
