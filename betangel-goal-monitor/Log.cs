using System.Text;

namespace GoalMonitor;

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write(message, ConsoleColor.Gray);
    public static void Signal(string message) => Write(message, ConsoleColor.Cyan);
    public static void Trade(string message) => Write(message, ConsoleColor.Green);
    public static void Warn(string message) => Write(message, ConsoleColor.Yellow);
    public static void Error(string message) => Write(message, ConsoleColor.Red);

    private static void Write(string message, ConsoleColor colour)
    {
        lock (Gate)
        {
            Console.ForegroundColor = colour;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");
            Console.ResetColor();
        }
    }
}

/// <summary>Append-only CSV file with a header row.</summary>
public sealed class CsvLog : IDisposable
{
    private readonly StreamWriter _writer;

    public CsvLog(string path, params string[] header)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var isNew = !File.Exists(path);
        _writer = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = true };
        if (isNew) Write(header);
    }

    public void Write(params string[] values) =>
        _writer.WriteLine(string.Join(",", values.Select(Escape)));

    private static string Escape(string v) =>
        v.IndexOfAny([',', '"', '\n']) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;

    public void Dispose() => _writer.Dispose();
}
