namespace MonoTorrent.Connections.Tracker;

public class HTTPTrackerConnectionParameters
{
    /// <summary>
    /// Setting this to 1 indicates that the client accepts a compact response.
    /// </summary>
    public bool Compact { get; }

    /// <summary>
    /// Number of peers that the client would like to receive from the tracker.
    /// </summary>
    public int? NumWant { get; }

    public static HTTPTrackerConnectionParameters Default { get; } = new Builder ().Build ();

    HTTPTrackerConnectionParameters (Builder builder)
    {
        Compact = builder.Compact;
        NumWant = builder.NumWant;
    }

    public class Builder
    {
        /// <summary>
        /// Setting this to 1 indicates that the client accepts a compact response.
        /// </summary>
        public bool Compact { get; set; } = true;

        /// <summary>
        /// Number of peers that the client would like to receive from the tracker.
        /// </summary>
        public int? NumWant { get; set; } = 100;

        public HTTPTrackerConnectionParameters Build ()
        {
            return new HTTPTrackerConnectionParameters (this);
        }
    }
}
