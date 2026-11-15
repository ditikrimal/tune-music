using System.Runtime.InteropServices;

namespace Tune.App.Audio;

/// <summary>Minimal P/Invoke surface of libmpv's client API (client.h, API 2.6).</summary>
internal static partial class MpvNative
{
    private const string Lib = "libmpv-2";

    public const int FormatString = 1;
    public const int FormatFlag = 3;
    public const int FormatDouble = 5;

    public const int EventShutdown = 1;
    public const int EventEndFile = 7;
    public const int EventFileLoaded = 8;
    public const int EventPropertyChange = 22;

    public const int EndReasonEof = 0;
    public const int EndReasonError = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public int EventId;
        public int Error;
        public ulong ReplyUserdata;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventProperty
    {
        public nint Name;
        public int Format;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EndFileData
    {
        public int Reason;
        public int Error;
        public long PlaylistEntryId;
        public long PlaylistInsertId;
        public int PlaylistInsertNumEntries;
    }

    [LibraryImport(Lib, EntryPoint = "mpv_create")]
    public static partial nint Create();

    [LibraryImport(Lib, EntryPoint = "mpv_initialize")]
    public static partial int Initialize(nint ctx);

    [LibraryImport(Lib, EntryPoint = "mpv_terminate_destroy")]
    public static partial void TerminateDestroy(nint ctx);

    [LibraryImport(Lib, EntryPoint = "mpv_set_option_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetOptionString(nint ctx, string name, string data);

    [LibraryImport(Lib, EntryPoint = "mpv_set_property_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetPropertyString(nint ctx, string name, string data);

    [LibraryImport(Lib, EntryPoint = "mpv_observe_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ObserveProperty(nint ctx, ulong replyUserdata, string name, int format);

    [LibraryImport(Lib, EntryPoint = "mpv_command")]
    public static partial int Command(nint ctx, nint[] args);

    [LibraryImport(Lib, EntryPoint = "mpv_wait_event")]
    public static partial nint WaitEvent(nint ctx, double timeout);

    [LibraryImport(Lib, EntryPoint = "mpv_wakeup")]
    public static partial void Wakeup(nint ctx);

    [LibraryImport(Lib, EntryPoint = "mpv_error_string")]
    public static partial nint ErrorString(int error);

    public static string Error(int code) => Marshal.PtrToStringUTF8(ErrorString(code)) ?? $"mpv error {code}";

    /// <summary>Runs an mpv command given as UTF-8 strings (null-terminated array).</summary>
    public static int Command(nint ctx, params string[] args)
    {
        var ptrs = new nint[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            return Command(ctx, ptrs);
        }
        finally
        {
            foreach (var p in ptrs) if (p != 0) Marshal.FreeCoTaskMem(p);
        }
    }
}
