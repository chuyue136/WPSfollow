using System.Runtime.InteropServices;

namespace WpsInkSync;

/// <summary>
/// 极简 IDispatch 晚绑定。ICC 的 UIAccess 进程里，WPS 返回的 COM 对象有时只能按
/// <c>IDispatch</c> 用，强转 <c>IWpsWindow</c> 会失败（插件日志：不是 WPS Window 接口）。
/// 这里直接用 DISPID 调 RangeFromPoint / GetPoint，绕开接口类型身份问题。
/// </summary>
static class WpsDispatch
{
    public const int DispRangeFromPoint = 110;
    public const int DispGetPoint = 112;

    const short DispatchMethod = 1;
    const ushort VT_I4 = 3, VT_DISPATCH = 9, VT_BYREF = 0x4000, VT_EMPTY = 0;
    static readonly int VarSize = IntPtr.Size == 8 ? 24 : 16;

    [StructLayout(LayoutKind.Sequential)]
    struct DISPPARAMS
    {
        public IntPtr rgvarg;
        public IntPtr rgdispidNamedArgs;
        public int cArgs;
        public int cNamedArgs;
    }

    public static object RangeFromPoint(IDispatchRaw disp, int x, int y) =>
        Invoke(disp, DispRangeFromPoint, new object[] { x, y });

    public static int GetTop(IDispatchRaw disp, object range)
    {
        var left = Marshal.AllocHGlobal(4);
        var top = Marshal.AllocHGlobal(4);
        var width = Marshal.AllocHGlobal(4);
        var height = Marshal.AllocHGlobal(4);
        try
        {
            Invoke(disp, DispGetPoint, new object[] { left, top, width, height, range });
            return Marshal.ReadInt32(top);
        }
        finally
        {
            Marshal.FreeHGlobal(left);
            Marshal.FreeHGlobal(top);
            Marshal.FreeHGlobal(width);
            Marshal.FreeHGlobal(height);
        }
    }

    static object Invoke(IDispatchRaw disp, int dispId, object[] args)
    {
        var n = args.Length;
        var varBuf = Marshal.AllocHGlobal(VarSize * Math.Max(1, n));
        var dpBuf = Marshal.AllocHGlobal(Marshal.SizeOf<DISPPARAMS>());
        var resBuf = Marshal.AllocHGlobal(VarSize);
        var dispPtrs = new IntPtr[n];
        try
        {
            for (var i = 0; i < n; i++)
            {
                var p = IntPtr.Add(varBuf, i * VarSize);
                var a = args[n - 1 - i];   // DISPPARAMS 的参数是反序的
                if (a is int iv) { WriteVt(p, VT_I4); Marshal.WriteInt32(p, 8, iv); }
                else if (a is IntPtr ip) { WriteVt(p, VT_I4 | VT_BYREF); Marshal.WriteIntPtr(p, 8, ip); }
                else if (a != null)
                {
                    dispPtrs[i] = Marshal.GetIDispatchForObject(a);
                    WriteVt(p, VT_DISPATCH);
                    Marshal.WriteIntPtr(p, 8, dispPtrs[i]);
                }
                else WriteVt(p, VT_EMPTY);
            }

            Marshal.StructureToPtr(new DISPPARAMS { rgvarg = varBuf, cArgs = n }, dpBuf, false);
            WriteVt(resBuf, VT_EMPTY);
            var iid = Guid.Empty;
            var hr = disp.Invoke(dispId, ref iid, 0, DispatchMethod, dpBuf, resBuf, IntPtr.Zero, IntPtr.Zero);
            if (hr != 0) throw Marshal.GetExceptionForHR(hr) ?? new COMException("IDispatch.Invoke", hr);

            if ((ushort)Marshal.ReadInt16(resBuf, 0) != VT_DISPATCH) return null;
            var pDisp = Marshal.ReadIntPtr(resBuf, 8);
            return pDisp == IntPtr.Zero ? null : Marshal.GetObjectForIUnknown(pDisp);
        }
        finally
        {
            foreach (var p in dispPtrs) if (p != IntPtr.Zero) Marshal.Release(p);
            Marshal.FreeHGlobal(varBuf);
            Marshal.FreeHGlobal(dpBuf);
            Marshal.FreeHGlobal(resBuf);
        }
    }

    static void WriteVt(IntPtr p, ushort vt) => Marshal.WriteInt16(p, 0, (short)vt);
}

[ComImport, Guid("00020400-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IDispatchRaw
{
    [PreserveSig] int GetTypeInfoCount(out int count);
    [PreserveSig] int GetTypeInfo(int iTInfo, int lcid, out IntPtr info);
    [PreserveSig] int GetIDsOfNames(ref Guid iid, IntPtr[] names, int cNames, int lcid, int[] dispIds);
    [PreserveSig] int Invoke(int dispId, ref Guid iid, int lcid, short flags,
        IntPtr pDispParams, IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr);
}
