const fs=require('fs');
const files=process.argv.slice(2);
for(const f of files){const b=fs.readFileSync(f); const a=[...(b.toString('latin1').match(/[\x20-\x7e]{5,}/g)||[]),...(b.toString('utf16le').match(/[\x20-\x7e]{5,}/g)||[])]; console.log('\nFILE '+f); console.log(a.filter(s=>/light|keyboard|overdrive|response|WMI|MiInterface|Performance|Eye|Gamma|RGB|Led|fan|temperature/i.test(s)).join('\n'));}
