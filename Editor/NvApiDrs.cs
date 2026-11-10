using System;
using System.Runtime.InteropServices;

namespace GSyncProfileGuard.Editor
{
    /// <summary>
    /// Minimal wrapper over NVIDIA's driver settings API (NVAPI "DRS"), the same interface
    /// NVIDIA Profile Inspector uses. nvapi64.dll ships with every NVIDIA driver; functions
    /// are looked up by ID through nvapi_QueryInterface.
    ///
    /// Structs are handled as raw byte buffers at fixed offsets rather than marshalled
    /// structs, because they are large fixed-size wide-char arrays with versioned sizes.
    /// Sizes and offsets were checked against driver 32.0.16.1742 (2026-11-09).
    /// </summary>
    internal sealed class NvApiDrs : IDisposable
    {
        // ---- Status codes ----
        public const int NVAPI_OK = 0;
        public const int NVAPI_SETTING_NOT_FOUND = -160;

        // ---- Function IDs (nvapi_interface.h) ----
        const uint ID_Initialize = 0x0150E828;
        const uint ID_GetErrorMessage = 0x6C2D048C;
        const uint ID_DRS_CreateSession = 0x0694D52E;
        const uint ID_DRS_DestroySession = 0xDAD9CFF8;
        const uint ID_DRS_LoadSettings = 0x375DBD6B;
        const uint ID_DRS_SaveSettings = 0xFCBC7E14;
        const uint ID_DRS_FindApplicationByName = 0xEEE566B2;
        const uint ID_DRS_FindProfileByName = 0x7E4A9A0B;
        const uint ID_DRS_CreateProfile = 0xCC176068;
        const uint ID_DRS_CreateApplication = 0x4347A9DE;
        const uint ID_DRS_GetProfileInfo = 0x61CD6FD6;
        const uint ID_DRS_GetSetting = 0x73BF8338;
        const uint ID_DRS_SetSetting = 0x577DD202;
        const uint ID_DRS_EnumApplications = 0x7FA2173A;
        const uint ID_DRS_RestoreProfileDefaultSetting = 0x53F0381E;

        // ---- Struct layouts (sizes in bytes; version = size | (structVersion << 16)) ----
        const int StrBytes = 2048 * 2; // NvAPI_UnicodeString: 2048 UTF-16 chars

        // NVDRS_SETTING_V1: version, settingName, settingId, settingType, settingLocation,
        // isCurrentPredefined, isPredefinedValid, predefinedValue (union, 4100), currentValue (union, 4100)
        const int SettingSize = 12320;
        const uint SettingVersion = 12320u | (1u << 16);
        const int Setting_Id = 4100;
        const int Setting_Type = 4104;
        const int Setting_Current = 8220;
        const int NVDRS_DWORD_TYPE = 0;

        // NVDRS_APPLICATION_V4: version, isPredefined, appName, userFriendlyName, launcher,
        // fileInFolder, flags (isMetro:1, isCommandLine:1), commandLine. V3 lacks commandLine.
        const int AppSizeV4 = 20492;
        const uint AppVersionV4 = 20492u | (4u << 16);
        const uint AppVersionV3 = 16396u | (3u << 16);
        const int App_Name = 8;

