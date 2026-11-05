"""End-to-end receiver chat test. All stores, messages and API credentials are disposable."""
from pathlib import Path
import datetime
import hashlib
import hmac
import json
import os
import socket
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request
import uuid
import io
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "artifacts" / "browser"
OUT.mkdir(parents=True, exist_ok=True)
TENANT, ACCOUNT, OTHER_ACCOUNT, OTHER_TENANT = (str(uuid.uuid4()) for _ in range(4))
SECRET = b"ReceiverChatDisposableSigningSecret"
jobs, sends = [], []
audio_buffer=io.BytesIO()
with wave.open(audio_buffer,"wb") as wav:
    wav.setnchannels(1);wav.setsampwidth(2);wav.setframerate(8000);wav.writeframes(b"\0\0"*8000)
AUDIO=audio_buffer.getvalue()

class Api(BaseHTTPRequestHandler):
    def log_message(self, *_): pass
    def result(self, status, value):
        data=json.dumps(value).encode(); self.send_response(status); self.send_header("Content-Type", "application/json"); self.send_header("Content-Length", str(len(data))); self.end_headers(); self.wfile.write(data)
    def do_GET(self):
        if self.headers.get("X-Api-Key") != "wat_DisposableReceiverChatKey": return self.result(401, {})
        if self.path.startswith("/api/v1/media/"):
            data=AUDIO;status=200;content_range=None
            if self.headers.get("Range"):
                start,end=self.headers["Range"].removeprefix("bytes=").split("-");start=int(start);end=int(end) if end else len(AUDIO)-1
                data=AUDIO[start:end+1];status=206;content_range=f"bytes {start}-{end}/{len(AUDIO)}"
            self.send_response(status);self.send_header("Content-Type","audio/wav");self.send_header("Content-Length",str(len(data)))
            if content_range:self.send_header("Content-Range",content_range)
            self.end_headers();self.wfile.write(data);return
        self.result(200, {"tenantId":TENANT} if self.path == "/api/v1/me" else jobs)
    def do_POST(self):
        if self.path.startswith("/api/v1/jobs/") and self.path.endswith("/retry"):
            jobs[0]["state"]="Queued"; jobs[0]["error"]=None; return self.result(202,jobs[0])
        assert self.path == "/api/v1/messages"
        if self.headers.get("Content-Length"):
            raw=self.rfile.read(int(self.headers["Content-Length"]))
        else:
            raw=b""
            while True:
                size=int(self.rfile.readline().strip().split(b";")[0],16)
                if not size: self.rfile.readline(); break
                raw+=self.rfile.read(size); self.rfile.read(2)
        value=json.loads(raw); sends.append(value)
        job={"id":str(uuid.uuid4()),"accountId":value["accountId"],"recipient":value["recipient"],"text":value["text"],"state":"Queued","createdAt":datetime.datetime.now(datetime.timezone.utc).isoformat(),"whatsAppMessageId":None,"error":None}
        jobs.append(job); self.result(202,job)

service=ThreadingHTTPServer(("127.0.0.1",0),Api)
threading.Thread(target=service.serve_forever,daemon=True).start()
with socket.socket() as sock:
    sock.bind(("127.0.0.1",0)); port=sock.getsockname()[1]
BASE=f"http://127.0.0.1:{port}"

def request(path, value=None, headers=None):
    data=json.dumps(value).encode() if value is not None else None
    req=urllib.request.Request(BASE+path,data=data,headers=headers or {})
    try:
        with urllib.request.urlopen(req, timeout=20) as response: return response.status,json.load(response)
    except urllib.error.HTTPError as error: return error.code,None

def event(name, chat, text, message=None, outgoing=False, tenant=TENANT, account=ACCOUNT, event_type=None):
    now=datetime.datetime.now(datetime.timezone.utc).isoformat()
    return {"version":"1","eventId":str(uuid.uuid4()),"eventType":event_type or ("message.sent" if outgoing else "message.received"),"tenantId":tenant,"accountId":account,"occurredAt":now,"data":{"messageId":message or str(uuid.uuid4()),"chatJid":chat,"chatName":name,"senderJid":chat,"text":text,"fromMe":outgoing,"timestamp":now,"mediaType":""}}

