using System.IO;
using DesktopApp.Models;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace DesktopApp.Security;

// Redact after rendering so structured properties and exception text are covered.
public sealed class RedactingLogFormatter : ITextFormatter
{
    private readonly MessageTemplateTextFormatter _inner = new(
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}");

    public void Format(LogEvent logEvent, TextWriter output)
    {
        using var buffer = new StringWriter();
        _inner.Format(logEvent, buffer);
        output.Write(DiagnosticRedactor.Redact(buffer.ToString(), Session.Username, Session.Password));
    }
}
