using System.Runtime.InteropServices;

namespace Immediate.Jobs;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct AcquisitionWindow(int Max, TimeSpan? Period);