def deliver(value):
    raw=json.dumps(value).encode(); timestamp=str(int(time.time()))
    signature="sha256="+hmac.new(SECRET,timestamp.encode()+b"."+raw,hashlib.sha256).hexdigest()
    req=urllib.request.Request(BASE+"/whatsapp/events",data=raw,headers={"Content-Type":"application/json","X-WaTenancy-Event-Id":value["eventId"],"X-WaTenancy-Timestamp":timestamp,"X-WaTenancy-Signature":signature})
    with urllib.request.urlopen(req,timeout=10) as response: assert response.status==200; return json.load(response)

with tempfile.TemporaryDirectory(prefix="ReceiverChatSmoke-") as store, open(OUT/"receiver-smoke.log","w",encoding="utf-8") as log:
    env=dict(os.environ,ASPNETCORE_ENVIRONMENT="Development",DOTNET_ENVIRONMENT="Development",Receiver__SigningSecret=SECRET.decode(),Receiver__DataRoot=store,Receiver__ApiKey="wat_DisposableReceiverChatKey",Receiver__ServiceBaseUrl=f"http://127.0.0.1:{service.server_port}")
    process=subprocess.Popen(["dotnet",str(ROOT/"samples/WebhookReceiver/bin/Release/net10.0/WebhookReceiver.dll"),"--urls",BASE,"--contentRoot",str(ROOT/"samples/WebhookReceiver")],env=env,stdout=log,stderr=log)
    try:
        for _ in range(80):
            try:
                if request("/health/live")[0]==200: break
            except (urllib.error.URLError,ConnectionError): time.sleep(.15)
        else: raise AssertionError("Receiver failed to start; see receiver-smoke.log")
        chat="27820000001@s.whatsapp.net"
        first=event("Amara Nkosi",chat,"Morning! Could you confirm the delivery date?")
        assert deliver(first)["duplicate"] is False
        assert deliver(first)["duplicate"] is True
        deliver(event("Amara Nkosi",chat,"Absolutely. Your order is scheduled for Friday.",outgoing=True))
        deliver(event("Amara Nkosi",chat,"Perfect, thank you! Please let me know when it’s on the way."))
        deliver(event("Daniel Jacobs","27820000002@s.whatsapp.net","Could I get a copy of the invoice, please?"))
        deliver(event("Sofia Mokoena","27820000003@s.whatsapp.net","Thanks for the quick update 🙂"))
        deliver(event("Operations team","120363000000000000@g.us","The morning deliveries have all been collected."))
        deliver(event("Thabo Dlamini","27820000004@s.whatsapp.net","What time do you close today?"))
        deliver(event("Amara · second number",chat,"This is a separate number’s conversation.",account=OTHER_ACCOUNT))
        deliver(event("Another company",chat,"This conversation belongs to another company.",tenant=OTHER_TENANT))
        assert request("/inbox/stats")[1]["threads"]==7
        assert request("/inbox/stats")[1]["messages"]==9
        assert request("/inbox/config",headers={"X-Forwarded-For":"203.0.113.1"})[0]==404
        bad=event("Bad",chat,"Unsigned"); assert request("/whatsapp/events",bad,{"Content-Type":"application/json"})[0]==401
        assert request("/inbox/reply",{"tenantId":TENANT,"accountId":ACCOUNT,"chatJid":chat,"text":"No CSRF", "requestId":str(uuid.uuid4())},{"Content-Type":"application/json"})[0]==400
        errors=[]
        with sync_playwright() as p:
            browser=p.chromium.launch(channel="msedge",headless=True)
            page=browser.new_page(viewport={"width":1440,"height":950})
            # Browser Link expects an injected jQuery-style $ with noConflict.
            page.add_init_script("window.$ = window.jQuery = Object.assign(function jqueryStub(){}, {noConflict(){return window.jQuery;}});")
            page.on("pageerror",lambda e:errors.append(str(e)))
            page.goto(BASE)
            assert page.evaluate("typeof $.noConflict === 'function' && $ === window.jQuery"), "Chat script collided with Browser Link's global $"
            expect(page.get_by_role("button",name="Open conversation with Amara Nkosi",exact=True)).to_be_visible()
            page.get_by_role("button",name="Open conversation with Amara Nkosi",exact=True).click()
            expect(page.get_by_role("log")).to_contain_text("confirm the delivery date")
            expect(page.get_by_role("log")).not_to_contain_text("separate number")
            page.get_by_label("Message",exact=True).fill("I’ll send the tracking details as soon as it’s dispatched.")
            page.get_by_role("button",name="Send message",exact=True).click()
            expect(page.get_by_role("log")).to_contain_text("tracking details")
            expect(page.get_by_role("log")).to_contain_text("Queued")
            assert len(sends)==1 and sends[0]["accountId"]==ACCOUNT and sends[0]["recipient"]==chat
            jobs[0]["state"]="Read"
            expect(page.get_by_role("log").locator('[title="Read"]')).to_be_visible(timeout=10000)
            page.screenshot(path=str(OUT/"receiver-chat.png"),full_page=True)
            deliver({"version":"1","eventId":str(uuid.uuid4()),"eventType":"message.dispatch","tenantId":TENANT,"accountId":ACCOUNT,"occurredAt":datetime.datetime.now(datetime.timezone.utc).isoformat(),"data":{"jobId":jobs[0]["id"],"messageId":"","state":"Failed","error":"Fixture failure; ready for manual retry"}})
            expect(page.get_by_role("button",name="Retry send",exact=True)).to_be_visible(timeout=10000)
            page.get_by_role("button",name="Retry send",exact=True).click()
            expect(page.get_by_role("log")).to_contain_text("Queued")
            jobs[0]["state"]="Read"
            expect(page.get_by_role("log").locator('[title="Read"]')).to_be_visible(timeout=10000)
            page.get_by_role("button",name="View event payload").first.click()
            expect(page.locator("#payload")).to_contain_text("message.received")
            page.get_by_role("button",name="Close payload").click()
            page.get_by_label("Search chats").fill("Daniel")
            expect(page.get_by_role("button",name="Open conversation with Daniel Jacobs",exact=True)).to_be_visible()
            expect(page.get_by_role("button",name="Open conversation with Amara Nkosi",exact=True)).not_to_be_visible()
            page.get_by_label("Search chats").fill("")
            page.get_by_role("button",name="Open conversation with Another company",exact=True).click()
            page.get_by_label("Message",exact=True).fill("Must not be sent")
            page.get_by_role("button",name="Send message",exact=True).click()
            expect(page.locator("#reply-notice")).to_contain_text("different company")
            assert len(sends)==1
            page.get_by_role("button",name="Open conversation with Amara Nkosi",exact=True).click()
            page.set_viewport_size({"width":390,"height":844})
            page.screenshot(path=str(OUT/"receiver-chat-mobile.png"),full_page=True)
            assert page.evaluate("document.documentElement.scrollWidth <= innerWidth")
            page.get_by_role("button",name="Back to contacts").click()
            expect(page.get_by_role("button",name="Open conversation with Daniel Jacobs",exact=True)).to_be_visible()
            page.set_viewport_size({"width":1440,"height":950})
            page.get_by_role("button",name="Received events",exact=True).click()
            expect(page.locator("#event-items")).to_contain_text("message.received")
            page.screenshot(path=str(OUT/"receiver-events.png"),full_page=True)
            # Edits and deletes update the displayed message rather than adding duplicates.
            updated=event("Amara Nkosi",chat,"Edited delivery question",message=first["data"]["messageId"],event_type="message.updated")
            deliver(updated)
            deleted=event("Amara Nkosi",chat,"",message=first["data"]["messageId"],event_type="message.deleted")
            deliver(deleted)
            page.get_by_role("button",name="Open conversation with Amara Nkosi",exact=True).click()
            expect(page.get_by_role("log")).to_contain_text("This message was deleted",timeout=10000)
            assert request("/inbox/stats")[1]["messages"]==8
            # Ready events may precede message events. The later Pending snapshot must not downgrade playback.
            voice=event("Amara Nkosi",chat,"",message="VOICE-FIXTURE")
            media_id=str(uuid.uuid4());metadata={"id":media_id,"state":"Ready","contentType":"audio/wav","name":"voice-note.wav","length":len(AUDIO)}
            ready=event("Amara Nkosi",chat,"",message="VOICE-FIXTURE",event_type="message.media.ready")
            ready["data"]["attachment"]=metadata;deliver(ready)
            voice["data"]["mediaType"]="audio";voice["data"]["attachment"]={**metadata,"state":"Pending"};deliver(voice)
            expect(page.locator("audio.voice-note")).to_be_visible(timeout=10000)
            player=page.locator("audio.voice-note");player.evaluate("async el=>{el.load();await new Promise((ok,fail)=>{el.onloadedmetadata=ok;el.onerror=fail;});}")
            assert player.evaluate("el=>el.duration")==1
            with urllib.request.urlopen(urllib.request.Request(BASE+"/inbox/media/"+media_id,headers={"Range":"bytes=0-3"})) as audio:
                assert audio.status==206 and audio.read()==b"RIFF"
            page.screenshot(path=str(OUT/"receiver-voice-note.png"),full_page=True)
            deliver(event("Amara Nkosi",chat,"",message="VOICE-FIXTURE",event_type="message.deleted"))
            expect(page.locator("audio.voice-note")).to_have_count(0,timeout=10000)
            try:urllib.request.urlopen(BASE+"/inbox/media/"+media_id);raise AssertionError("Deleted media remained accessible")
            except urllib.error.HTTPError as error:assert error.code==404
            # Realistic volume must not push either scroller or the composer outside the viewport.
            for index in range(45): deliver(event("Contact "+str(index),"2782111"+str(index).zfill(4)+"@s.whatsapp.net","Contact list scrolling fixture"))
            for index in range(65): deliver(event("Amara Nkosi",chat,"Conversation scrolling fixture "+str(index)))
            page.reload()
            page.get_by_role("button",name="Open conversation with Amara Nkosi",exact=True).click()
            expect(page.get_by_role("log")).to_contain_text("Conversation scrolling fixture 64")
            assert page.locator("#threads").evaluate("el => el.scrollHeight > el.clientHeight")
            assert page.locator("#messages").evaluate("el => el.scrollHeight > el.clientHeight")
            assert page.locator("#composer").evaluate("el => el.getBoundingClientRect().bottom <= innerHeight")
            page.locator("#threads").evaluate("el => el.scrollTop = el.scrollHeight")
            assert page.locator("#threads").evaluate("el => el.scrollTop > 0")
            page.locator("#messages").evaluate("el => el.scrollTop = 0")
            assert page.locator("#messages").evaluate("el => el.scrollTop === 0")
            page.get_by_role("button",name="Load earlier messages").click()
            expect(page.get_by_role("log")).to_contain_text("This message was deleted")
            page.set_viewport_size({"width":390,"height":844})
            assert page.locator("#composer").evaluate("el => el.getBoundingClientRect().bottom <= innerHeight")
            assert page.locator("#messages").evaluate("el => el.scrollHeight > el.clientHeight")
            assert errors==[],errors
            browser.close()
        print(json.dumps({"receiver_chat":"passed","tenancy":"company and account isolation verified","replies":"fake API only","page_errors":errors}))
    finally:
        process.terminate(); process.wait(timeout=15); service.shutdown()
