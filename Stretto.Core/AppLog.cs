namespace Stretto.Core;

/// <summary>
/// Where the engine and its stores report things worth a line in the log. Core does not
/// know where the log lives: the host sets <see cref="Sink"/>, and until it does a message
/// goes nowhere, which is what a test wants.
/// </summary>
public static class AppLog
{
    public static Action<string>? Sink { get; set; }

    public static void Write(string message) => Sink?.Invoke(message);
}
