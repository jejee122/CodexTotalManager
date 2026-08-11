namespace CodexModelManager.Services;

public sealed class OpenCodexAccountApiUnavailableException : Exception
{
    public OpenCodexAccountApiUnavailableException(string message) : base(message) { }

    public OpenCodexAccountApiUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}
