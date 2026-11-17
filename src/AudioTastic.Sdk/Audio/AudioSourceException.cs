namespace AudioTastic.Sdk.Audio;

/// <summary>PG-02: thrown by input plugins when a file cannot be opened or decoded.</summary>
public class AudioSourceException : Exception
{
    public AudioSourceException()
    {
    }

    public AudioSourceException(string message)
        : base(message)
    {
    }

    public AudioSourceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
