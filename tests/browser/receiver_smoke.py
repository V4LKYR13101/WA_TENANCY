"""Checks the local receiver sample using a disposable signing secret/store."""
import hashlib
import hmac
import json
import time
import urllib.error
import urllib.request
import uuid

URL = "http://127.0.0.1:5190/whatsapp/events"
SECRET = b"DisposableReceiverTestSecret"
event_id = str(uuid.uuid4())
body = json.dumps({"version": "1", "eventId": event_id, "tenantId": str(uuid.uuid4()), "eventType": "webhook.test", "data": {"test": True}}).encode()

def post(raw, timestamp=None, signature=None):
    timestamp = str(int(time.time()) if timestamp is None else timestamp)
    signature = signature or "sha256=" + hmac.new(SECRET, timestamp.encode() + b"." + raw, hashlib.sha256).hexdigest()
    request = urllib.request.Request(URL, data=raw, headers={"Content-Type": "application/json", "X-WaTenancy-Event-Id": event_id, "X-WaTenancy-Timestamp": timestamp, "X-WaTenancy-Signature": signature})
    try:
        with urllib.request.urlopen(request, timeout=5) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        return error.code, None

assert post(body) == (200, {"acknowledged": True, "duplicate": False})
assert post(body) == (200, {"acknowledged": True, "duplicate": True})
assert post(body, signature="sha256=" + "0" * 64)[0] == 401
assert post(body, timestamp=int(time.time()) - 301)[0] == 401
assert post(b"not json")[0] == 400
malformed = json.dumps({"eventId": 4, "tenantId": str(uuid.uuid4()), "eventType": "test"}).encode()
assert post(malformed)[0] == 400
print(json.dumps({"receiver": "passed", "checks": 6}))
