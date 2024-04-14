namespace MonoTorrent.Logging;

public sealed class NullLogger : ILogger
{
    public void Info (string message)
    {
    }

    public void Debug (string message)
    {
    }

    public void Error (string message)
    {
    }

    public static ILogger Instance { get; } = new NullLogger ();
    NullLogger () { }
}
