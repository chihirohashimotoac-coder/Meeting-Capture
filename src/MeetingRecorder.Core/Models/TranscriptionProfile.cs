namespace MeetingRecorder.Core.Models;

/// <summary>
/// How much time the user is willing to spend on a transcription.
/// </summary>
/// <remarks>
/// <para>
/// There is one knob and it has three positions, because that is what the
/// choice actually is: everything underneath - beam width, temperature
/// fallback, whether speaker clustering runs - moves together along a single
/// axis from "give me something now" to "take as long as it needs". Exposing
/// beam size and a temperature increment separately would be four settings
/// nobody can reason about and three combinations that make no sense.
/// </para>
/// <para>
/// <see cref="Accurate"/> is exactly what this application did before the
/// setting existed, unchanged, so nobody's transcript quietly got worse when
/// the choice appeared. It is not the default any more, but it is still there
/// and it is still the same thing.
/// </para>
/// </remarks>
public enum TranscriptionProfile
{
    /// <summary>Greedy decoding, no temperature fallback, no speaker clustering.</summary>
    Fast = 0,

    /// <summary>A narrow beam and temperature fallback. The default.</summary>
    Balanced = 1,

    /// <summary>The settings this application used before profiles existed.</summary>
    Accurate = 2,
}

/// <summary>Names and one-line descriptions for the three profiles, in Japanese.</summary>
public static class TranscriptionProfiles
{
    public static string DisplayName(TranscriptionProfile profile) => profile switch
    {
        TranscriptionProfile.Fast => "高速",
        TranscriptionProfile.Balanced => "標準",
        TranscriptionProfile.Accurate => "高精度",
        _ => profile.ToString(),
    };

    public static string Description(TranscriptionProfile profile) => profile switch
    {
        TranscriptionProfile.Fast =>
            "貪欲探索・温度フォールバックなし・話者分離なし。最短時間で結果が欲しいとき。",
        TranscriptionProfile.Balanced =>
            "ビーム幅2・温度フォールバックあり。処理時間と精度の釣り合いを取った既定値。",
        TranscriptionProfile.Accurate =>
            "ビーム幅5・温度フォールバックあり。プロファイル導入前と同一の設定。時間はかかります。",
        _ => string.Empty,
    };

    public static IReadOnlyList<TranscriptionProfile> All { get; } = new[]
    {
        TranscriptionProfile.Fast,
        TranscriptionProfile.Balanced,
        TranscriptionProfile.Accurate,
    };
}
