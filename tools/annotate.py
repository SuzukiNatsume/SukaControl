import pefile,re
p=pefile.PE(r'C:\Program Files\MI\Xiaomi G Command Center\2.1.1.413\libXiaomiGCommandCenter.dll');b=p.OPTIONAL_HEADER.ImageBase;d=p.get_memory_mapped_image()
for line in open('research/wmi-region.asm'):
 m=re.match(r'(0x[0-9a-f]+) lea \w+, \[rip \+ (0x[0-9a-f]+)\]',line)
 if m:
  a=int(m[1],16)+7+int(m[2],16)-b;s=d[a:a+85].split(b'\0')[0]
  if s: line=line.rstrip()+' ; '+str(s)+'\n'
 print(line,end='')
