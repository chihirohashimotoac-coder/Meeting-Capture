using System.Windows;

namespace MeetingRecorder.App.Views;

/// <summary>
/// Explicit row types for the list and combo controls.
/// </summary>
/// <remarks>
/// Anonymous types would be shorter, but they are internal and unnamed, which
/// makes WPF binding failures silent and impossible to check at compile time.
/// Named public types let the markup compiler and the UI smoke tests catch a
/// renamed property.
/// </remarks>
public sealed record DeviceRow(string Name, string? Id);

public sealed record ModelRow(string Label, string Id);

public sealed record PreflightRow(string Icon, string Title, string Detail, string Remedy, Visibility RemedyVisibility);

public sealed record RecoveryRow(string Name, string Started, string Duration, int Segments);
