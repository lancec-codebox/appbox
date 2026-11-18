import json,http.server
N={'n':0}
class H(http.server.BaseHTTPRequestHandler):
    def log_message(self,*a): pass
    def send(self,code,body,ct='application/json',extra={}):
        b=body if isinstance(body,bytes) else json.dumps(body).encode()
        self.send_response(code); self.send_header('Content-Type',ct); self.send_header('Content-Length',str(len(b)))
        for k,v in extra.items(): self.send_header(k,v)
        self.end_headers(); self.wfile.write(b)
    def do_GET(self):
        p=self.path
        if p=='/v1/status': return self.send(200,{'app':'medusa','version':'1.9.1','ytDlp':'2026.11.01'})
        if p=='/v1/jobs/j1': N['n']+=1; return self.send(200,{'id':'j1','state':'done' if N['n']>2 else 'downloading','progress':0.5})
        if p=='/v1/jobs/j1/result': return self.send(200,{'mediaId':'m1','title':'Me at the zoo','durationSec':19,'extractor':'youtube','width':640,'height':360,'videoId':'jNQXAC9IVRw','video':{'path':'/v1/media/m1/video','contentType':'video/mp4','bytes':2000000},'subtitles':None,'tracks':[],'sync':[]})
        if p=='/v1/media/m1': return self.do_GET2()
        if p=='/v1/media/m1/video':
            r=self.headers.get('Range'); return self.send(206,b'x'*1048576,'video/mp4',{'Content-Range':'bytes 0-1048575/2000000','Accept-Ranges':'bytes'})
        self.send(404,{'error':'not found'})
    def do_GET2(self): self.send(200,{'mediaId':'m1','title':'Me at the zoo','durationSec':19,'extractor':'youtube','width':640,'height':360,'video':{'contentType':'video/mp4','bytes':2000000}})
    def do_POST(self):
        l=int(self.headers.get('Content-Length',0)); self.rfile.read(l)
        self.send(200,{'id':'j1'})
http.server.HTTPServer(('127.0.0.1',38917),H).serve_forever()
