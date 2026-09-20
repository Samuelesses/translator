using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GameAudioTranslator.Models;

namespace GameAudioTranslator.Services;

/// <summary>
/// Lists candidate processes to offer in the "capture a specific application"
/// picker. Restricted to processes with a visible top-level window, which
/// reliably includes games like FiveM (they own the visible game window)
/// while filtering out the sea of background services/helpers Windows runs.
/// </summary>
public static class ProcessAudioSourceProvider
{
    public static List<ProcessAudioSource> GetCandidateProcesses()
    {
        var result = new List<ProcessAudioSource>();
        int currentPid = Environment.ProcessId;

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == currentPid)
                {
                    continue;
                }

                string title = process.MainWindowTitle;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                result.Add(new ProcessAudioSource(
                    process.Id,
                    process.ProcessName,
                    $"{title} — {process.ProcessName}.exe"));
            }
            catch
            {
                // Some processes (elevated/system) refuse property access; skip them.
            }
            finally
            {
                process.Dispose();
            }
        }

        return result.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
