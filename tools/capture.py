import frida,time,json
s=frida.get_local_device().attach(28760)
js="""
const p=Process.getModuleByName('kernel32.dll').getExportByName('WriteFile');
Interceptor.attach(p,{onEnter(args){let n=args[2].toInt32();if(n===65){try{let a=new Uint8Array(args[1].readByteArray(n));if(a[1]===0x57&&a[2]===0xab)send({type:'hid',data:Array.from(a)});}catch(e){}}}});
send({type:'ready'});
"""
script=s.create_script(js)
f=open('research/capture.jsonl','a',buffering=1)
script.on('message',lambda message,data:f.write(json.dumps(message)+'\n'))
script.load()
time.sleep(180)
s.detach()
