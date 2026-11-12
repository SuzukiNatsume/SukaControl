import hid,json,time
x=next(x for x in hid.enumerate(0x1a86,0xe302) if x['interface_number']==2);h=hid.device();h.open_path(x['path'])
def ex(cmd,payload=[]):
 p=[0]*65;p[1:4]=[0x57,0xab,cmd];p[4:4+len(payload)]=payload;h.write(p);r=list(h.read(65,1000));r=([0]+r) if r and r[0]==0x57 else r;print(hex(cmd),bytes(r).hex());return r
original=ex(1)
try:
 p=[0]*60;p[:9]=[0,7,0,1,2,2,0,0,0x33];ex(2,p);time.sleep(.1);ex(1)
 p[:9]=[0,2,0,1,2,2,0,0,0x33];ex(2,p);time.sleep(.1);ex(1)
finally:ex(2,original[4:]);ex(1);h.close()
