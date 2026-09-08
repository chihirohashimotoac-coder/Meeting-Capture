using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MeetingRecorder.Core.Diagnostics;

namespace MeetingRecorder.Audio;

/// <summary>
/// Lists the outbound TCP connections owned by this process.
/// </summary>
/// <remarks>
/// <para>
/// The product's central privacy claim is that meeting audio never leaves the
/// machine. That claim is only worth as much as a user's ability to check it, so
/// the application can report its own connections: <c>--diagnose</c> prints them,
/// and outside a model download the list should be empty.
/// </para>
/// <para>
/// This is evidence, not proof - a determined leak could use UDP, or a child
/// process. The full check is a packet capture, which is why T-45 in
/// docs/WINDOWS_E2E_TEST.md still exists. What this gives the user is a
/// five-second sanity check they will actually run.
/// </para>
/// <para>
/// It lives in the Windows platform project alongside
/// <see cref="WindowsSleepPreventer"/> because, like that class, it is an OS
/// integration rather than anything to do with audio.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class ProcessNetworkInspector
{
    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int dwOutBufLen,
        bool sort,
        int ipVersion,
        int tblClass,
        int reserved);

    /// <summary>
    /// Returns "address:port" for every established outbound connection this
    /// process owns, or <c>null</c> when the table could not be read (which is
    /// reported honestly rather than as "no connections").
    /// </summary>
    public static IReadOnlyList<string>? GetOutboundConnections()
    {
        // MIB_TCP_STATE_ESTAB. Listening sockets and half-open states are not
        // outbound traffic and would only add noise.
        const uint EstablishedState = 5;

        var pid = (uint)Environment.ProcessId;
        var bufferSize = 0;
        var buffer = IntPtr.Zero;

        try
        {
            GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (bufferSize <= 0)
            {
                return null;
            }

            buffer = Marshal.AllocHGlobal(bufferSize);
            if (GetExtendedTcpTable(buffer, ref bufferSize, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0)
            {
                return null;
            }

            var rowCount = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            var result = new List<string>();

            for (var i = 0; i < rowCount; i++)
            {
                var rowPtr = IntPtr.Add(buffer, sizeof(int) + (i * rowSize));
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);

                if (row.OwningPid != pid || row.State != EstablishedState)
                {
                    continue;
                }

                var address = new IPAddress(BitConverter.GetBytes(row.RemoteAddr));
                var port = ((row.RemotePort & 0xFF) << 8) | ((row.RemotePort >> 8) & 0xFF);
                result.Add($"{address}:{port}");
            }

            return result;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>Builds the host facts the diagnostics collector cannot read itself.</summary>
    public static DiagnosticEnvironment DescribeEnvironment(string appVersion)
    {
        bool elevated;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            elevated = new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            elevated = false;
        }

        return new DiagnosticEnvironment(
            appVersion,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            elevated,
            GetOutboundConnections());
    }
}
