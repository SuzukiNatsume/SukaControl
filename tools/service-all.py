import re
p=r'C:\Program Files (x86)\Timi Personal Computing\MiService\4.1.1.188\MiDeviceService\MiDeviceServiceEntry.dll'
b=open(p,'rb').read()
for s in re.findall(rb'[\x20-\x7e]{4,}',b):
 if any(t.lower() in s.lower() for t in [b'Interface',b'OverDriver',b'wmiclass',b'root\\',b'GetFan',b'GetCpu']):print(s.decode())
for s in re.findall(r'[\x20-\x7e]{4,}',b.decode('utf-16le',errors='ignore')):
 if any(t.lower() in s.lower() for t in ['Interface','OverDriver','root\\','GetFan','GetCpu']):print('WIDE',s)
