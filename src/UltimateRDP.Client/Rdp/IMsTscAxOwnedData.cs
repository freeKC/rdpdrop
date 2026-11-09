using System.Runtime.InteropServices;

namespace UltimateRDP.Client.Rdp;

/// <summary>
/// A copy of the IMsTscAx interface of the Remote Desktop control, written by hand so that
/// <see cref="SendOnVirtualChannel"/> takes the data as a BSTR the caller allocated and
/// keeps alive, instead of a string that the marshaller frees as soon as the call returns.
/// </summary>
/// <remarks>
/// <para>
/// Checked against Interop.MSTSCLib.dll of Devolutions.MsRdpEx 2026.10.26 (interop\legacy)
/// by reflection: IMsTscAx has the IID 8C11EFAE-92C3-11D1-BC1E-00C04FA31489, is dual
/// (TypeLibTypeFlags FDual and FDispatchable, no InterfaceType attribute), derives from
/// IMsTscAx_Redist, which declares no member, and declares 30 methods. SendOnVirtualChannel
/// is the 30th, DISPID 34, so its slot in the table of functions is 36 after the three of
/// IUnknown and the four of IDispatch; Marshal.GetComSlotForMethodInfo of the .NET Framework
/// gives 36 for it.
/// </para>
/// <para>
/// The methods below repeat the 30 methods in the same order and with the same
/// signatures, property accessors written as methods so that their order is the order of
/// the type library (put before get). Only SendOnVirtualChannel is called. Before using
/// this interface, <see cref="OwnedChannelData"/> checks at run time that it has the IID
/// and declares the same methods, in the same order, as IMsTscAx of the interop assembly.
/// </para>
/// </remarks>
[ComImport]
[Guid("8C11EFAE-92C3-11D1-BC1E-00C04FA31489")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IMsTscAxOwnedData
{
    [DispId(1)]
    void set_Server([MarshalAs(UnmanagedType.BStr)] string pServer);

    [DispId(1)]
    [return: MarshalAs(UnmanagedType.BStr)]
    string get_Server();

    [DispId(2)]
    void set_Domain([MarshalAs(UnmanagedType.BStr)] string pDomain);

    [DispId(2)]
    [return: MarshalAs(UnmanagedType.BStr)]
    string get_Domain();

    [DispId(3)]
    void set_UserName([MarshalAs(UnmanagedType.BStr)] string pUserName);

    [DispId(3)]
    [return: MarshalAs(UnmanagedType.BStr)]
    string get_UserName();

    [DispId(4)]
    void set_DisconnectedText([MarshalAs(UnmanagedType.BStr)] string pDisconnectedText);

    [DispId(4)]
    [return: MarshalAs(UnmanagedType.BStr)]
    string get_DisconnectedText();

    [DispId(5)]
    void set_ConnectingText([MarshalAs(UnmanagedType.BStr)] string pConnectingText);

    [DispId(5)]
    [return: MarshalAs(UnmanagedType.BStr)]
    string get_ConnectingText();

    [DispId(6)]
    short get_Connected();

    [DispId(12)]
    void set_DesktopWidth(int pVal);

    [DispId(12)]
    int get_DesktopWidth();

    [DispId(13)]
    void set_DesktopHeight(int pVal);

    [DispId(13)]
    int get_DesktopHeight();

    [DispId(16)]
    void set_StartConnected(int pfStartConnected);

    [DispId(16)]
    int get_StartConnected();

    [DispId(17)]
    int get_HorizontalScrollBarVisible();

    [DispId(18)]
    int get_VerticalScrollBarVisible();

    [DispId(19)]
    void set_FullScreenTitle([MarshalAs(UnmanagedType.BStr)] string value);

    [DispId(20)]
    int get_CipherStrength();

    [DispId(21)]
    [return: MarshalAs(UnmanagedType.BStr)]
    string get_Version();

    [DispId(22)]
    int get_SecuredSettingsEnabled();

    [DispId(97)]
    [return: MarshalAs(UnmanagedType.Interface)]
    object get_SecuredSettings();

    [DispId(98)]
    [return: MarshalAs(UnmanagedType.Interface)]
    object get_AdvancedSettings();

    [DispId(99)]
    [return: MarshalAs(UnmanagedType.Interface)]
    object get_Debugger();

    [DispId(30)]
    void Connect();

    [DispId(31)]
    void Disconnect();

    [DispId(33)]
    void CreateVirtualChannels([MarshalAs(UnmanagedType.BStr)] string newVal);

    /// <summary>
    /// Sends <paramref name="chanData"/>, a BSTR the caller allocated, on the channel. The
    /// caller frees it once the data surely left, not when the call returns.
    /// </summary>
    [DispId(34)]
    void SendOnVirtualChannel([MarshalAs(UnmanagedType.BStr)] string chanName, IntPtr chanData);
}
