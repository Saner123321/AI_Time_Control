using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// A static loopback-only fixture. No credentials, downloads, arbitrary files,
// service commands, CORS, state-changing endpoints or browser-control interface.
internal sealed class ManualViewFixtureServer:IDisposable {
    readonly TcpListener listener=new TcpListener(IPAddress.Loopback,0);
    readonly string path="/"+Guid.NewGuid().ToString("N");bool disposed;
    internal string Url {get;private set;}
    internal ManualViewFixtureServer(){listener.Start(4);Url="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+path;Task.Run(()=>Accept());}
    async Task Accept(){while(!disposed){TcpClient client;try{client=await listener.AcceptTcpClientAsync();}catch{return;}await Task.Run(()=>Respond(client));}}
    void Respond(TcpClient client){using(client){
        try{client.ReceiveTimeout=900;client.SendTimeout=900;using(var stream=client.GetStream()){
            var data=new byte[2048];int length=0;
            while(length<data.Length){int read=stream.Read(data,length,data.Length-length);if(read==0)return;length+=read;if(Encoding.ASCII.GetString(data,0,length).Contains("\r\n\r\n"))break;}
            string request=Encoding.ASCII.GetString(data,0,length);string first=request.Split(new[]{"\r\n"},StringSplitOptions.None)[0];bool ok=first=="GET "+path+" HTTP/1.1"||first=="GET "+path+" HTTP/1.0";
            byte[] body=Encoding.UTF8.GetBytes(ok?Page:"Not found");byte[] header=Encoding.ASCII.GetBytes("HTTP/1.1 "+(ok?"200 OK":"404 Not Found")+"\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: "+body.Length+"\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; frame-ancestors 'none'\r\n\r\n");stream.Write(header,0,header.Length);stream.Write(body,0,body.Length);
        }}catch(IOException){}catch(SocketException){}catch(ObjectDisposedException){}
    }}
    public void Dispose(){disposed=true;listener.Stop();}
    const string Page=@"<!doctype html><html lang='zh-CN'><meta charset='utf-8'><title>AICT 本地观看验证</title>
<style>body{background:#edf4f5;color:#123e43;font:20px system-ui;margin:40px}main{max-width:820px;margin:auto}.video{height:240px;background:linear-gradient(120deg,#1a7d83,#173f63);color:white;border-radius:20px;display:grid;place-items:center;margin:24px 0;font-size:30px}button{font:inherit;padding:15px;background:#167d83;color:white;border:0;border-radius:10px}#counter{font-size:32px}</style>
<main><h1>AICT 本地观看验证</h1><p>这是本地测试页面，没有外部请求。屏幕上的遮挡不应改变这里的内容。</p><div class='video'>模拟视频观看区域</div><button id='increase'>增加测试计数</button><p>AI点击测试计数：<strong id='counter'>0</strong></p><p>验收：你看不到本页时，AI仍能读到标题、截图中看到本页，并把计数从0加到1。</p></main><script>document.getElementById('increase').addEventListener('click',()=>{let e=document.getElementById('counter');e.textContent=String(Number(e.textContent)+1);});</script></html>";
}
