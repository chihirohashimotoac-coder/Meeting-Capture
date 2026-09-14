using System.Windows;
using MeetingRecorder.Core.Models;

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

/// <param name="Profile">The transcription speed profile this row selects.</param>
/// <param name="Label">What the combo box shows.</param>
public sealed record ProfileRow(TranscriptionProfile Profile, string Label);

/// <param name="Mode">The minutes generation mode this row selects.</param>
/// <param name="Label">What the combo box shows.</param>
public sealed record MinutesModeRow(MinutesGenerationMode Mode, string Label);
