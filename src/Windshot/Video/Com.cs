using System.Runtime.InteropServices;

namespace Windshot.Video;

/// <summary>
/// Calls COM methods through their vtables. Trimmed .NET apps have no built-in COM interop
/// ([ComImport] casts fail), so WASAPI and Media Foundation are reached this way instead.
/// </summary>
internal static unsafe partial class Com
{
    public static void* Slot(IntPtr obj, int index) => (*(void***)obj)[index];

    public static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new COMException($"{what} failed (0x{hr:X8})", hr);
    }

    public static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero)
            Marshal.Release(obj);
    }

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr result);

    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(IntPtr p);

    [LibraryImport("mfplat.dll")]
    public static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateAttributes(out IntPtr attributes, uint initialSize);

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateMediaType(out IntPtr type);

    [LibraryImport("mfreadwrite.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr reader);

    // ---- IMFAttributes (also IMFMediaType and IMFSample, which extend it) ----

    public static void SetUInt32(IntPtr attributes, Guid key, uint value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Slot(attributes, 21))(attributes, &key, value), "SetUINT32");

    public static void SetGuid(IntPtr attributes, Guid key, Guid value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(attributes, 24))(attributes, &key, &value), "SetGUID");

    public static uint GetUInt32(IntPtr attributes, Guid key)
    {
        uint value;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Slot(attributes, 7))(attributes, &key, &value), "GetUINT32");
        return value;
    }

    public static ulong GetUInt64(IntPtr attributes, Guid key)
    {
        ulong value;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong*, int>)Slot(attributes, 8))(attributes, &key, &value), "GetUINT64");
        return value;
    }

    public static bool TryGetInt32(IntPtr attributes, Guid key, out int value)
    {
        uint v;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Slot(attributes, 7))(attributes, &key, &v);
        value = (int)v;
        return hr >= 0;
    }
}

/// <summary>Media Foundation GUIDs used here.</summary>
internal static class MF
{
    public const uint Version = 0x00020070;
    public const uint FirstVideoStream = 0xFFFFFFFC;
    public const uint FirstAudioStream = 0xFFFFFFFD;
    public const uint EndOfStream = 0x2; // MF_SOURCE_READERF_ENDOFSTREAM

    public static readonly Guid EnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid SamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    public static readonly Guid NumChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    public static readonly Guid BitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    public static readonly Guid Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid Audio = new("73647561-0000-0010-8000-00AA00389B71");
    public static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00AA00389B71");
    public static readonly Guid Pcm = new("00000001-0000-0010-8000-00AA00389B71");
}
