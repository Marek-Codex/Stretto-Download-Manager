using System.Diagnostics;

namespace Stretto.Tests;

public sealed class AssertionException(string message) : Exception(message);

/// <summary>Thrown by a test that cannot run here, which is not the same as one that failed.</summary>
public sealed class SkippedException(string reason) : Exception(reason);

/// <summary>The handful of checks this suite needs. Anything more would be a framework.</summary>
public static class Assert
{
    public static void True(bool condition, string what)
    {
        if (!condition)
            throw new AssertionException($"expected {what}");
    }

    public static void False(bool condition, string what) => True(!condition, what);

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{what}: expected {expected}, got {actual}");
    }
}

/// <summary>
/// Runs each test, keeps going after a failure, and reports at the end.
///
/// Carrying on past a failure matters here: these tests exercise different engine paths,
/// and knowing that three of six broke is a much better signal than stopping at the first.
/// </summary>
public sealed class Runner
{
    private readonly List<string> _failures = [];
    private int _passed;

    public async Task Run(string name, Func<Task> test)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await test();

            _passed++;
            Write(ConsoleColor.Green, "PASS");
            Console.WriteLine($" {name}  ({stopwatch.ElapsedMilliseconds} ms)");
        }
        catch (SkippedException skipped)
        {
            Write(ConsoleColor.DarkYellow, "SKIP");
            Console.WriteLine($" {name}  ({skipped.Message})");
        }
        catch (Exception exception)
        {
            var reason = exception is AssertionException
                ? exception.Message
                : $"{exception.GetType().Name}: {exception.Message}";

            _failures.Add($"{name} — {reason}");

            Write(ConsoleColor.Red, "FAIL");
            Console.WriteLine($" {name}  ({stopwatch.ElapsedMilliseconds} ms)");
            Console.WriteLine($"       {reason}");
        }
    }

    /// <summary>Returns the process exit code, so this is usable from a script or CI.</summary>
    public int Report()
    {
        Console.WriteLine();

        if (_failures.Count == 0)
        {
            Write(ConsoleColor.Green, $"All {_passed} tests passed.");
            Console.WriteLine();
            return 0;
        }

        Write(ConsoleColor.Red, $"{_failures.Count} failed, {_passed} passed:");
        Console.WriteLine();

        foreach (var failure in _failures)
            Console.WriteLine($"  - {failure}");

        return 1;
    }

    private static void Write(ConsoleColor colour, string text)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = colour;
        Console.Write(text);
        Console.ForegroundColor = previous;
    }
}
