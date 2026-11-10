# G-Sync Profile Guard

An editor-only Unity package for Windows with NVIDIA GPUs. It stops the Unity editor from triggering G-Sync, while G-Sync stays on for everything else.

## The problem it fixes

With G-Sync enabled (even in "full screen mode" only), clicking into a D3D12 Unity editor can switch the displays into variable refresh. Some monitors blank while they re-lock, often on every display at once, and sometimes a monitor stays black (Win + Ctrl + Shift + B brings it back). Turning G-Sync off globally stops it, but costs G-Sync in games.

**The root cause:** NVIDIA's built-in **"Unity 3D"** driver profile claims every `unity.exe` and sets G-Sync to **"full screen and windowed"** for it. That per-app setting overrides your global choice, so even with G-Sync set to "full screen mode" only, every Unity editor still gets windowed G-Sync. NVIDIA Control Panel's per-program "Monitor Technology: Fixed Refresh" didn't fix it, because the profile's G-Sync mode stays at "full screen and windowed".

What does work is setting G-Sync to Off and Disallow in the driver profile the editor actually uses. NVIDIA Profile Inspector can do that by hand. This package does it automatically and keeps it in place.

## What it does

Once per editor session (on startup), it asks the NVIDIA driver which profile applies to the running editor's exe, and makes sure that profile has:

| Setting (Profile Inspector name) | NVAPI ID | Value |
|---|---|---|
| G-SYNC - Application Mode | `0x1194F158` | `0` Off |
| G-SYNC - Application State | `0x10A879CF` | `2` Disallow |
| G-SYNC - Application Requested State | `0x10A879AC` | `2` Disallow |

- **The profile it fixes:** whichever one the driver applies to the editor. That's a user profile naming this exe by full path (for example, one made in Profile Inspector) if there is one. Otherwise it's NVIDIA's built-in "Unity 3D" profile. Changes to a built-in profile are stored as user overrides and can be undone.
- **It only edits a profile that covers nothing but `unity.exe`.** If the matched profile also covers other programs, it leaves it alone and says so.
- **If no profile matches at all,** it creates **"Unity Editor (G-Sync Profile Guard)"** and adds the exe. The driver won't add a new `unity.exe` entry while "Unity 3D" exists, so this only happens on drivers without that profile.
- **If anything changed,** it offers to restart the editor. The driver only applies profile changes when a process starts.
- **If everything is already correct,** it does nothing and stays silent.
- **Built Unity games aren't affected,** because they have their own exe names.

Driver updates (especially clean installs) can wipe custom profiles. The startup check puts the settings back.

## Menu

**Tools > G-Sync Profile Guard**
- **Check Now** checks and fixes the profile, then shows the result.
- **Check On Startup** turns the automatic check on or off. It's on by default and stored per user in EditorPrefs.
- **Restore NVIDIA Defaults For This Editor** removes the three settings from the profile the editor uses. On "Unity 3D" that means NVIDIA's own defaults again.

## Install

There are three ways to install it.

**From the release download.** Download `com.local.gsync-profile-guard-1.0.0.tgz` from the [Releases](../../releases) page. In Unity, open **Window > Package Manager > + > Add package from tarball...** and pick the file. Keep the `.tgz` somewhere permanent, because the project references it by path.

**From git.** Use **+ > Add package from git URL...** with:

```
https://github.com/jcoker85/GSyncProfileGuard.git#v1.0.0
```

The repo is private, so this needs git on this machine signed in to GitHub (for example through Git Credential Manager).

**From a local clone.** Use **+ > Add package from disk...** and pick the clone's `package.json`, or add it to a project's `Packages/manifest.json`:

```json
"com.local.gsync-profile-guard": "file:<path to your clone>"
```

## Notes

- This package is Windows only and does nothing on machines without an NVIDIA driver.
- It talks to the driver through `nvapi64.dll` (NVAPI DRS), the same interface NVIDIA Profile Inspector uses. No extra binaries are shipped.
- Struct layouts and IDs were checked against NVIDIA driver 32.0.16.1742.
- To undo, use **Restore NVIDIA Defaults For This Editor**, turn off **Check On Startup** (or remove the package), and restart the editor.
