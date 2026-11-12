import hid,json,time
x=next(x for x in hid.enumerate(0x1a86,0xe302) if x['interface_number']==2)
h=hid.device();h.open_path(x['path'])
for cmd,payload in [(1,[]),(0xc,[1]),(0xc,[0]),(0xa,[1]),(0xa,[0])]:
 p=[0]*65;p[1:4]=[0x57,0xab,cmd];p[4:4+len(payload)]=payload;h.write(p);r=bytes(h.read(65,1000));print(hex(cmd),payload,r.hex())
h.close()
