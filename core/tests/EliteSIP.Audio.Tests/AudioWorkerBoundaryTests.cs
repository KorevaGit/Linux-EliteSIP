#pragma warning disable CA2201 // Проверяем обработку искусственно введённых ошибок устройства и среды.
using System.Runtime.InteropServices;

namespace EliteSIP.Audio.Tests;

public sealed class AudioWorkerBoundaryTests
{
    [Fact]
    public void Managed_worker_fault_and_failed_diagnostics_do_not_escape_boundary()
    {
        Exception? captured = null;
        AudioWorkerBoundary.Run(() => throw new COMException("device invalidated"), error => captured = error);
        Assert.IsType<COMException>(captured);
        AudioWorkerBoundary.Run(() => throw new InvalidOperationException("bad callback"), _ => throw new IOException("bad log"));
    }
    [Fact]
    public void Corrupted_state_and_memory_exhaustion_are_not_swallowed()
    {
        Assert.False(AudioWorkerBoundary.IsRecoverableManaged(new OutOfMemoryException()));
        Assert.False(AudioWorkerBoundary.IsRecoverableManaged(new AccessViolationException()));
        Assert.False(AudioWorkerBoundary.IsRecoverableManaged(new SEHException()));
    }
}

#pragma warning restore CA2201
