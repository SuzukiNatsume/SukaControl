import pefile,capstone,sys
p=pefile.PE(sys.argv[1]);b=p.OPTIONAL_HEADER.ImageBase;d=p.get_memory_mapped_image();md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True;md.skipdata=True; ts=[int(x,16) for x in sys.argv[2:]];found=set()
for s in p.sections:
 if not s.Characteristics&0x20000000: continue
 for i in md.disasm(s.get_data(),b+s.VirtualAddress):
  if not i.id:continue
  if any((o.type==capstone.x86.X86_OP_MEM and o.mem.base==capstone.x86.X86_REG_RIP and i.address+i.size+o.mem.disp in ts) or (o.type==capstone.x86.X86_OP_IMM and o.imm in ts) for o in i.operands):
   e=next((e for e in p.DIRECTORY_ENTRY_EXCEPTION if e.struct.BeginAddress<=i.address-b<e.struct.EndAddress),None)
   if e and e.struct.BeginAddress not in found:
    found.add(e.struct.BeginAddress);print('FUNC',hex(b+e.struct.BeginAddress),hex(b+e.struct.EndAddress),'REF',hex(i.address))
    for z in md.disasm(d[e.struct.BeginAddress:e.struct.EndAddress],b+e.struct.BeginAddress):print(hex(z.address),z.mnemonic,z.op_str)