        // NVDRS_PROFILE_V1: version, profileName, gpuSupport, isPredefined, numOfApps, numOfSettings
        const int ProfileSize = 4116;
        const uint ProfileVersion = 4116u | (1u << 16);
        const int Profile_Name = 4;
        const int Profile_IsPredefined = 4104;

        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr QueryInterface(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnVoid();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnErrMsg(int status, IntPtr buf64);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnCreateSession(out IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnSession(IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnFindApp(IntPtr session, [MarshalAs(UnmanagedType.LPWStr)] string appName, out IntPtr profile, IntPtr application);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnFindProfile(IntPtr session, [MarshalAs(UnmanagedType.LPWStr)] string profileName, out IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnCreateProfile(IntPtr session, IntPtr profileInfo, out IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnProfileBuf(IntPtr session, IntPtr profile, IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnGetSetting(IntPtr session, IntPtr profile, uint settingId, IntPtr setting);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnEnumApps(IntPtr session, IntPtr profile, uint startIndex, ref uint appCount, IntPtr applications);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnProfileSetting(IntPtr session, IntPtr profile, uint settingId);

        static bool s_initialized;
        IntPtr _session;

        static T Fn<T>(uint id) where T : class
        {
            IntPtr p = QueryInterface(id);
            if (p == IntPtr.Zero)
                throw new NvApiException("NVAPI function 0x" + id.ToString("X8") + " is not available in this driver.", 0);
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        static void Check(int status, string what)
        {
            if (status != NVAPI_OK)
                throw new NvApiException(what + " failed: " + ErrorText(status), status);
        }

        public static string ErrorText(int status)
        {
            IntPtr buf = Marshal.AllocHGlobal(64);
            try
            {
                Zero(buf, 64);
                if (Fn<FnErrMsg>(ID_GetErrorMessage)(status, buf) == NVAPI_OK)
                    return Marshal.PtrToStringAnsi(buf) + " (" + status + ")";
            }
            catch (Exception) { /* fall through */ }
            finally { Marshal.FreeHGlobal(buf); }
            return "NVAPI status " + status;
        }

        /// <summary>Opens a settings session and loads the current driver settings.</summary>
        /// <exception cref="DllNotFoundException">No NVIDIA driver installed.</exception>
        public NvApiDrs()
        {
            if (!s_initialized)
            {
                Check(Fn<FnVoid>(ID_Initialize)(), "NvAPI_Initialize");
                s_initialized = true;
            }
            Check(Fn<FnCreateSession>(ID_DRS_CreateSession)(out _session), "NvAPI_DRS_CreateSession");
            Check(Fn<FnSession>(ID_DRS_LoadSettings)(_session), "NvAPI_DRS_LoadSettings");
        }

        public void Dispose()
        {
            if (_session != IntPtr.Zero)
            {
                try { Fn<FnSession>(ID_DRS_DestroySession)(_session); } catch (Exception) { }
                _session = IntPtr.Zero;
            }
        }

        public void Save() => Check(Fn<FnSession>(ID_DRS_SaveSettings)(_session), "NvAPI_DRS_SaveSettings");

        /// <summary>
        /// Finds the profile the driver would apply to <paramref name="appPath"/>.
        /// Returns false if no profile matches. <paramref name="matchedAppName"/> is the
        /// application entry that matched (a bare exe name or a full path).
        /// </summary>
        public bool TryFindApplication(string appPath, out IntPtr profile, out string matchedAppName)
        {
            profile = IntPtr.Zero;
            matchedAppName = null;
            IntPtr app = Marshal.AllocHGlobal(AppSizeV4);
            try
            {
                int status = 0;
                foreach (uint version in new[] { AppVersionV4, AppVersionV3 })
                {
                    Zero(app, AppSizeV4);
                    Marshal.WriteInt32(app, 0, unchecked((int)version));
                    status = Fn<FnFindApp>(ID_DRS_FindApplicationByName)(_session, appPath, out profile, app);
                    if (status == NVAPI_OK)
                    {
                        matchedAppName = Marshal.PtrToStringUni(app + App_Name);
                        return true;
                    }
                    if (status != NvApiIncompatibleStructVersion) break;
                }
                profile = IntPtr.Zero;
                return false;
            }
            finally { Marshal.FreeHGlobal(app); }
        }

        const int NvApiIncompatibleStructVersion = -9;

        public bool TryFindProfile(string name, out IntPtr profile)
            => Fn<FnFindProfile>(ID_DRS_FindProfileByName)(_session, name, out profile) == NVAPI_OK;

        public IntPtr CreateProfile(string name)
        {
            IntPtr info = Marshal.AllocHGlobal(ProfileSize);
            try
            {
                Zero(info, ProfileSize);
                Marshal.WriteInt32(info, 0, unchecked((int)ProfileVersion));
                WriteString(info, Profile_Name, name);
                Check(Fn<FnCreateProfile>(ID_DRS_CreateProfile)(_session, info, out IntPtr profile), "NvAPI_DRS_CreateProfile");
                return profile;
            }
            finally { Marshal.FreeHGlobal(info); }
        }

        public void AddApplication(IntPtr profile, string appPath)
        {
            IntPtr app = Marshal.AllocHGlobal(AppSizeV4);
            try
            {
                int status = 0;
                foreach (uint version in new[] { AppVersionV4, AppVersionV3 })
                {
                    Zero(app, AppSizeV4);
                    Marshal.WriteInt32(app, 0, unchecked((int)version));
                    WriteString(app, App_Name, appPath);
                    status = Fn<FnProfileBuf>(ID_DRS_CreateApplication)(_session, profile, app);
                    if (status != NvApiIncompatibleStructVersion) break;
                }
                Check(status, "NvAPI_DRS_CreateApplication");
            }
            finally { Marshal.FreeHGlobal(app); }
        }

        public void GetProfileInfo(IntPtr profile, out string name, out bool isPredefined)
        {
            IntPtr info = Marshal.AllocHGlobal(ProfileSize);
            try
            {
                Zero(info, ProfileSize);
                Marshal.WriteInt32(info, 0, unchecked((int)ProfileVersion));
                Check(Fn<FnProfileBuf>(ID_DRS_GetProfileInfo)(_session, profile, info), "NvAPI_DRS_GetProfileInfo");
                name = Marshal.PtrToStringUni(info + Profile_Name);
                isPredefined = Marshal.ReadInt32(info, Profile_IsPredefined) != 0;
            }
            finally { Marshal.FreeHGlobal(info); }
        }

        /// <summary>Reads a DWORD setting. Returns false if the profile doesn't set it.</summary>
        public bool TryGetDword(IntPtr profile, uint settingId, out uint value)
        {
            value = 0;
            IntPtr s = Marshal.AllocHGlobal(SettingSize);
            try
            {
                Zero(s, SettingSize);
                Marshal.WriteInt32(s, 0, unchecked((int)SettingVersion));
                int status = Fn<FnGetSetting>(ID_DRS_GetSetting)(_session, profile, settingId, s);
                if (status == NVAPI_SETTING_NOT_FOUND) return false;
                Check(status, "NvAPI_DRS_GetSetting 0x" + settingId.ToString("X8"));
                value = unchecked((uint)Marshal.ReadInt32(s, Setting_Current));
                return true;
            }
            finally { Marshal.FreeHGlobal(s); }
        }

        public void SetDword(IntPtr profile, uint settingId, uint value)
        {
            IntPtr s = Marshal.AllocHGlobal(SettingSize);
            try
            {
                Zero(s, SettingSize);
                Marshal.WriteInt32(s, 0, unchecked((int)SettingVersion));
                Marshal.WriteInt32(s, Setting_Id, unchecked((int)settingId));
                Marshal.WriteInt32(s, Setting_Type, NVDRS_DWORD_TYPE);
                Marshal.WriteInt32(s, Setting_Current, unchecked((int)value));
                Check(Fn<FnProfileBuf>(ID_DRS_SetSetting)(_session, profile, s), "NvAPI_DRS_SetSetting 0x" + settingId.ToString("X8"));
            }
            finally { Marshal.FreeHGlobal(s); }
        }

        /// <summary>Lists the application names (exe names or full paths) in a profile.</summary>
        public string[] GetApplicationNames(IntPtr profile)
        {
            var names = new System.Collections.Generic.List<string>();
            const int batch = 16;
            IntPtr buf = Marshal.AllocHGlobal(AppSizeV4 * batch);
            try
            {
                for (uint start = 0; ; start += batch)
                {
                    Zero(buf, AppSizeV4 * batch);
                    for (int i = 0; i < batch; i++)
                        Marshal.WriteInt32(buf + i * AppSizeV4, 0, unchecked((int)AppVersionV4));
                    uint count = batch;
                    int status = Fn<FnEnumApps>(ID_DRS_EnumApplications)(_session, profile, start, ref count, buf);
                    if (status == NvApiEndEnumeration) break;
                    Check(status, "NvAPI_DRS_EnumApplications");
                    for (int i = 0; i < count; i++)
                        names.Add(Marshal.PtrToStringUni(buf + i * AppSizeV4 + App_Name));
                    if (count < batch) break;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return names.ToArray();
        }

        const int NvApiEndEnumeration = -7;

        /// <summary>
        /// Removes the user's value for a setting: a predefined profile goes back to NVIDIA's
        /// default, a user profile drops the setting. Not-set is not an error.
        /// </summary>
        public void RestoreDefault(IntPtr profile, uint settingId)
        {
            int status = Fn<FnProfileSetting>(ID_DRS_RestoreProfileDefaultSetting)(_session, profile, settingId);
            if (status == NVAPI_SETTING_NOT_FOUND) return;
            Check(status, "NvAPI_DRS_RestoreProfileDefaultSetting 0x" + settingId.ToString("X8"));
        }

        static void Zero(IntPtr p, int bytes)
        {
            var zeros = new byte[bytes];
            Marshal.Copy(zeros, 0, p, bytes);
        }

        static void WriteString(IntPtr buf, int offset, string value)
        {
            if (value.Length > 2047) value = value.Substring(0, 2047);
            char[] chars = (value + "\0").ToCharArray();
            Marshal.Copy(chars, 0, buf + offset, chars.Length);
        }
    }

    internal sealed class NvApiException : Exception
    {
        public readonly int Status;
        public NvApiException(string message, int status) : base(message) { Status = status; }
    }
}
