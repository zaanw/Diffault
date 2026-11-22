using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ErrorHunt;

internal sealed class LocalDiagnostics(string filePath)
{
    private const int SizeLimit = 256 * 1024;
    private readonly object gate = new();

    internal static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ErrorHunt");

    internal static LocalDiagnostics Default { get; } =
        new(Path.Combine(DataDirectory, "diagnostics.log"));

    internal void Record(string operation, Exception? exception = null)
    {
        var entry = new StringBuilder()
            .Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            .Append(' ').Append(operation)
            .Append(" app=").Append(typeof(LocalDiagnostics).Assembly.GetName().Version)
            .Append(" os=").Append(Environment.OSVersion.Version)
            .Append(" arch=").Append(RuntimeInformation.ProcessArchitecture);
        for (int depth = 0; exception is not null && depth < 4; depth++, exception = exception.InnerException)
        {
            // Messages, paths, stack frames, and process output can contain user data.
            entry.Append(" error=").Append(exception.GetType().Name)
                .Append("/0x").Append(exception.HResult.ToString("X8", CultureInfo.InvariantCulture));
            if (exception is EngineFailure engine) entry.Append(" exit=").Append(engine.ExitCode);
        }

        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                using var stream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                if (stream.Length >= SizeLimit) stream.SetLength(0);
                stream.Seek(0, SeekOrigin.End);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.WriteLine(entry);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A full disk or a profile without write access must not terminate the app.
            System.Diagnostics.Trace.WriteLine(entry);
        }
    }

    internal string Read()
    {
        lock (gate)
        {
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var buffer = new char[SizeLimit];
                var length = reader.ReadBlock(buffer, 0, buffer.Length);
                return new string(buffer, 0, length);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                return "";
            }
        }
    }
}

internal sealed class EngineFailure(int exitCode) : Exception("The analysis engine could not read the input.")
{
    internal int ExitCode { get; } = exitCode;
}
