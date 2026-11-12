import pefile,capstone,sys
p=pefile.PE(sys.argv[1]); base=p.OPTIONAL_HEADER.ImageBase; data=p.get_memory_mapped_image(); md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True;md.skipdata=True
if sys.argv[2]=='exports':
 for s in p.DIRECTORY_ENTRY_EXPORT.symbols: print(hex(base+s.address),s.name)
 sys.exit()
if sys.argv[2].startswith('0x'):
 lo=int(sys.argv[2],16)-base;hi=lo+(int(sys.argv[3]) if len(sys.argv)>3 else 800)
 for ins in md.disasm(data[lo:hi],base+lo): print(hex(ins.address),ins.mnemonic,ins.op_str)
 sys.exit()
wide='--wide' in sys.argv;term=sys.argv[2].encode('utf-16le' if wide else 'utf8'); targets=[];start=0
while (i:=data.find(term,start))>=0:
 j=i
 while j>0 and (data[j-2:j]!=b'\0\0' if wide else data[j-1]!=0): j-=2 if wide else 1
 targets.append(base+j);start=i+1
print('TARGETS',*[hex(x) for x in targets])
refs=[]
for s in p.sections:
 if s.Characteristics&0x20000000:
  for ins in md.disasm(s.get_data(),base+s.VirtualAddress):
   if ins.id and any(o.type==capstone.x86.X86_OP_MEM and o.mem.base==capstone.x86.X86_REG_RIP and ins.address+ins.size+o.mem.disp in targets for o in ins.operands): refs.append(ins.address)
for ref in refs[:12]:
 rva=ref-base; entry=next((e for e in p.DIRECTORY_ENTRY_EXCEPTION if e.struct.BeginAddress<=rva<e.struct.EndAddress),None)
 lo=entry.struct.BeginAddress if entry else rva-80;hi=entry.struct.EndAddress if entry else rva+150
 print('REF',hex(ref),'FUNC',hex(base+lo),hex(base+hi))
 for ins in md.disasm(data[lo:min(hi,lo+12000)],base+lo):print(hex(ins.address),ins.mnemonic,ins.op_str)
