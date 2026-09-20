namespace GameAudioTranslator.Models;

public class ProcessAudioSource
{
    public int ProcessId { get; }
    public string ProcessName { get; }
    public string DisplayName { get; }

    public ProcessAudioSource(int processId, string processName, string displayName)
    {
        ProcessId = processId;
        ProcessName = processName;
        DisplayName = displayName;
    }

    public override string ToString() => DisplayName;
}
