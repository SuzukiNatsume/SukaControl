import pefile,re,struct
p=pefile.PE(r'C:\Program Files\MI\Xiaomi G Command Center\2.1.1.413\libXiaomiGCommandCenter.dll');d=p.get_memory_mapped_image();b=p.OPTIONAL_HEADER.ImageBase
for m in re.finditer(rb'\.\?AV[^\0]{1,180}bitland_sync_operation[^\0]*',d):
 td=m.start()-16;needle=struct.pack('<I',td);start=0
 while (i:=d.find(needle,start))>=0:
  start=i+1;col=i-12
  if col<0 or struct.unpack_from('<I',d,col)[0]!=1:continue
  if struct.unpack_from('<I',d,col+20)[0]!=col:continue
  j=d.find(struct.pack('<Q',b+col))
  if j<0:continue
  print(m.group().decode(),hex(b+j+8),*[hex(x) for x in struct.unpack_from('<6Q',d,j+8)])
