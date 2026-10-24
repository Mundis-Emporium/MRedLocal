using System.Diagnostics;

namespace RedMLocalSetup;

public sealed class ServerProcessProbe : IServerProcessProbe
{
    public bool IsRunning(ServerCandidate candidate)
    {
        // An FXServer process can run outside its data folder. Refuse conservatively.
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.ProcessName.StartsWith("FXServer", StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (InvalidOperationException) { }
            }
        }
        return false;
    }
}
