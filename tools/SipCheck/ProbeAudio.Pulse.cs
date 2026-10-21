using EliteSIP.Audio;
using EliteSIP.Audio.Pulse;

namespace EliteSIP.Tools.SipCheck;

/// <summary>
/// Движок тракта Linux. Стоит на месте ProbeAudio.Wasapi.cs общего стенда:
/// остальные исходники стенда те же, что у Windows-версии.
/// </summary>
internal static partial class ProbeAudio
{
    private static partial IVoiceAudioEngine Open(VoiceAudioConfiguration configuration) =>
        new PulseVoiceAudioEngine(configuration);
}
