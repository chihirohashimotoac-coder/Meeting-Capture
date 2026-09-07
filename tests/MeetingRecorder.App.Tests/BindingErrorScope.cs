using System.Diagnostics;
using System.Text;
using System.Windows;

namespace MeetingRecorder.App.Tests;

/// <summary>
/// Captures WPF data-binding failures while a window is built and laid out.
/// </summary>
/// <remarks>
/// A mistyped binding path is invisible at compile time and, at run time, only
/// shows up as a blank control plus a line in the debug trace. Since this
/// application is authored where it cannot be run interactively, that trace is
/// the signal: any binding error raised while a window is constructed and
/// measured fails the build.
/// </remarks>
public sealed class BindingErrorScope : IDisposable
{
    private readonly CollectingListener _listener = new();
    private readonly SourceLevels _previousLevel;

    public BindingErrorScope()
    {
        PresentationTraceSources.Refresh();
        _previousLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning | SourceLevels.Error;
        PresentationTraceSources.DataBindingSource.Listeners.Add(_listener);
    }

    public IReadOnlyList<string> Errors => _listener.Messages;

    /// <summary>Builds the visual tree without needing a real desktop window.</summary>
    public static void ForceLayout(FrameworkElement element)
    {
        element.Measure(new Size(1400, 900));
        element.Arrange(new Rect(0, 0, 1400, 900));
        element.UpdateLayout();
    }

    public string Describe()
    {
        var builder = new StringBuilder();
        foreach (var message in Errors)
        {
            builder.AppendLine(message);
        }

        return builder.ToString();
    }

    public void Dispose()
    {
        PresentationTraceSources.DataBindingSource.Listeners.Remove(_listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = _previousLevel;
    }

    private sealed class CollectingListener : TraceListener
    {
        private readonly List<string> _messages = new();
        private readonly StringBuilder _pending = new();

        public IReadOnlyList<string> Messages => _messages;

        public override void Write(string? message) => _pending.Append(message);

        public override void WriteLine(string? message)
        {
            _pending.Append(message);
            var text = _pending.ToString();
            _pending.Clear();

            // "Cannot find source" / "property not found" are the failures that
            // matter. Informational trace lines are ignored.
            if (text.Contains("Error", StringComparison.OrdinalIgnoreCase)
                || text.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
                || text.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                _messages.Add(text);
            }
        }
    }
}
