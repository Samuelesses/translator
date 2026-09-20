using NAudio.CoreAudioApi;

namespace GameAudioTranslator.Models;

public class AudioDeviceOption
{
    public string Name { get; }
    public MMDevice? Device { get; }
    public string? Id => Device?.ID;

    public AudioDeviceOption(string name, MMDevice? device)
    {
        Name = name;
        Device = device;
    }

    public override string ToString() => Name;
}
