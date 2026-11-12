"""Create real user-owned state and change only the current user's ICC association."""
import ctypes, json, os, pathlib, winreg
root = pathlib.Path(__file__).resolve().parents[1]
key_path = r'Software\Microsoft\Windows NT\CurrentVersion\ICM\ProfileAssociations\Display\{4d36e96e-e325-11ce-bfc1-08002be10318}\0008'
color_key = r'\Registry\Machine\System\CurrentControlSet\Control\Class\{4d36e96e-e325-11ce-bfc1-08002be10318}\0008'
with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key_path) as key:
    profiles = winreg.QueryValueEx(key, 'ICMProfile')[0]
    per_user = winreg.QueryValueEx(key, 'UsePerUserProfiles')[0]
display_id = json.loads((root / 'research/independent-diagnostic.json').read_text(encoding='utf-8-sig'))['displays'][0]['DeviceId']
snapshot = dict(ColorKey=color_key, Profiles=profiles, PerUser=bool(per_user), Existed=True, MissingParents=[])
(root / 'research/uninstall-color-before.json').write_text(json.dumps(snapshot, indent=2), encoding='utf-8')
settings = dict(F7Action='showApp', CaptureF7=True, Warmth=35, OriginalProfiles={display_id:profiles[-1]}, ColorBackups={display_id:snapshot})
local = pathlib.Path(os.environ['LOCALAPPDATA']) / 'MiControl'
local.mkdir(exist_ok=True)
(local / 'settings.json').write_text(json.dumps(settings, indent=2), encoding='utf-8')
m = ctypes.WinDLL('mscms', use_last_error=True)
m.WcsSetUsePerUserProfiles.argtypes = [ctypes.c_wchar_p, ctypes.c_uint, ctypes.c_bool]
m.WcsAssociateColorProfileWithDevice.argtypes = [ctypes.c_uint, ctypes.c_wchar_p, ctypes.c_wchar_p]
m.WcsSetDefaultColorProfile.argtypes = [ctypes.c_uint, ctypes.c_wchar_p, ctypes.c_uint, ctypes.c_uint, ctypes.c_uint, ctypes.c_wchar_p]
assert m.WcsSetUsePerUserProfiles(color_key, 0x6d6e7472, True), ctypes.get_last_error()
assert m.WcsAssociateColorProfileWithDevice(1, 'xiaomi_sRGB_base.icc', color_key), ctypes.get_last_error()
assert m.WcsSetDefaultColorProfile(1, color_key, 0, 0, 0, 'xiaomi_sRGB_base.icc'), ctypes.get_last_error()
print('Saved original associations and selected Xiaomi sRGB for the uninstall restoration test.')
