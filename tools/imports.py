import pefile,capstone,sys,json
p=pefile.PE(sys.argv[1]);b=p.OPTIONAL_HEADER.ImageBase;d=p.get_memory_mapped_image();md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True;md.skipdata=True
imports={i.address:i.name.decode() if i.name else str(i.ordinal) for x in p.DIRECTORY_ENTRY_IMPORT for i in x.imports}
for addr,name in imports.items():
 if any(t.lower() in name.lower() for t in sys.argv[2:]): print('IMPORT',hex(addr),name)
for s in p.sections:
 if not s.Characteristics&0x20000000:continue
 for i in md.disasm(s.get_data(),b+s.VirtualAddress):
  if not i.id:continue
  for o in i.operands:
   if o.type==capstone.x86.X86_OP_MEM and o.mem.base==capstone.x86.X86_REG_RIP:
    a=i.address+i.size+o.mem.disp
    if a in imports and any(t.lower() in imports[a].lower() for t in sys.argv[2:]):
     e=next((e for e in p.DIRECTORY_ENTRY_EXCEPTION if e.struct.BeginAddress<=i.address-b<e.struct.EndAddress),None)
     if e:
      print('\nFUNCTION',imports[a],hex(b+e.struct.BeginAddress),hex(b+e.struct.EndAddress))
      for z in md.disasm(d[e.struct.BeginAddress:e.struct.EndAddress],b+e.struct.BeginAddress):print(hex(z.address),z.mnemonic,z.op_str)
