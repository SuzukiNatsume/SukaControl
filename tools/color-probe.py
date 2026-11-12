import ctypes as c,json
from ctypes import wintypes as w
class D(c.Structure):_fields_=[('cb',w.DWORD),('name',w.WCHAR*32),('text',w.WCHAR*128),('flags',w.DWORD),('id',w.WCHAR*128),('key',w.WCHAR*128)]
u=c.WinDLL('user32');m=c.WinDLL('mscms',use_last_error=True)
r=[]
for i in range(20):
 a=D();a.cb=c.sizeof(a)
 if not u.EnumDisplayDevicesW(None,i,c.byref(a),0):break
 if not a.flags&1:continue
 for j in range(10):
  d=D();d.cb=c.sizeof(d)
  if not u.EnumDisplayDevicesW(a.name,j,c.byref(d),1):break
  o={k:getattr(d,k) for k in ['name','id','key','text','flags']};o['gdi']=a.name;o['queries']=[]
  for name in [a.name,d.name,d.id,d.key]:
   for scope in [0,1]:
    b=c.create_unicode_buffer(4096);ok=m.WcsGetDefaultColorProfile(scope,name,0,0,0,c.sizeof(b),b);o['queries'].append([name,scope,ok,c.get_last_error(),b.value])
  r.append(o)
print(json.dumps(r,indent=2))
