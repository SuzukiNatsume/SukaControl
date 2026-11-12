"""Read-only focused AML disassembly for local OEM WMI methods (no ACPI execution)."""
from pathlib import Path
import re,struct
files=list(Path('research/acpi').glob('*.aml'))
methods={}
def pkg(b,p):
 n=b[p]>>6
 return ((b[p]&15)|sum(b[p+i+1]<<(4+8*i) for i in range(n)),n+1) if n else (b[p]&63,1)
def name(b,p):
 start=p;prefix=''
 while b[p] in (0x5c,0x5e):prefix+=chr(b[p]);p+=1
 count=1
 if b[p]==0x2e:count=2;p+=1
 elif b[p]==0x2f:count=b[p+1];p+=2
 names=[]
 for _ in range(count):
  s=b[p:p+4]
  if not re.fullmatch(rb'[A-Z_][A-Z0-9_]{3}',s):raise ValueError('not a name')
  names.append(s.decode());p+=4
 return prefix+'.'.join(names),p
definitions=[]
for f in files:
 b=f.read_bytes()
 for p in range(36,len(b)-8):
  try:
   if b[p]==0x15:
    s,q=name(b,p+1)
    if b[q]==8:methods[s.split('.')[-1]]=b[q+1]
   if b[p]!=0x14:continue
   length,n=pkg(b,p+1);s,q=name(b,p+1+n)
   if p+1+length>len(b):continue
   methods[s.split('.')[-1]]=b[q]&7
   if f.name.find('SSD9_XMCC')>=0:definitions.append((f,s,q+1,p+1+length))
  except (ValueError,IndexError):pass
ops={0x70:('Store',2),0x71:('RefOf',1),0x72:('Add',3),0x73:('Concat',3),0x74:('Subtract',3),0x75:('Increment',1),0x76:('Decrement',1),0x77:('Multiply',3),0x78:('Divide',4),0x79:('ShiftLeft',3),0x7a:('ShiftRight',3),0x7b:('And',3),0x7d:('Or',3),0x7f:('Xor',3),0x80:('Not',2),0x81:('FindSetLeftBit',2),0x83:('DerefOf',1),0x86:('Notify',2),0x87:('SizeOf',1),0x88:('Index',3),0x8a:('CreateDWordField',3),0x8b:('CreateWordField',3),0x8c:('CreateByteField',3),0x8f:('CreateQWordField',3),0x90:('LAnd',2),0x91:('LOr',2),0x92:('LNot',1),0x93:('LEqual',2),0x94:('LGreater',2),0x95:('LLess',2),0x96:('ToBuffer',2),0x99:('ToInteger',2),0x9d:('CopyObject',2),0xa4:('Return',1)}
class Parser:
 def __init__(self,b):self.b=b
 def expr(self,p):
  b=self.b;o=b[p];p+=1
  if o in (0,1,255):return str({0:0,1:1,255:'Ones'}[o]),p
  if 0x60<=o<=0x67:return 'Local'+str(o-0x60),p
  if 0x68<=o<=0x6e:return 'Arg'+str(o-0x68),p
  if o in (0xa,0xb,0xc,0xe):n={0xa:1,0xb:2,0xc:4,0xe:8}[o];return hex(int.from_bytes(b[p:p+n],'little')),p+n
  if o==0xd:q=b.index(0,p);return repr(b[p:q].decode(errors='replace')),q+1
  if o==8:
   s,p=name(b,p);v,p=self.expr(p);return 'Name('+s+', '+v+')',p
  if o in (0x11,0x12,0x13):
   length,n=pkg(b,p);end=p+length;v=b[p+n:end].hex();return ('Buffer' if o==0x11 else 'Package')+'('+v+')',end
  if o==0x5b:
   x=b[p];p+=1;nm,n={0x21:('Stall',1),0x22:('Sleep',1),0x23:('Acquire',2),0x27:('Release',1),0x31:('Debug',0)}.get(x,('Ext'+hex(x),0));args=[]
   for _ in range(n):v,p=self.expr(p);args.append(v)
   return nm+'('+', '.join(args)+')',p
  if o in ops:
   nm,n=ops[o];args=[]
   for _ in range(n):v,p=self.expr(p);args.append(v)
   return nm+'('+', '.join(args)+')',p
  try:
   s,p=name(b,p-1);n=methods.get(s.split('.')[-1],0);args=[]
   for _ in range(n):v,p=self.expr(p);args.append(v)
   return s+('('+', '.join(args)+')' if n else ''),p
  except ValueError:return 'Opcode_'+hex(o),p
 def body(self,p,end,indent=0):
  lines=[]
  while p<end:
   try:
    o=self.b[p]
    if o in (0xa0,0xa1,0xa2):
     length,n=pkg(self.b,p+1);finish=p+1+length;q=p+1+n
     if o==0xa1:condition='Else'
     else:condition,q=self.expr(q);condition=('If' if o==0xa0 else 'While')+' ('+condition+')'
     lines.append(' '*indent+condition+' {');lines+=self.body(q,finish,indent+2);lines.append(' '*indent+'}');p=finish
    elif o==0xa5:lines.append(' '*indent+'Break');p+=1
    elif o==0xa3:p+=1
    else:v,p=self.expr(p);lines.append(' '*indent+v)
   except (IndexError,ValueError) as e:lines.append(' '*indent+'Parse stopped: '+str(e));break
  return lines
result=[]
for f,s,start,end in definitions:
 if s not in ('WMAA','_WED'):continue
 result+=['Method '+s+' '+hex(start)+'..'+hex(end)]+Parser(f.read_bytes()).body(start,end)
Path('research/acpi/oem-wmi-inspection.txt').write_text('\n'.join(result), encoding='utf-8')
print('\n'.join(result[:55]))
